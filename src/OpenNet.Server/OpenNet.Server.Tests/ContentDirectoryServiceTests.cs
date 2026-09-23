using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;
using OpenNet.Server.Infrastructure;
using OpenNet.Server.Options;

namespace OpenNet.Server.Tests;

[TestClass]
public sealed class ContentDirectoryServiceTests
{
    [TestMethod]
    public async Task RegisterAndLookupRoundTripsMultipleAliases()
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
            Microsoft.Extensions.Options.Options.Create(new ContentDirectoryOptions
            {
                LeaseLifetimeSeconds = 300,
                MaxInventoryItems = 100,
                MaxLookupPeers = 20
            }));

        RegisterContentNodeResponse registration = await service.RegisterAsync(
            new RegisterContentNodeRequest
            {
                NodeId = "test-node-0001",
                Generation = 1,
                RegistrationId = Guid.NewGuid(),
                Endpoints =
                [
                    new PeerEndpointRequest
                    {
                        Transport = PeerTransportContract.Tcp,
                        AddressFamily = PeerAddressFamilyContract.Ipv4,
                        Port = 6881
                    },
                    new PeerEndpointRequest
                    {
                        Transport = PeerTransportContract.Utp,
                        AddressFamily = PeerAddressFamilyContract.Ipv4,
                        Port = 6881
                    }
                ],
                Contents =
                [
                    new ContentAnnouncementRequest
                    {
                        Size = 123456,
                        Identities =
                        [
                            new ContentIdentityContract
                            {
                                Algorithm = 1,
                                Digest = new string('a', 64)
                            },
                            new ContentIdentityContract
                            {
                                Algorithm = 128,
                                Digest = new string('b', 40)
                            }
                        ]
                    }
                ]
            },
            IPAddress.Parse("203.0.113.10"),
            CancellationToken.None);

        Assert.AreEqual(1, registration.ContentCount);

        ContentLookupResponse? lookup = await service.LookupAsync(
            128,
            new string('B', 40),
            null,
            null,
            false,
            CancellationToken.None);

        Assert.IsNotNull(lookup);
        Assert.AreEqual(123456, lookup.Size);
        Assert.AreEqual(1, lookup.Peers.Count);
        Assert.AreEqual("test-node-0001", lookup.Peers[0].NodeId);
        Assert.IsFalse(lookup.Peers[0].Ready);
        Assert.IsTrue(lookup.Peers[0].Endpoints.All(
            endpoint => endpoint.Address == "203.0.113.10"));
        Assert.IsTrue(lookup.Peers[0].Endpoints.Any(
            endpoint => endpoint.Transport == PeerTransportContract.Tcp));
        Assert.IsTrue(lookup.Peers[0].Endpoints.Any(
            endpoint => endpoint.Transport == PeerTransportContract.Utp));

        ContentHeartbeatResponse? heartbeat = await service.HeartbeatAsync(
            "test-node-0001",
            registration.LeaseId,
            CancellationToken.None);
        Assert.IsNotNull(heartbeat);
        Assert.IsTrue(heartbeat.LeaseExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task RegistrationRejectsDuplicateAliasAcrossInventoryItems()
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
            Microsoft.Extensions.Options.Options.Create(new ContentDirectoryOptions()));

        var request = new RegisterContentNodeRequest
        {
            NodeId = "test-node-0002",
            Generation = 1,
            RegistrationId = Guid.NewGuid(),
            Contents =
            [
                new ContentAnnouncementRequest
                {
                    Size = 1,
                    Identities =
                    [
                        new ContentIdentityContract
                        {
                            Algorithm = 1,
                            Digest = new string('c', 64)
                        }
                    ]
                },
                new ContentAnnouncementRequest
                {
                    Size = 2,
                    Identities =
                    [
                        new ContentIdentityContract
                        {
                            Algorithm = 1,
                            Digest = new string('c', 64)
                        }
                    ]
                }
            ]
        };

        await Assert.ThrowsAsync<ContentDirectoryConflictException>(
            () => service.RegisterAsync(
                request,
                IPAddress.Parse("203.0.113.11"),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task SameRegistrationCanRetryAfterResponseLoss()
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
            Microsoft.Extensions.Options.Options.Create(new ContentDirectoryOptions()));

        Guid registrationId = Guid.NewGuid();
        var request = new RegisterContentNodeRequest
        {
            NodeId = "test-node-retry",
            Generation = 1,
            RegistrationId = registrationId,
            Endpoints =
            [
                new PeerEndpointRequest
                {
                    Transport = PeerTransportContract.Tcp,
                    Port = 6881
                }
            ],
            Contents =
            [
                new ContentAnnouncementRequest
                {
                    Size = 42,
                    Identities =
                    [
                        new ContentIdentityContract
                        {
                            Algorithm = 1,
                            Digest = new string('d', 64)
                        }
                    ]
                }
            ]
        };

        RegisterContentNodeResponse first = await service.RegisterAsync(
            request,
            IPAddress.Parse("203.0.113.20"),
            CancellationToken.None);

        // Simulate the server committing the first request while its HTTP
        // response is lost. The client repeats the same generation and
        // RegistrationId without knowing the newly-issued lease.
        RegisterContentNodeResponse retry = await service.RegisterAsync(
            request,
            IPAddress.Parse("203.0.113.20"),
            CancellationToken.None);

        Assert.AreEqual(1, retry.AcceptedGeneration);
        Assert.AreEqual(1, retry.ContentCount);
        Assert.AreNotEqual(first.LeaseId, retry.LeaseId);
    }

    [TestMethod]
    public async Task SameGenerationWithDifferentRegistrationIsRejected()
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
            Microsoft.Extensions.Options.Options.Create(new ContentDirectoryOptions()));

        RegisterContentNodeRequest Build(Guid registrationId) => new()
        {
            NodeId = "test-node-generation",
            Generation = 1,
            RegistrationId = registrationId,
            Contents =
            [
                new ContentAnnouncementRequest
                {
                    Size = 1,
                    Identities =
                    [
                        new ContentIdentityContract
                        {
                            Algorithm = 3,
                            Digest = new string('e', 64)
                        }
                    ]
                }
            ]
        };

        await service.RegisterAsync(
            Build(Guid.NewGuid()),
            IPAddress.Parse("203.0.113.21"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ContentDirectoryConflictException>(
            () => service.RegisterAsync(
                Build(Guid.NewGuid()),
                IPAddress.Parse("203.0.113.21"),
                CancellationToken.None));
    }
}


[TestClass]
public sealed class ContentDirectoryWakeupTests
{
    private static byte[] BuildCanonicalManifest(
        long size,
        string rootHex,
        out string infoHash)
    {
        byte[] root = Convert.FromHexString(rootHex);
        using var info = new MemoryStream();
        void WriteAscii(Stream stream, string value)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
            stream.Write(bytes);
        }

        WriteAscii(info, "d9:file treed7:contentd0:d6:lengthi");
        WriteAscii(info, size.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        WriteAscii(info, "e11:pieces root32:");
        info.Write(root);
        WriteAscii(info,
            "eee12:meta versioni2e4:name18:OpenNet.Content.v1"
            + "12:piece lengthi1048576ee");

        byte[] infoBytes = info.ToArray();
        infoHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(infoBytes))
            .ToLowerInvariant();

        using var torrent = new MemoryStream();
        WriteAscii(torrent, "d4:info");
        torrent.Write(infoBytes);
        WriteAscii(torrent, "12:piece layersdee");
        return torrent.ToArray();
    }

    [TestMethod]
    public async Task LookupWakeupCompletionPublishesReadyCanonicalManifest()
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

        RegisterContentNodeResponse registration = await service.RegisterAsync(
            new RegisterContentNodeRequest
            {
                NodeId = "test-node-wakeup",
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
                        Size = 1024,
                        Identities =
                        [
                            new ContentIdentityContract
                            {
                                Algorithm = 1,
                                Digest = new string('f', 64)
                            }
                        ]
                    }
                ]
            },
            IPAddress.Parse("203.0.113.30"),
            CancellationToken.None);

        ContentLookupResponse? pending = await service.LookupAsync(
            1,
            new string('f', 64),
            20,
            null,
            true,
            CancellationToken.None);
        Assert.IsNotNull(pending);
        Assert.IsFalse(pending.ManifestAvailable);
        Assert.IsTrue(pending.Peers.All(peer => !peer.Ready));

        IReadOnlyList<ContentWakeupResponse>? wakeups =
            await service.PollWakeupsAsync(
                "test-node-wakeup",
                registration.LeaseId,
                null,
                CancellationToken.None);
        Assert.IsNotNull(wakeups);
        Assert.HasCount(1, wakeups);

        byte[] manifest = BuildCanonicalManifest(
            1024,
            new string('f', 64),
            out string canonicalInfoHash);
        bool completed = await service.CompleteWakeupAsync(
            "test-node-wakeup",
            wakeups[0].WakeRequestId,
            new CompleteContentWakeupRequest
            {
                LeaseId = registration.LeaseId,
                Succeeded = true,
                CanonicalProtocolVersion = 1,
                CanonicalInfoHashV2 = canonicalInfoHash,
                CanonicalTorrentBase64 = Convert.ToBase64String(manifest)
            },
            CancellationToken.None);
        Assert.IsTrue(completed);

        ContentLookupResponse? ready = await service.LookupAsync(
            1,
            new string('f', 64),
            20,
            null,
            true,
            CancellationToken.None);
        Assert.IsNotNull(ready);
        Assert.IsTrue(ready.ManifestAvailable);
        Assert.AreEqual(canonicalInfoHash, ready.CanonicalInfoHashV2);
        Assert.IsTrue(ready.Peers.Any(peer => peer.Ready));

        byte[]? stored = await service.GetCanonicalManifestAsync(
            ready.ContentId,
            CancellationToken.None);
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(manifest, stored);
    }

    [TestMethod]
    public async Task CompleteWakeupRejectsCanonicalManifestWithoutBep52Root()
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

        RegisterContentNodeResponse registration = await service.RegisterAsync(
            new RegisterContentNodeRequest
            {
                NodeId = "test-node-no-v2-root",
                Generation = 1,
                RegistrationId = Guid.NewGuid(),
                Contents =
                [
                    new ContentAnnouncementRequest
                    {
                        Size = 1024,
                        Identities =
                        [
                            new ContentIdentityContract
                            {
                                Algorithm = 3,
                                Digest = new string('d', 64)
                            }
                        ]
                    }
                ]
            },
            IPAddress.Parse("203.0.113.31"),
            CancellationToken.None);

        ContentLookupResponse? lookup = await service.LookupAsync(
            3,
            new string('d', 64),
            20,
            null,
            true,
            CancellationToken.None);
        Assert.IsNotNull(lookup);

        IReadOnlyList<ContentWakeupResponse>? wakeups =
            await service.PollWakeupsAsync(
                "test-node-no-v2-root",
                registration.LeaseId,
                null,
                CancellationToken.None);
        Assert.IsNotNull(wakeups);
        Assert.HasCount(1, wakeups);

        await Assert.ThrowsAsync<ContentDirectoryConflictException>(
            () => service.CompleteWakeupAsync(
                "test-node-no-v2-root",
                wakeups[0].WakeRequestId,
                new CompleteContentWakeupRequest
                {
                    LeaseId = registration.LeaseId,
                    Succeeded = true,
                    CanonicalProtocolVersion = 1,
                    CanonicalInfoHashV2 = new string('a', 64),
                    CanonicalTorrentBase64 =
                        Convert.ToBase64String([1, 2, 3])
                },
                CancellationToken.None));
    }

    [TestMethod]
    public async Task InventoryRefreshPreservesReadyStateForUnchangedContent()
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

        RegisterContentNodeRequest Build(
            long generation,
            Guid registrationId,
            Guid? previousLeaseId) => new()
        {
            NodeId = "test-node-preserve-ready",
            Generation = generation,
            RegistrationId = registrationId,
            PreviousLeaseId = previousLeaseId,
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
                    Size = 2048,
                    Identities =
                    [
                        new ContentIdentityContract
                        {
                            Algorithm = 1,
                            Digest = new string('9', 64)
                        }
                    ]
                }
            ]
        };

        RegisterContentNodeResponse first = await service.RegisterAsync(
            Build(1, Guid.NewGuid(), null),
            IPAddress.Parse("203.0.113.40"),
            CancellationToken.None);

        ContentLookupResponse? pending = await service.LookupAsync(
            1,
            new string('9', 64),
            20,
            null,
            true,
            CancellationToken.None);
        Assert.IsNotNull(pending);

        IReadOnlyList<ContentWakeupResponse>? wakeups =
            await service.PollWakeupsAsync(
                "test-node-preserve-ready",
                first.LeaseId,
                null,
                CancellationToken.None);
        Assert.IsNotNull(wakeups);
        Assert.HasCount(1, wakeups);

        byte[] manifest = BuildCanonicalManifest(
            2048,
            new string('9', 64),
            out string canonicalInfoHash);
        Assert.IsTrue(await service.CompleteWakeupAsync(
            "test-node-preserve-ready",
            wakeups[0].WakeRequestId,
            new CompleteContentWakeupRequest
            {
                LeaseId = first.LeaseId,
                Succeeded = true,
                CanonicalProtocolVersion = 1,
                CanonicalInfoHashV2 = canonicalInfoHash,
                CanonicalTorrentBase64 =
                    Convert.ToBase64String(manifest)
            },
            CancellationToken.None));

        RegisterContentNodeResponse refreshed = await service.RegisterAsync(
            Build(2, Guid.NewGuid(), first.LeaseId),
            IPAddress.Parse("203.0.113.40"),
            CancellationToken.None);

        ContentLookupResponse? ready = await service.LookupAsync(
            1,
            new string('9', 64),
            20,
            null,
            false,
            CancellationToken.None);
        Assert.IsNotNull(ready);
        Assert.IsTrue(ready.Peers.Any(peer =>
            peer.NodeId == "test-node-preserve-ready" && peer.Ready));
        Assert.AreEqual(2, refreshed.AcceptedGeneration);
    }
}
