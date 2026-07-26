using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenNet.Server.Contracts;
using OpenNet.Server.Domain;
using OpenNet.Server.Options;

namespace OpenNet.Server.Application;

public sealed class TraversalDirectoryService(
    ITraversalServerRepository repository,
    IMemoryCache cache,
    IOptions<TraversalDirectoryOptions> options)
{
    private const string AvailableServersCacheKey = "traversal-servers:available:v1";
    private readonly TraversalDirectoryOptions directoryOptions = options.Value;

    public async Task<TraversalServerListResponse> GetAvailableAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TraversalServerResponse> servers =
            await cache.GetOrCreateAsync(
                AvailableServersCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow =
                        TimeSpan.FromSeconds(directoryOptions.CacheDurationSeconds);
                    DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(
                        -directoryOptions.HeartbeatLifetimeSeconds);
                    IReadOnlyList<TraversalServer> entities =
                        await repository.GetAvailableAsync(cutoff, cancellationToken);

                    return entities.Select(ToResponse).ToArray();
                })
            ?? [];

        return new(
            DateTimeOffset.UtcNow,
            directoryOptions.CacheDurationSeconds,
            servers);
    }

    public async Task<TraversalServerResponse> UpsertAsync(
        UpsertTraversalServerRequest request,
        CancellationToken cancellationToken)
    {
        ValidateAddress(request.IPv4Address, nameof(request.IPv4Address));
        if (!string.IsNullOrWhiteSpace(request.IPv6Address)
            && !TraversalServer.IsValidIPv6(request.IPv6Address))
        {
            throw new ArgumentException(
                "A valid IPv6 address is required.",
                nameof(request.IPv6Address));
        }
        if (!string.IsNullOrWhiteSpace(request.AlternateIPv4Address))
        {
            ValidateAddress(request.AlternateIPv4Address, nameof(request.AlternateIPv4Address));
        }

        TraversalServer? server = request.Id is Guid id
            ? await repository.FindAsync(id, cancellationToken)
            : await repository.FindByEndpointAsync(
                request.IPv4Address,
                request.ApiPort,
                cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (server is null)
        {
            server = new TraversalServer
            {
                Name = request.Name.Trim(),
                IPv4Address = request.IPv4Address,
                CreatedUtc = now
            };
        }

        server.Name = request.Name.Trim();
        server.IPv4Address = request.IPv4Address;
        server.IPv6Address = string.IsNullOrWhiteSpace(request.IPv6Address)
            ? null
            : request.IPv6Address;
        server.ApiPort = request.ApiPort;
        server.StunPort = request.StunPort;
        server.AlternateStunPort = request.AlternateStunPort;
        server.AlternateIPv4Address = string.IsNullOrWhiteSpace(request.AlternateIPv4Address)
            ? null
            : request.AlternateIPv4Address;
        server.Priority = request.Priority;
        server.IsEnabled = true;
        server.LastHeartbeatUtc = now;
        server.UpdatedUtc = now;

        await repository.SaveAsync(server, cancellationToken);
        cache.Remove(AvailableServersCacheKey);
        return ToResponse(server);
    }

    public async Task<bool> HeartbeatAsync(Guid id, CancellationToken cancellationToken)
    {
        TraversalServer? server = await repository.FindAsync(id, cancellationToken);
        if (server is null || !server.IsEnabled)
        {
            return false;
        }

        server.LastHeartbeatUtc = DateTimeOffset.UtcNow;
        server.UpdatedUtc = server.LastHeartbeatUtc;
        await repository.SaveAsync(server, cancellationToken);
        cache.Remove(AvailableServersCacheKey);
        return true;
    }

    public bool IsManagementRequestAuthorized(HttpRequest request)
    {
        string configuredKey = directoryOptions.ManagementApiKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return false;
        }

        return request.Headers.TryGetValue("X-OpenNet-Api-Key", out var supplied)
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(configuredKey),
                System.Text.Encoding.UTF8.GetBytes(supplied.ToString()));
    }

    private static TraversalServerResponse ToResponse(TraversalServer server)
    {
        return new(
            server.Id,
            server.Name,
            server.IPv4Address,
            server.IPv6Address,
            server.ApiPort,
            server.StunPort,
            server.AlternateStunPort,
            server.AlternateIPv4Address,
            server.Priority);
    }

    private static void ValidateAddress(string address, string parameterName)
    {
        if (!TraversalServer.IsValidPublicOrDevelopmentIPv4(address))
        {
            throw new ArgumentException("A valid IPv4 address is required.", parameterName);
        }
    }
}
