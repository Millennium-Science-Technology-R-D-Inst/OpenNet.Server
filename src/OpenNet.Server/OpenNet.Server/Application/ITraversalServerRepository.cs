using OpenNet.Server.Domain;

namespace OpenNet.Server.Application;

public interface ITraversalServerRepository
{
    Task<IReadOnlyList<TraversalServer>> GetAvailableAsync(
        DateTimeOffset heartbeatCutoff,
        CancellationToken cancellationToken);

    Task<TraversalServer?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<TraversalServer?> FindByEndpointAsync(
        string ipv4Address,
        int apiPort,
        CancellationToken cancellationToken);

    Task SaveAsync(TraversalServer server, CancellationToken cancellationToken);
}
