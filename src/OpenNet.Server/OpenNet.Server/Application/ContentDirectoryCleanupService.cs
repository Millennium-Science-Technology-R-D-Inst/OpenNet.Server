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
                List<ContentNode> expiredNodes = await db.ContentNodes
                    .Where(node => node.LeaseExpiresUtc <= now)
                    .ToListAsync(stoppingToken);

                if (expiredNodes.Count == 0)
                {
                    continue;
                }

                db.ContentNodes.RemoveRange(expiredNodes);
                await db.SaveChangesAsync(stoppingToken);

                logger.LogDebug(
                    "Removed {ExpiredNodeCount} expired content-directory nodes.",
                    expiredNodes.Count);
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
