using Microsoft.EntityFrameworkCore;
using OpenNet.Server.Application;
using OpenNet.Server.Domain;

namespace OpenNet.Server.Infrastructure;

public sealed class TraversalServerRepository(TraversalDbContext dbContext)
    : ITraversalServerRepository
{
    public async Task<IReadOnlyList<TraversalServer>> GetAvailableAsync(
        DateTimeOffset heartbeatCutoff,
        CancellationToken cancellationToken)
    {
        return await dbContext.TraversalServers
            .AsNoTracking()
            .Where(server => server.IsEnabled && server.LastHeartbeatUtc >= heartbeatCutoff)
            .OrderBy(server => server.Priority)
            .ThenBy(server => server.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<TraversalServer?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.TraversalServers.SingleOrDefaultAsync(
            server => server.Id == id,
            cancellationToken);
    }

    public Task<TraversalServer?> FindByEndpointAsync(
        string ipv4Address,
        int apiPort,
        CancellationToken cancellationToken)
    {
        return dbContext.TraversalServers.SingleOrDefaultAsync(
            server => server.IPv4Address == ipv4Address && server.ApiPort == apiPort,
            cancellationToken);
    }

    public async Task SaveAsync(TraversalServer server, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(server).State == EntityState.Detached)
        {
            dbContext.TraversalServers.Add(server);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
