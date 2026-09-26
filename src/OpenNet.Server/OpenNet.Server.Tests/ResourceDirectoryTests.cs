using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;
using OpenNet.Server.Infrastructure;
using OpenNet.Server.Options;

namespace OpenNet.Server.Tests;

[TestClass]
public sealed class ResourceDirectoryTests
{
    private static async Task<(ContentDirectoryDbContext Db, ContentDirectoryService Service, RegisterContentNodeResponse Registration)>
        CreateRegisteredNodeAsync(
            SqliteConnection connection,
            string nodeId,
            string bep52Digest,
            string sha256Digest)
    {
        DbContextOptions<ContentDirectoryDbContext> dbOptions =
            new DbContextOptionsBuilder<ContentDirectoryDbContext>()
                .UseSqlite(connection)
                .Options;
        var db = new ContentDirectoryDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var service = new ContentDirectoryService(
            db,
            Microsoft.Extensions.Options.Options.Create(
                new ContentDirectoryOptions()));

        RegisterContentNodeResponse registration = await service.RegisterAsync(
            new RegisterContentNodeRequest
            {
                NodeId = nodeId,
                Generation = 1,
                RegistrationId = Guid.NewGuid(),
                Endpoints =
                [
                    new PeerEndpointRequest
                    {
                        AddressFamily = PeerAddressFamilyContract.Ipv4,
                        Transport = PeerTransportContract.Tcp,
                        Port = 6881
                    }
                ],
                Contents =
                [
                    new ContentAnnouncementRequest
                    {
                        Size = 4096,
                        Identities =
                        [
                            new ContentIdentityContract
                            {
                                Algorithm = 1,
                                Digest = bep52Digest
                            },
                            new ContentIdentityContract
                            {
                                Algorithm = 3,
                                Digest = sha256Digest
                            }
                        ]
                    }
                ]
            },
            IPAddress.Parse("203.0.113.50"),
            CancellationToken.None);

        return (db, service, registration);
    }

    [TestMethod]
    public async Task ResourceAnnouncementCanResolveRegisteredContent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        string root = new string('1', 64);
        string sha256 = new string('2', 64);
        var context = await CreateRegisteredNodeAsync(
            connection,
            "resource-node-0001",
            root,
            sha256);
        await using ContentDirectoryDbContext db = context.Db;

        bool announced = await context.Service.AnnounceResourceAsync(
            "resource-node-0001",
            new ResourceAnnouncementRequest
            {
                LeaseId = context.Registration.LeaseId,
                ResourceKey = new ResourceKeyContract
                {
                    Algorithm = 1,
                    Digest = new string('a', 64)
                },
                ContentIdentity = new ContentIdentityContract
                {
                    Algorithm = 1,
                    Digest = root
                }
            },
            CancellationToken.None);
        Assert.IsTrue(announced);

        ResourceLookupResponse? lookup =
            await context.Service.LookupResourceAsync(
                1,
                new string('A', 64),
                null,
                CancellationToken.None);
        Assert.IsNotNull(lookup);
        Assert.HasCount(1, lookup.Candidates);
        Assert.AreEqual(4096, lookup.Candidates[0].Size);
        Assert.IsTrue(lookup.Candidates[0].Identities.Any(
            identity => identity.Algorithm == 1
                && identity.Digest == root));
        Assert.IsTrue(lookup.Candidates[0].Identities.Any(
            identity => identity.Algorithm == 3
                && identity.Digest == sha256));
    }

    [TestMethod]
    public async Task ResourceAnnouncementRequiresCurrentInventoryOwnership()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var context = await CreateRegisteredNodeAsync(
            connection,
            "resource-node-0002",
            new string('3', 64),
            new string('4', 64));
        await using ContentDirectoryDbContext db = context.Db;

        await Assert.ThrowsAsync<ContentDirectoryConflictException>(
            () => context.Service.AnnounceResourceAsync(
                "resource-node-0002",
                new ResourceAnnouncementRequest
                {
                    LeaseId = context.Registration.LeaseId,
                    ResourceKey = new ResourceKeyContract
                    {
                        Algorithm = 1,
                        Digest = new string('b', 64)
                    },
                    ContentIdentity = new ContentIdentityContract
                    {
                        Algorithm = 3,
                        Digest = new string('9', 64)
                    }
                },
                CancellationToken.None));
    }

    [TestMethod]
    public async Task SameNodeCanRemapChangedResource()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<ContentDirectoryDbContext> dbOptions =
            new DbContextOptionsBuilder<ContentDirectoryDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new ContentDirectoryDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var service = new ContentDirectoryService(
            db,
            Microsoft.Extensions.Options.Options.Create(
                new ContentDirectoryOptions()));

        string firstRoot = new string('5', 64);
        string secondRoot = new string('6', 64);
        RegisterContentNodeResponse registration = await service.RegisterAsync(
            new RegisterContentNodeRequest
            {
                NodeId = "resource-node-remap",
                Generation = 1,
                RegistrationId = Guid.NewGuid(),
                Contents =
                [
                    new ContentAnnouncementRequest
                    {
                        Size = 100,
                        Identities =
                        [
                            new ContentIdentityContract
                            {
                                Algorithm = 1,
                                Digest = firstRoot
                            }
                        ]
                    },
                    new ContentAnnouncementRequest
                    {
                        Size = 200,
                        Identities =
                        [
                            new ContentIdentityContract
                            {
                                Algorithm = 1,
                                Digest = secondRoot
                            }
                        ]
                    }
                ]
            },
            IPAddress.Parse("203.0.113.51"),
            CancellationToken.None);

        ResourceKeyContract key = new()
        {
            Algorithm = 1,
            Digest = new string('c', 64)
        };

        Assert.IsTrue(await service.AnnounceResourceAsync(
            "resource-node-remap",
            new ResourceAnnouncementRequest
            {
                LeaseId = registration.LeaseId,
                ResourceKey = key,
                ContentIdentity = new ContentIdentityContract
                {
                    Algorithm = 1,
                    Digest = firstRoot
                }
            },
            CancellationToken.None));

        Assert.IsTrue(await service.AnnounceResourceAsync(
            "resource-node-remap",
            new ResourceAnnouncementRequest
            {
                LeaseId = registration.LeaseId,
                ResourceKey = key,
                ContentIdentity = new ContentIdentityContract
                {
                    Algorithm = 1,
                    Digest = secondRoot
                }
            },
            CancellationToken.None));

        ResourceLookupResponse? lookup = await service.LookupResourceAsync(
            1,
            key.Digest,
            null,
            CancellationToken.None);
        Assert.IsNotNull(lookup);
        Assert.HasCount(1, lookup.Candidates);
        Assert.AreEqual(200, lookup.Candidates[0].Size);
        Assert.IsTrue(lookup.Candidates[0].Identities.Any(
            identity => identity.Digest == secondRoot));
    }

    [TestMethod]
    public async Task InventoryRemovalInvalidatesResourceObservation()
    {
        await using SqliteConnection connection =
            new("Data Source=:memory:");
        await connection.OpenAsync();

        string root = new string('7', 64);
        string sha256 = new string('8', 64);
        var context = await CreateRegisteredNodeAsync(
            connection,
            "resource-node-removal",
            root,
            sha256);
        await using ContentDirectoryDbContext db = context.Db;

        ResourceKeyContract key = new()
        {
            Algorithm = 1,
            Digest = new string('d', 64)
        };

        Assert.IsTrue(await context.Service.AnnounceResourceAsync(
            "resource-node-removal",
            new ResourceAnnouncementRequest
            {
                LeaseId = context.Registration.LeaseId,
                ResourceKey = key,
                ContentIdentity = new ContentIdentityContract
                {
                    Algorithm = 1,
                    Digest = root
                }
            },
            CancellationToken.None));

        Assert.IsNotNull(await context.Service.LookupResourceAsync(
            1,
            key.Digest,
            null,
            CancellationToken.None));

        RegisterContentNodeResponse refreshed =
            await context.Service.RegisterAsync(
                new RegisterContentNodeRequest
                {
                    NodeId = "resource-node-removal",
                    Generation = 2,
                    RegistrationId = Guid.NewGuid(),
                    PreviousLeaseId = context.Registration.LeaseId,
                    Contents = []
                },
                IPAddress.Parse("203.0.113.50"),
                CancellationToken.None);
        Assert.AreEqual(0, refreshed.ContentCount);

        Assert.IsNull(await context.Service.LookupResourceAsync(
            1,
            key.Digest,
            null,
            CancellationToken.None));
    }
}
