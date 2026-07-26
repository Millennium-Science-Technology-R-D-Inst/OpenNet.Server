using System.ComponentModel.DataAnnotations;

namespace OpenNet.Server.Options;

public sealed class TraversalDirectoryOptions
{
    public const string SectionName = "TraversalDirectory";

    [Range(30, 3600)]
    public int HeartbeatLifetimeSeconds { get; init; } = 180;

    [Range(1, 300)]
    public int CacheDurationSeconds { get; init; } = 15;

    public string ManagementApiKey { get; init; } = string.Empty;

    public IReadOnlyList<SeedTraversalServerOptions> SeedServers { get; init; } = [];
}

public sealed class SeedTraversalServerOptions
{
    public required string Name { get; init; }
    public required string IPv4Address { get; init; }
    public string? IPv6Address { get; init; }
    public int ApiPort { get; init; } = 48100;
    public int StunPort { get; init; } = 3478;
    public int AlternateStunPort { get; init; } = 3479;
    public string? AlternateIPv4Address { get; init; }
    public int Priority { get; init; } = 100;
}
