using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;
using OpenNet.Server.Domain;
using OpenNet.Server.Infrastructure;
using OpenNet.Server.Options;
using System.Text.Json;

namespace OpenNet.Server.Tests;

[TestClass]
public sealed class TraversalDirectoryServiceTests
{
    [TestMethod]
    public async Task GetAvailableAsync_MapsAndCachesRepositoryResults()
    {
        FakeRepository repository = new();
        repository.Servers.Add(CreateServer());
        using MemoryCache cache = new(new MemoryCacheOptions());
        TraversalDirectoryService service = CreateService(repository, cache);

        TraversalServerListResponse first =
            await service.GetAvailableAsync(CancellationToken.None);
        TraversalServerListResponse second =
            await service.GetAvailableAsync(CancellationToken.None);

        Assert.HasCount(1, first.Servers);
        Assert.AreEqual("198.51.100.10", first.Servers[0].IPv4Address);
        Assert.AreEqual(1, repository.AvailableCalls);
        Assert.AreEqual(first.Servers[0], second.Servers[0]);
    }

    [TestMethod]
    public async Task UpsertAsync_RejectsNonIPv4Address()
    {
        FakeRepository repository = new();
        using MemoryCache cache = new(new MemoryCacheOptions());
        TraversalDirectoryService service = CreateService(repository, cache);

        UpsertTraversalServerRequest request = new()
        {
            Name = "invalid",
            IPv4Address = "2001:db8::1"
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.UpsertAsync(request, CancellationToken.None));
    }

    [TestMethod]
    public async Task UpsertAndHeartbeat_PersistNodeState()
    {
        FakeRepository repository = new();
        using MemoryCache cache = new(new MemoryCacheOptions());
        TraversalDirectoryService service = CreateService(repository, cache);

        TraversalServerResponse created = await service.UpsertAsync(
            new UpsertTraversalServerRequest
            {
                Name = "node-a",
                IPv4Address = "203.0.113.10",
                AlternateIPv4Address = "203.0.113.11",
                ApiPort = 48100
            },
            CancellationToken.None);
        DateTimeOffset before = repository.Servers.Single().LastHeartbeatUtc;
        await Task.Delay(5);

        bool updated = await service.HeartbeatAsync(
            created.Id,
            CancellationToken.None);

        Assert.IsTrue(updated);
        Assert.IsTrue(repository.Servers.Single().LastHeartbeatUtc > before);
    }

    [TestMethod]
    public void ManagementAuthorization_RequiresConfiguredConstantTimeKey()
    {
        FakeRepository repository = new();
        using MemoryCache cache = new(new MemoryCacheOptions());
        TraversalDirectoryService service = CreateService(repository, cache);
        DefaultHttpContext context = new();

        Assert.IsFalse(service.IsManagementRequestAuthorized(context.Request));
        context.Request.Headers["X-OpenNet-Api-Key"] = "test-key";
        Assert.IsTrue(service.IsManagementRequestAuthorized(context.Request));
    }

    [TestMethod]
    public void DirectoryResponse_UsesClientCompatibleWebJsonNames()
    {
        TraversalServerResponse server = new(
            Guid.NewGuid(),
            "node-a",
            "198.51.100.10",
            "2001:db8::10",
            48100,
            3478,
            3479,
            "198.51.100.11",
            100);
        TraversalServerListResponse response =
            new(DateTimeOffset.UtcNow, 15, [server]);

        string json = JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"servers\"");
        StringAssert.Contains(json, "\"ipv4Address\":\"198.51.100.10\"");
        StringAssert.Contains(json, "\"ipv6Address\":\"2001:db8::10\"");
        StringAssert.Contains(json, "\"alternateIPv4Address\":\"198.51.100.11\"");
        StringAssert.Contains(json, "\"alternateStunPort\":3479");
    }

    [TestMethod]
    public void TraversalDbContext_CanSelectOfficialMySqlProvider()
    {
        DbContextOptions<TraversalDbContext> options =
            new DbContextOptionsBuilder<TraversalDbContext>()
                .UseMySQL(
                    "Server=127.0.0.1;Database=opennet;User=opennet;Password=test")
                .Options;

        using TraversalDbContext context = new(options);

        Assert.AreEqual(
            "MySql.EntityFrameworkCore",
            context.Database.ProviderName);
    }

    private static TraversalDirectoryService CreateService(
        FakeRepository repository,
        IMemoryCache cache)
    {
        return new(
            repository,
            cache,
            Microsoft.Extensions.Options.Options.Create(new TraversalDirectoryOptions
            {
                CacheDurationSeconds = 30,
                HeartbeatLifetimeSeconds = 180,
                ManagementApiKey = "test-key"
            }));
    }

    private static TraversalServer CreateServer()
    {
        return new()
        {
            Name = "node-a",
            IPv4Address = "198.51.100.10",
            ApiPort = 48100,
            LastHeartbeatUtc = DateTimeOffset.UtcNow
        };
    }

    private sealed class FakeRepository : ITraversalServerRepository
    {
        public List<TraversalServer> Servers { get; } = [];
        public int AvailableCalls { get; private set; }

        public Task<IReadOnlyList<TraversalServer>> GetAvailableAsync(
            DateTimeOffset heartbeatCutoff,
            CancellationToken cancellationToken)
        {
            AvailableCalls++;
            IReadOnlyList<TraversalServer> result = Servers
                .Where(server => server.IsEnabled
                    && server.LastHeartbeatUtc >= heartbeatCutoff)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<TraversalServer?> FindAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Servers.SingleOrDefault(server => server.Id == id));
        }

        public Task<TraversalServer?> FindByEndpointAsync(
            string ipv4Address,
            int apiPort,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                Servers.SingleOrDefault(server =>
                    server.IPv4Address == ipv4Address && server.ApiPort == apiPort));
        }

        public Task SaveAsync(
            TraversalServer server,
            CancellationToken cancellationToken)
        {
            if (!Servers.Contains(server))
                Servers.Add(server);
            return Task.CompletedTask;
        }
    }
}
