using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenNet.Server.Domain;
using OpenNet.Server.Infrastructure;
using OpenNet.Server.Options;

namespace OpenNet.Server.Application;

public sealed class ContentDirectoryCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ContentDirectoryOptions> options,
    ILogger<ContentDirectoryCleanupService> logger)
    : BackgroundService
{
    private readonly TimeSpan interval = TimeSpan.FromSeconds(
        Math.Clamp(options.Value.LeaseLifetimeSeconds / 2, 30, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                ContentDirectoryDbContext db =
                    scope.ServiceProvider.GetRequiredService<ContentDirectoryDbContext>();

                DateTimeOffset now = DateTimeOffset.UtcNow;
                List<ResourceObservation> expiredResources =
                    await db.ResourceObservations
                        .Where(observation => observation.ExpiresUtc <= now)
                        .ToListAsync(stoppingToken);
                if (expiredResources.Count != 0)
                {
                    db.ResourceObservations.RemoveRange(expiredResources);
                }

                List<ContentNode> expiredNodes = await db.ContentNodes
                    .Where(node => node.LeaseExpiresUtc <= now)
                    .ToListAsync(stoppingToken);

                if (expiredNodes.Count != 0)
                {
                    db.ContentNodes.RemoveRange(expiredNodes);
                }

                if (expiredNodes.Count == 0 && expiredResources.Count == 0)
                {
                    continue;
                }

                await db.SaveChangesAsync(stoppingToken);

                logger.LogDebug(
                    "Removed {ExpiredNodeCount} expired content-directory nodes and {ExpiredResourceCount} expired resource observations.",
                    expiredNodes.Count,
                    expiredResources.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Content-directory lease cleanup failed.");
            }
        }
    }
}
