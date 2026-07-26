using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OpenNet.Server.Contracts;

public sealed record TraversalServerResponse(
    Guid Id,
    string Name,
    [property: JsonPropertyName("ipv4Address")]
    string IPv4Address,
    [property: JsonPropertyName("ipv6Address")]
    string? IPv6Address,
    int ApiPort,
    int StunPort,
    int AlternateStunPort,
    string? AlternateIPv4Address,
    int Priority);

public sealed record TraversalServerListResponse(
    DateTimeOffset GeneratedAtUtc,
    int CacheLifetimeSeconds,
    IReadOnlyList<TraversalServerResponse> Servers);

public sealed class UpsertTraversalServerRequest
{
    public Guid? Id { get; init; }

    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required]
    public required string IPv4Address { get; init; }

    public string? IPv6Address { get; init; }

    [Range(1, 65535)]
    public int ApiPort { get; init; } = 48100;

    [Range(1, 65535)]
    public int StunPort { get; init; } = 3478;

    [Range(1, 65535)]
    public int AlternateStunPort { get; init; } = 3479;

    public string? AlternateIPv4Address { get; init; }

    [Range(0, 10000)]
    public int Priority { get; init; } = 100;
}

public sealed class TraversalHeartbeatRequest
{
    [Required]
    public required Guid ServerId { get; init; }
}
