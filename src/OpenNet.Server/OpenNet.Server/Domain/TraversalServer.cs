using System.Net;

namespace OpenNet.Server.Domain;

public sealed class TraversalServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string IPv4Address { get; set; }
    public string? IPv6Address { get; set; }
    public int ApiPort { get; set; } = 48100;
    public int StunPort { get; set; } = 3478;
    public int AlternateStunPort { get; set; } = 3479;
    public string? AlternateIPv4Address { get; set; }
    public int Priority { get; set; } = 100;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset LastHeartbeatUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public static bool IsValidPublicOrDevelopmentIPv4(string value)
    {
        return IPAddress.TryParse(value, out IPAddress? address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && !IPAddress.Any.Equals(address)
            && !IPAddress.Broadcast.Equals(address);
    }

    public static bool IsValidIPv6(string value)
    {
        return IPAddress.TryParse(value, out IPAddress? address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && !IPAddress.IPv6Any.Equals(address)
            && !IPAddress.IPv6None.Equals(address);
    }
}
