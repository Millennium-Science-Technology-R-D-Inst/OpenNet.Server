using System.ComponentModel.DataAnnotations;

namespace OpenNet.Server.Options;

public sealed class ContentDirectoryOptions
{
    public const string SectionName = "ContentDirectory";

    [Range(30, 3600)]
    public int LeaseLifetimeSeconds { get; init; } = 300;

    [Range(1, 100000)]
    public int MaxInventoryItems { get; init; } = 50000;

    [Range(1, 100)]
    public int MaxLookupPeers { get; init; } = 20;

    [Range(5, 300)]
    public int WakeRequestLifetimeSeconds { get; init; } = 30;

    [Range(1, 300)]
    public int WakeRetryBackoffSeconds { get; init; } = 10;

    [Range(30, 900)]
    public int SeedReadyLifetimeSeconds { get; init; } = 240;

    [Range(1, 256)]
    public int MaxWakeupsPerPoll { get; init; } = 32;

    [Range(1024, 64 * 1024 * 1024)]
    public int MaxCanonicalTorrentBytes { get; init; } = 8 * 1024 * 1024;
}
