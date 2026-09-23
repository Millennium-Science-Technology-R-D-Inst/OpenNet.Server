using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenNet.Server.Contracts;
using OpenNet.Server.Domain;
using OpenNet.Server.Infrastructure;
using OpenNet.Server.Options;

namespace OpenNet.Server.Application;

public sealed class ContentDirectoryConflictException(string message) : Exception(message);

public sealed class ContentDirectoryService(
    ContentDirectoryDbContext dbContext,
    IOptions<ContentDirectoryOptions> options)
{
    private readonly ContentDirectoryOptions directoryOptions = options.Value;

    private sealed record NormalizedIdentity(
        ContentIdentityAlgorithm Algorithm,
        string DigestHex);

    private sealed record NormalizedAnnouncement(
        long Size,
        IReadOnlyList<NormalizedIdentity> Identities);

    public async Task<RegisterContentNodeResponse> RegisterAsync(
        RegisterContentNodeRequest request,
        IPAddress observedRemoteAddress,
        CancellationToken cancellationToken)
    {
        if (request.Contents.Count > directoryOptions.MaxInventoryItems)
        {
            throw new ArgumentException(
                $"Inventory contains {request.Contents.Count} items; the configured limit is {directoryOptions.MaxInventoryItems}.",
                nameof(request.Contents));
        }

        if (request.RegistrationId == Guid.Empty)
        {
            throw new ArgumentException(
                "RegistrationId must be a non-empty UUID.",
                nameof(request.RegistrationId));
        }

        string nodeId = request.NodeId.Trim();
        if (nodeId.Length < 8 || nodeId.Length > 128)
        {
            throw new ArgumentException("NodeId length must be between 8 and 128 characters.", nameof(request.NodeId));
        }

        List<NormalizedAnnouncement> announcements =
            request.Contents.Select(NormalizeAnnouncement).ToList();
        EnsureRequestAliasesAreUnambiguous(announcements);

        IPAddress observedAddress = ContentIdentityRules.NormalizeObservedAddress(observedRemoteAddress);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset leaseExpiresUtc = now.AddSeconds(directoryOptions.LeaseLifetimeSeconds);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        ContentNode? node = await dbContext.ContentNodes
            .SingleOrDefaultAsync(item => item.NodeId == nodeId, cancellationToken);

        if (node is null)
        {
            node = new ContentNode
            {
                NodeId = nodeId,
                CreatedUtc = now
            };
            dbContext.ContentNodes.Add(node);
        }
        else
        {
            if (request.Generation < node.Generation)
            {
                throw new ContentDirectoryConflictException(
                    $"Inventory generation {request.Generation} is older than the server generation {node.Generation}.");
            }

            if (request.Generation == node.Generation
                && node.LastRegistrationId != request.RegistrationId)
            {
                throw new ContentDirectoryConflictException(
                    "The inventory generation is already committed by a different registration request.");
            }

            if (request.Generation > node.Generation
                && node.LeaseExpiresUtc > now
                && request.PreviousLeaseId != node.LeaseId)
            {
                throw new ContentDirectoryConflictException(
                    "The node already has an active lease. Supply PreviousLeaseId to replace its inventory.");
            }
        }

        node.Generation = request.Generation;
        node.LastRegistrationId = request.RegistrationId;
        node.LeaseId = Guid.NewGuid();
        node.LeaseExpiresUtc = leaseExpiresUtc;
        node.LastSeenUtc = now;

        List<ContentNodeEndpoint> oldEndpoints = await dbContext.ContentNodeEndpoints
            .Where(endpoint => endpoint.NodeId == nodeId)
            .ToListAsync(cancellationToken);
        dbContext.ContentNodeEndpoints.RemoveRange(oldEndpoints);

        List<ContentPresence> oldPresences = await dbContext.ContentPresences
            .Where(presence => presence.NodeId == nodeId)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, ContentPresence> previousPresences =
            oldPresences.ToDictionary(presence => presence.ContentId);

        bool observedIpv6 = observedAddress.AddressFamily
            == System.Net.Sockets.AddressFamily.InterNetworkV6;
        PeerAddressFamilyContract observedFamily = observedIpv6
            ? PeerAddressFamilyContract.Ipv6
            : PeerAddressFamilyContract.Ipv4;

        foreach (PeerEndpointRequest endpoint in request.Endpoints
            .Where(endpoint => endpoint.AddressFamily == observedFamily)
            .DistinctBy(endpoint => (endpoint.Transport, endpoint.Port)))
        {
            dbContext.ContentNodeEndpoints.Add(new ContentNodeEndpoint
            {
                NodeId = nodeId,
                Address = observedAddress.ToString(),
                IsIpv6 = observedIpv6,
                Port = endpoint.Port,
                Transport = endpoint.Transport switch
                {
                    PeerTransportContract.Tcp => ContentPeerTransport.Tcp,
                    PeerTransportContract.Utp => ContentPeerTransport.Utp,
                    _ => throw new ArgumentOutOfRangeException(nameof(endpoint.Transport))
                }
            });
        }

        Dictionary<(ContentIdentityAlgorithm Algorithm, string Digest), Guid> existingIdentityMap =
            await LoadExistingIdentityMapAsync(announcements, cancellationToken);

        var registeredContentIds = new HashSet<Guid>();
        foreach (NormalizedAnnouncement announcement in announcements)
        {
            Guid[] matchedContentIds = announcement.Identities
                .Select(identity => existingIdentityMap.TryGetValue(
                    (identity.Algorithm, identity.DigestHex),
                    out Guid contentId)
                    ? contentId
                    : Guid.Empty)
                .Where(contentId => contentId != Guid.Empty)
                .Distinct()
                .ToArray();

            if (matchedContentIds.Length > 1)
            {
                throw new ContentDirectoryConflictException(
                    "The supplied identities already resolve to different server content objects.");
            }

            ContentObject content;
            if (matchedContentIds.Length == 1)
            {
                content = await dbContext.Contents.SingleAsync(
                    item => item.Id == matchedContentIds[0],
                    cancellationToken);
                if (content.Size != announcement.Size)
                {
                    throw new ContentDirectoryConflictException(
                        "An existing content identity was registered with a different file size.");
                }
            }
            else
            {
                content = new ContentObject
                {
                    Id = Guid.NewGuid(),
                    Size = announcement.Size,
                    CreatedUtc = now
                };
                dbContext.Contents.Add(content);
            }

            foreach (NormalizedIdentity identity in announcement.Identities)
            {
                var key = (identity.Algorithm, identity.DigestHex);
                if (!existingIdentityMap.ContainsKey(key))
                {
                    dbContext.ContentIdentities.Add(new ContentIdentityRecord
                    {
                        ContentId = content.Id,
                        Algorithm = identity.Algorithm,
                        DigestHex = identity.DigestHex
                    });
                    existingIdentityMap[key] = content.Id;
                }
            }

            if (registeredContentIds.Add(content.Id))
            {
                if (previousPresences.Remove(content.Id, out ContentPresence? presence))
                {
                    // Inventory refreshes must not tear down a seed session that
                    // was just woken for the same content. Preserve the demand/
                    // readiness state while advancing the inventory generation.
                    presence.Generation = request.Generation;
                    presence.UpdatedUtc = now;
                }
                else
                {
                    dbContext.ContentPresences.Add(new ContentPresence
                    {
                        NodeId = nodeId,
                        ContentId = content.Id,
                        Generation = request.Generation,
                        UpdatedUtc = now
                    });
                }
            }
        }

        if (previousPresences.Count != 0)
        {
            dbContext.ContentPresences.RemoveRange(previousPresences.Values);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RegisterContentNodeResponse(
            node.LeaseId,
            node.LeaseExpiresUtc,
            node.Generation,
            registeredContentIds.Count);
    }

    public async Task<ContentHeartbeatResponse?> HeartbeatAsync(
        string nodeId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ContentNode? node = await dbContext.ContentNodes.SingleOrDefaultAsync(
            item => item.NodeId == nodeId,
            cancellationToken);

        if (node is null || node.LeaseId != leaseId || node.LeaseExpiresUtc <= now)
        {
            return null;
        }

        node.LastSeenUtc = now;
        node.LeaseExpiresUtc = now.AddSeconds(directoryOptions.LeaseLifetimeSeconds);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ContentHeartbeatResponse(node.LeaseExpiresUtc);
    }

    public async Task<ContentLookupResponse?> LookupAsync(
        int algorithmValue,
        string digest,
        int? requestedMaxPeers,
        string? excludeNodeId,
        bool prepare,
        CancellationToken cancellationToken)
    {
        ContentIdentityAlgorithm algorithm = (ContentIdentityAlgorithm)algorithmValue;
        string normalizedDigest = ContentIdentityRules.NormalizeDigest(algorithm, digest);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ContentIdentityRecord? identity = await dbContext.ContentIdentities
            .AsNoTracking()
            .Include(item => item.Content)
            .SingleOrDefaultAsync(
                item => item.Algorithm == algorithm && item.DigestHex == normalizedDigest,
                cancellationToken);

        if (identity is null)
        {
            return null;
        }

        int maxPeers = Math.Clamp(
            requestedMaxPeers ?? directoryOptions.MaxLookupPeers,
            1,
            directoryOptions.MaxLookupPeers);

        IQueryable<ContentPresence> query = dbContext.ContentPresences
            .Where(presence =>
                presence.ContentId == identity.ContentId
                && presence.Node.LeaseExpiresUtc > now);

        if (!string.IsNullOrWhiteSpace(excludeNodeId))
        {
            query = query.Where(presence => presence.NodeId != excludeNodeId);
        }

        List<ContentPresence> presences = await query
            .Include(presence => presence.Node)
            .ThenInclude(node => node.Endpoints)
            .OrderByDescending(presence => presence.SeedReadyUntilUtc > now)
            .ThenByDescending(presence => presence.Node.LastSeenUtc)
            .Take(maxPeers)
            .ToListAsync(cancellationToken);

        bool queuedWakeup = false;
        DateTimeOffset wakeExpiredBefore =
            now.AddSeconds(-directoryOptions.WakeRequestLifetimeSeconds);

        if (prepare)
        {
            foreach (ContentPresence presence in presences)
            {
                bool ready = presence.SeedReadyUntilUtc > now
                    && identity.Content.CanonicalProtocolVersion == 1
                    && !string.IsNullOrWhiteSpace(identity.Content.CanonicalInfoHashV2)
                    && identity.Content.CanonicalTorrent is { Length: > 0 };
                if (ready)
                {
                    continue;
                }

                bool pending = presence.WakeRequestId.HasValue
                    && presence.WakeRequestedUtc > wakeExpiredBefore;
                bool backoffElapsed = !presence.NextWakeAllowedUtc.HasValue
                    || presence.NextWakeAllowedUtc <= now;
                if (!pending && backoffElapsed)
                {
                    presence.WakeRequestId = Guid.NewGuid();
                    presence.WakeRequestedUtc = now;
                    presence.UpdatedUtc = now;
                    queuedWakeup = true;
                }
            }

            if (queuedWakeup)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        bool manifestAvailable =
            identity.Content.CanonicalProtocolVersion == 1
            && !string.IsNullOrWhiteSpace(identity.Content.CanonicalInfoHashV2)
            && identity.Content.CanonicalTorrent is { Length: > 0 };

        var peers = presences.Select(presence =>
            new ContentPeerResponse(
                presence.NodeId,
                presence.Node.LeaseExpiresUtc,
                manifestAvailable && presence.SeedReadyUntilUtc > now,
                presence.Node.Endpoints
                    .Select(endpoint => new PeerEndpointResponse(
                        endpoint.Address,
                        endpoint.IsIpv6,
                        endpoint.Port,
                        endpoint.Transport switch
                        {
                            ContentPeerTransport.Tcp => PeerTransportContract.Tcp,
                            ContentPeerTransport.Utp => PeerTransportContract.Utp,
                            _ => throw new InvalidOperationException("Unknown peer transport.")
                        },
                        "observed-address-unverified-port"))
                    .ToArray()))
            .ToArray();

        return new ContentLookupResponse(
            identity.ContentId,
            new ContentIdentityContract
            {
                Algorithm = algorithmValue,
                Digest = normalizedDigest
            },
            identity.Content.Size,
            identity.Content.CanonicalProtocolVersion,
            identity.Content.CanonicalInfoHashV2,
            manifestAvailable,
            peers.Any(peer => peer.Ready) ? 0 : 1000,
            now,
            peers);
    }

    public async Task<IReadOnlyList<ContentWakeupResponse>?> PollWakeupsAsync(
        string nodeId,
        Guid leaseId,
        int? requestedMaxItems,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ContentNode? node = await dbContext.ContentNodes
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.NodeId == nodeId, cancellationToken);
        if (node is null || node.LeaseId != leaseId || node.LeaseExpiresUtc <= now)
        {
            return null;
        }

        int maxItems = Math.Clamp(
            requestedMaxItems ?? directoryOptions.MaxWakeupsPerPoll,
            1,
            directoryOptions.MaxWakeupsPerPoll);
        DateTimeOffset validAfter =
            now.AddSeconds(-directoryOptions.WakeRequestLifetimeSeconds);

        List<ContentPresence> pending = await dbContext.ContentPresences
            .AsNoTracking()
            .Where(presence =>
                presence.NodeId == nodeId
                && presence.WakeRequestId != null
                && presence.WakeRequestedUtc > validAfter)
            .Include(presence => presence.Content)
            .ThenInclude(content => content.Identities)
            .OrderBy(presence => presence.WakeRequestedUtc)
            .Take(maxItems)
            .ToListAsync(cancellationToken);

        return pending.Select(presence => new ContentWakeupResponse(
            presence.WakeRequestId!.Value,
            presence.ContentId,
            presence.Content.Size,
            presence.Content.Identities
                .Select(identity => new ContentIdentityContract
                {
                    Algorithm = (int)identity.Algorithm,
                    Digest = identity.DigestHex
                })
                .ToArray()))
            .ToArray();
    }

    public async Task<bool> CompleteWakeupAsync(
        string nodeId,
        Guid wakeRequestId,
        CompleteContentWakeupRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ContentNode? node = await dbContext.ContentNodes
            .SingleOrDefaultAsync(item => item.NodeId == nodeId, cancellationToken);
        if (node is null || node.LeaseId != request.LeaseId || node.LeaseExpiresUtc <= now)
        {
            return false;
        }

        ContentPresence? presence = await dbContext.ContentPresences
            .Include(item => item.Content)
            .SingleOrDefaultAsync(
                item => item.NodeId == nodeId
                    && item.WakeRequestId == wakeRequestId,
                cancellationToken);
        if (presence is null)
        {
            return false;
        }

        if (!request.Succeeded)
        {
            presence.WakeRequestId = null;
            presence.WakeRequestedUtc = null;
            presence.SeedReadyUntilUtc = null;
            presence.NextWakeAllowedUtc =
                now.AddSeconds(directoryOptions.WakeRetryBackoffSeconds);
            presence.UpdatedUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        string? bep52Root = await dbContext.ContentIdentities
            .AsNoTracking()
            .Where(identity => identity.ContentId == presence.ContentId
                && identity.Algorithm
                    == ContentIdentityAlgorithm.Bep52FileRootSha256)
            .Select(identity => identity.DigestHex)
            .SingleOrDefaultAsync(cancellationToken);
        if (bep52Root is null)
        {
            throw new ContentDirectoryConflictException(
                "Canonical v2 metadata requires an authoritative BEP 52 file-root identity.");
        }

        if (request.CanonicalProtocolVersion != 1)
        {
            throw new ArgumentException(
                "Only canonical protocol version 1 is supported.",
                nameof(request.CanonicalProtocolVersion));
        }

        string infoHash = (request.CanonicalInfoHashV2 ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (infoHash.Length != 64 || !infoHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "CanonicalInfoHashV2 must contain exactly 64 hexadecimal characters.",
                nameof(request.CanonicalInfoHashV2));
        }

        byte[] manifest;
        try
        {
            manifest = Convert.FromBase64String(
                request.CanonicalTorrentBase64 ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "CanonicalTorrentBase64 is not valid Base64.",
                nameof(request.CanonicalTorrentBase64),
                exception);
        }

        if (manifest.Length == 0
            || manifest.Length > directoryOptions.MaxCanonicalTorrentBytes)
        {
            throw new ArgumentException(
                $"Canonical torrent metadata must contain 1 to {directoryOptions.MaxCanonicalTorrentBytes} bytes.",
                nameof(request.CanonicalTorrentBase64));
        }

        if (!CanonicalTorrentValidator.Validate(
            manifest,
            presence.Content.Size,
            bep52Root,
            infoHash,
            out string validationError))
        {
            throw new ContentDirectoryConflictException(validationError);
        }

        ContentObject content = presence.Content;
        if (!string.IsNullOrWhiteSpace(content.CanonicalInfoHashV2)
            && !content.CanonicalInfoHashV2.Equals(
                infoHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentDirectoryConflictException(
                "The node produced a different canonical v2 info-hash for this content.");
        }

        if (content.CanonicalTorrent is { Length: > 0 }
            && !content.CanonicalTorrent.AsSpan().SequenceEqual(manifest))
        {
            throw new ContentDirectoryConflictException(
                "The node produced different canonical torrent metadata for this content.");
        }

        content.CanonicalProtocolVersion = 1;
        content.CanonicalInfoHashV2 = infoHash;
        content.CanonicalTorrent = manifest;

        presence.WakeRequestId = null;
        presence.WakeRequestedUtc = null;
        presence.NextWakeAllowedUtc = now;
        presence.SeedReadyUntilUtc =
            now.AddSeconds(directoryOptions.SeedReadyLifetimeSeconds);
        presence.UpdatedUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<byte[]?> GetCanonicalManifestAsync(
        Guid contentId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Contents
            .AsNoTracking()
            .Where(content => content.Id == contentId
                && content.CanonicalProtocolVersion == 1)
            .Select(content => content.CanonicalTorrent)
            .SingleOrDefaultAsync(cancellationToken);
    }


    public async Task<bool> AnnounceResourceAsync(
        string nodeId,
        ResourceAnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ContentNode? node = await dbContext.ContentNodes
            .SingleOrDefaultAsync(item => item.NodeId == nodeId, cancellationToken);
        if (node is null
            || node.LeaseId != request.LeaseId
            || node.LeaseExpiresUtc <= now)
        {
            return false;
        }

        ResourceKeyAlgorithm resourceAlgorithm =
            (ResourceKeyAlgorithm)request.ResourceKey.Algorithm;
        string resourceDigest = ResourceKeyRules.NormalizeDigest(
            resourceAlgorithm,
            request.ResourceKey.Digest);

        ContentIdentityAlgorithm contentAlgorithm =
            (ContentIdentityAlgorithm)request.ContentIdentity.Algorithm;
        string contentDigest = ContentIdentityRules.NormalizeDigest(
            contentAlgorithm,
            request.ContentIdentity.Digest);

        ContentIdentityRecord? identity = await dbContext.ContentIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Algorithm == contentAlgorithm
                    && item.DigestHex == contentDigest,
                cancellationToken);
        if (identity is null)
        {
            throw new ContentDirectoryConflictException(
                "The announced content identity is not registered.");
        }

        bool nodeOwnsContent = await dbContext.ContentPresences
            .AsNoTracking()
            .AnyAsync(
                presence => presence.NodeId == nodeId
                    && presence.ContentId == identity.ContentId,
                cancellationToken);
        if (!nodeOwnsContent)
        {
            throw new ContentDirectoryConflictException(
                "The node cannot announce a resource mapping for content outside its current inventory.");
        }

        ResourceObservation? observation = await dbContext.ResourceObservations
            .SingleOrDefaultAsync(
                item => item.Algorithm == resourceAlgorithm
                    && item.DigestHex == resourceDigest
                    && item.NodeId == nodeId,
                cancellationToken);

        if (observation is null)
        {
            observation = new ResourceObservation
            {
                Algorithm = resourceAlgorithm,
                DigestHex = resourceDigest,
                NodeId = nodeId
            };
            dbContext.ResourceObservations.Add(observation);
        }

        observation.ContentId = identity.ContentId;
        observation.ObservedUtc = now;
        observation.ExpiresUtc =
            now.AddSeconds(directoryOptions.ResourceHintLifetimeSeconds);

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ResourceLookupResponse?> LookupResourceAsync(
        int algorithmValue,
        string digest,
        int? requestedMaxCandidates,
        CancellationToken cancellationToken)
    {
        ResourceKeyAlgorithm algorithm = (ResourceKeyAlgorithm)algorithmValue;
        string normalizedDigest = ResourceKeyRules.NormalizeDigest(
            algorithm,
            digest);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int maxCandidates = Math.Clamp(
            requestedMaxCandidates ?? directoryOptions.MaxResourceCandidates,
            1,
            directoryOptions.MaxResourceCandidates);

        List<ResourceObservation> observations = await dbContext.ResourceObservations
            .AsNoTracking()
            .Where(observation =>
                observation.Algorithm == algorithm
                && observation.DigestHex == normalizedDigest
                && observation.ExpiresUtc > now
                && observation.Node.LeaseExpiresUtc > now)
            .Include(observation => observation.Content)
            .ThenInclude(content => content.Identities)
            .OrderByDescending(observation => observation.ObservedUtc)
            .Take(directoryOptions.MaxResourceObservationsPerLookup)
            .ToListAsync(cancellationToken);

        if (observations.Count == 0)
        {
            return null;
        }

        ResourceCandidateResponse[] candidates = observations
            .GroupBy(observation => observation.ContentId)
            .Select(group =>
            {
                ResourceObservation newest = group
                    .OrderByDescending(item => item.ObservedUtc)
                    .First();
                return new ResourceCandidateResponse(
                    newest.ContentId,
                    newest.Content.Size,
                    group.Select(item => item.NodeId).Distinct().Count(),
                    group.Max(item => item.ObservedUtc),
                    newest.Content.Identities
                        .Select(identity => new ContentIdentityContract
                        {
                            Algorithm = (int)identity.Algorithm,
                            Digest = identity.DigestHex
                        })
                        .OrderBy(identity => identity.Algorithm)
                        .ToArray());
            })
            .OrderByDescending(candidate => candidate.ObservationCount)
            .ThenByDescending(candidate => candidate.LastObservedUtc)
            .Take(maxCandidates)
            .ToArray();

        return new ResourceLookupResponse(
            new ResourceKeyContract
            {
                Algorithm = algorithmValue,
                Digest = normalizedDigest
            },
            now,
            candidates);
    }

    private static NormalizedAnnouncement NormalizeAnnouncement(
        ContentAnnouncementRequest request)
    {
        if (request.Identities.Count == 0)
        {
            throw new ArgumentException("Every content item must contain at least one identity.");
        }

        var identities = request.Identities
            .Select(identity =>
            {
                ContentIdentityAlgorithm algorithm =
                    (ContentIdentityAlgorithm)identity.Algorithm;
                return new NormalizedIdentity(
                    algorithm,
                    ContentIdentityRules.NormalizeDigest(algorithm, identity.Digest));
            })
            .Distinct()
            .ToArray();

        return new NormalizedAnnouncement(request.Size, identities);
    }

    private static void EnsureRequestAliasesAreUnambiguous(
        IReadOnlyList<NormalizedAnnouncement> announcements)
    {
        var owners = new Dictionary<(ContentIdentityAlgorithm Algorithm, string Digest), int>();
        for (int index = 0; index < announcements.Count; ++index)
        {
            foreach (NormalizedIdentity identity in announcements[index].Identities)
            {
                var key = (identity.Algorithm, identity.DigestHex);
                if (owners.TryGetValue(key, out int existingIndex) && existingIndex != index)
                {
                    throw new ContentDirectoryConflictException(
                        "The same content identity appears in more than one inventory item.");
                }

                owners[key] = index;
            }
        }
    }

    private async Task<Dictionary<(ContentIdentityAlgorithm Algorithm, string Digest), Guid>>
        LoadExistingIdentityMapAsync(
            IReadOnlyList<NormalizedAnnouncement> announcements,
            CancellationToken cancellationToken)
    {
        var result =
            new Dictionary<(ContentIdentityAlgorithm Algorithm, string Digest), Guid>();

        foreach (IGrouping<ContentIdentityAlgorithm, NormalizedIdentity> group in announcements
            .SelectMany(announcement => announcement.Identities)
            .Distinct()
            .GroupBy(identity => identity.Algorithm))
        {
            string[] digests = group.Select(identity => identity.DigestHex).Distinct().ToArray();
            foreach (string[] chunk in digests.Chunk(400))
            {
                List<ContentIdentityRecord> matches = await dbContext.ContentIdentities
                    .AsNoTracking()
                    .Where(identity =>
                        identity.Algorithm == group.Key
                        && chunk.Contains(identity.DigestHex))
                    .ToListAsync(cancellationToken);

                foreach (ContentIdentityRecord match in matches)
                {
                    result[(match.Algorithm, match.DigestHex)] = match.ContentId;
                }
            }
        }

        return result;
    }
}
