using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenNet.Server.Domain;
using OpenNet.Server.Options;

namespace OpenNet.Server.Infrastructure;

public static class TraversalDatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        TraversalDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<TraversalDbContext>();
        TraversalDirectoryOptions options =
            scope.ServiceProvider.GetRequiredService<IOptions<TraversalDirectoryOptions>>().Value;

        await dbContext.Database.EnsureCreatedAsync();

        foreach (SeedTraversalServerOptions seed in options.SeedServers)
        {
            TraversalServer? existing = await dbContext.TraversalServers.SingleOrDefaultAsync(
                server => server.IPv4Address == seed.IPv4Address
                    && server.ApiPort == seed.ApiPort);

            if (existing is null)
            {
                dbContext.TraversalServers.Add(new TraversalServer
                {
                    Name = seed.Name,
                    IPv4Address = seed.IPv4Address,
                    IPv6Address = seed.IPv6Address,
                    ApiPort = seed.ApiPort,
                    StunPort = seed.StunPort,
                    AlternateStunPort = seed.AlternateStunPort,
                    AlternateIPv4Address = seed.AlternateIPv4Address,
                    Priority = seed.Priority
                });
            }
            else
            {
                existing.Name = seed.Name;
                existing.IPv6Address = seed.IPv6Address;
                existing.StunPort = seed.StunPort;
                existing.AlternateStunPort = seed.AlternateStunPort;
                existing.AlternateIPv4Address = seed.AlternateIPv4Address;
                existing.Priority = seed.Priority;
                existing.IsEnabled = true;
                existing.LastHeartbeatUtc = DateTimeOffset.UtcNow;
                existing.UpdatedUtc = existing.LastHeartbeatUtc;
            }
        }

        await dbContext.SaveChangesAsync();
    }
}
