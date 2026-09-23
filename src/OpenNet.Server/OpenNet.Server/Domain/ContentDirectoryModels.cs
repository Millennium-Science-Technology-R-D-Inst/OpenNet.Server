using System.Net;

namespace OpenNet.Server.Domain;

public enum ContentIdentityAlgorithm : short
{
    Bep52FileRootSha256 = 1,
    WholeFileSha1 = 2,
    WholeFileSha256 = 3,
    BitCometLtSeed160Opaque = 128
}

public enum ContentPeerTransport : byte
{
    Tcp = 1,
    Utp = 2
}

public sealed class ContentNode
{
    public required string NodeId { get; set; }
    public long Generation { get; set; }
    public Guid LastRegistrationId { get; set; }
    public Guid LeaseId { get; set; }
    public DateTimeOffset LeaseExpiresUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<ContentNodeEndpoint> Endpoints { get; set; } = [];
    public ICollection<ContentPresence> Presences { get; set; } = [];
}

public sealed class ContentNodeEndpoint
{
    public long Id { get; set; }
    public required string NodeId { get; set; }
    public ContentNode Node { get; set; } = null!;
    public required string Address { get; set; }
    public bool IsIpv6 { get; set; }
    public int Port { get; set; }
    public ContentPeerTransport Transport { get; set; }
}

public sealed class ContentObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Size { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int CanonicalProtocolVersion { get; set; }
    public string? CanonicalInfoHashV2 { get; set; }
    public byte[]? CanonicalTorrent { get; set; }
    public ICollection<ContentIdentityRecord> Identities { get; set; } = [];
    public ICollection<ContentPresence> Presences { get; set; } = [];
}

public sealed class ContentIdentityRecord
{
    public long Id { get; set; }
    public Guid ContentId { get; set; }
    public ContentObject Content { get; set; } = null!;
    public ContentIdentityAlgorithm Algorithm { get; set; }
    public required string DigestHex { get; set; }
}

public sealed class ContentPresence
{
    public required string NodeId { get; set; }
    public ContentNode Node { get; set; } = null!;
    public Guid ContentId { get; set; }
    public ContentObject Content { get; set; } = null!;
    public long Generation { get; set; }
    public Guid? WakeRequestId { get; set; }
    public DateTimeOffset? WakeRequestedUtc { get; set; }
    public DateTimeOffset? NextWakeAllowedUtc { get; set; }
    public DateTimeOffset? SeedReadyUntilUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class ContentIdentityRules
{
    public static int ExpectedDigestBytes(ContentIdentityAlgorithm algorithm) => algorithm switch
    {
        ContentIdentityAlgorithm.Bep52FileRootSha256 => 32,
        ContentIdentityAlgorithm.WholeFileSha1 => 20,
        ContentIdentityAlgorithm.WholeFileSha256 => 32,
        ContentIdentityAlgorithm.BitCometLtSeed160Opaque => 20,
        _ => 0
    };

    public static string NormalizeDigest(ContentIdentityAlgorithm algorithm, string digestHex)
    {
        int expectedBytes = ExpectedDigestBytes(algorithm);
        if (expectedBytes == 0)
        {
            throw new ArgumentException($"Unsupported content identity algorithm {(short)algorithm}.", nameof(algorithm));
        }

        string normalized = digestHex.Trim().ToLowerInvariant();
        if (normalized.Length != expectedBytes * 2 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                $"Digest for {algorithm} must contain exactly {expectedBytes * 2} hexadecimal characters.",
                nameof(digestHex));
        }

        return normalized;
    }

    public static IPAddress NormalizeObservedAddress(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
