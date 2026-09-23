using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OpenNet.Server.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PeerTransportContract>))]
public enum PeerTransportContract
{
    Tcp,
    Utp
}

[JsonConverter(typeof(JsonStringEnumConverter<PeerAddressFamilyContract>))]
public enum PeerAddressFamilyContract
{
    Ipv4 = 4,
    Ipv6 = 6
}

public sealed class ContentIdentityContract
{
    [Range(1, short.MaxValue)]
    public int Algorithm { get; init; }

    [Required]
    public required string Digest { get; init; }
}

public sealed class ContentAnnouncementRequest
{
    [Range(0, long.MaxValue)]
    public long Size { get; init; }

    [Required, MinLength(1), MaxLength(8)]
    public required List<ContentIdentityContract> Identities { get; init; }
}

public sealed class PeerEndpointRequest
{
    public PeerTransportContract Transport { get; init; }

    public PeerAddressFamilyContract AddressFamily { get; init; }

    [Range(1, 65535)]
    public int Port { get; init; }
}

public sealed class RegisterContentNodeRequest
{
    [Required, StringLength(128, MinimumLength = 8)]
    public required string NodeId { get; init; }

    [Range(0, long.MaxValue)]
    public long Generation { get; init; }

    public Guid RegistrationId { get; init; }

    public Guid? PreviousLeaseId { get; init; }

    [MaxLength(8)]
    public List<PeerEndpointRequest> Endpoints { get; init; } = [];

    [Required]
    public List<ContentAnnouncementRequest> Contents { get; init; } = [];
}

public sealed record RegisterContentNodeResponse(
    Guid LeaseId,
    DateTimeOffset LeaseExpiresAtUtc,
    long AcceptedGeneration,
    int ContentCount);

public sealed class ContentHeartbeatRequest
{
    public Guid LeaseId { get; init; }
}

public sealed record ContentHeartbeatResponse(
    DateTimeOffset LeaseExpiresAtUtc);

public sealed record PeerEndpointResponse(
    string Address,
    bool IsIpv6,
    int Port,
    PeerTransportContract Transport,
    string Verification);

public sealed record ContentPeerResponse(
    string NodeId,
    DateTimeOffset LeaseExpiresAtUtc,
    bool Ready,
    IReadOnlyList<PeerEndpointResponse> Endpoints);

public sealed record ContentLookupResponse(
    Guid ContentId,
    ContentIdentityContract Identity,
    long Size,
    int CanonicalProtocolVersion,
    string? CanonicalInfoHashV2,
    bool ManifestAvailable,
    int RetryAfterMilliseconds,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ContentPeerResponse> Peers);

public sealed record ContentWakeupResponse(
    Guid WakeRequestId,
    Guid ContentId,
    long Size,
    IReadOnlyList<ContentIdentityContract> Identities);

public sealed class CompleteContentWakeupRequest
{
    public Guid LeaseId { get; init; }

    public bool Succeeded { get; init; }

    public int CanonicalProtocolVersion { get; init; } = 1;

    public string? CanonicalInfoHashV2 { get; init; }

    public string? CanonicalTorrentBase64 { get; init; }

    [StringLength(512)]
    public string? Error { get; init; }
}


public sealed class ResourceKeyContract
{
    public int Algorithm { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Digest { get; init; } = string.Empty;
}

public sealed class ResourceAnnouncementRequest
{
    public Guid LeaseId { get; init; }

    [Required]
    public ResourceKeyContract ResourceKey { get; init; } = new();

    [Required]
    public ContentIdentityContract ContentIdentity { get; init; } = new();
}

public sealed record ResourceCandidateResponse(
    Guid ContentId,
    long Size,
    int ObservationCount,
    DateTimeOffset LastObservedUtc,
    IReadOnlyList<ContentIdentityContract> Identities);

public sealed record ResourceLookupResponse(
    ResourceKeyContract ResourceKey,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ResourceCandidateResponse> Candidates);
