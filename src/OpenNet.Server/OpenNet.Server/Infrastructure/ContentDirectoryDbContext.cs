using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenNet.Server.Domain;

namespace OpenNet.Server.Infrastructure;

public sealed class ContentDirectoryDbContext(DbContextOptions<ContentDirectoryDbContext> options)
    : DbContext(options)
{
    public DbSet<ContentNode> ContentNodes => Set<ContentNode>();
    public DbSet<ContentNodeEndpoint> ContentNodeEndpoints => Set<ContentNodeEndpoint>();
    public DbSet<ContentObject> Contents => Set<ContentObject>();
    public DbSet<ContentIdentityRecord> ContentIdentities => Set<ContentIdentityRecord>();
    public DbSet<ContentPresence> ContentPresences => Set<ContentPresence>();
    public DbSet<ResourceObservation> ResourceObservations => Set<ResourceObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite does not natively translate relational comparisons over
        // DateTimeOffset. Store all directory timestamps as UTC Unix
        // milliseconds so lease/readiness filters remain server-side and
        // behave identically on SQLite and MySQL.
        var utcInstant = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableUtcInstant = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue
                ? value.Value.ToUnixTimeMilliseconds()
                : null,
            value => value.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
                : null);
        modelBuilder.Entity<ContentNode>(entity =>
        {
            entity.ToTable("ContentNodes");
            entity.HasKey(node => node.NodeId);
            entity.Property(node => node.NodeId).HasMaxLength(128);
            entity.Property(node => node.LeaseExpiresUtc)
                .HasConversion(utcInstant);
            entity.Property(node => node.LastSeenUtc)
                .HasConversion(utcInstant);
            entity.Property(node => node.CreatedUtc)
                .HasConversion(utcInstant);
            entity.HasIndex(node => node.LeaseExpiresUtc);
            entity.HasIndex(node => node.LastRegistrationId);
        });

        modelBuilder.Entity<ContentNodeEndpoint>(entity =>
        {
            entity.ToTable("ContentNodeEndpoints");
            entity.HasKey(endpoint => endpoint.Id);
            entity.Property(endpoint => endpoint.Address).HasMaxLength(45).IsRequired();
            entity.Property(endpoint => endpoint.Transport).HasConversion<byte>();
            entity.HasIndex(endpoint => new
            {
                endpoint.NodeId,
                endpoint.Address,
                endpoint.Port,
                endpoint.Transport
            }).IsUnique();
            entity.HasOne(endpoint => endpoint.Node)
                .WithMany(node => node.Endpoints)
                .HasForeignKey(endpoint => endpoint.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentObject>(entity =>
        {
            entity.ToTable("Contents");
            entity.HasKey(content => content.Id);
            entity.Property(content => content.CreatedUtc)
                .HasConversion(utcInstant);
            entity.HasIndex(content => content.Size);
            entity.Property(content => content.CanonicalInfoHashV2).HasMaxLength(64);
            entity.Property(content => content.CanonicalTorrent);
        });

        modelBuilder.Entity<ContentIdentityRecord>(entity =>
        {
            entity.ToTable("ContentIdentities");
            entity.HasKey(identity => identity.Id);
            entity.Property(identity => identity.Algorithm).HasConversion<short>();
            entity.Property(identity => identity.DigestHex).HasMaxLength(64).IsRequired();
            entity.HasIndex(identity => new { identity.Algorithm, identity.DigestHex }).IsUnique();
            entity.HasOne(identity => identity.Content)
                .WithMany(content => content.Identities)
                .HasForeignKey(identity => identity.ContentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentPresence>(entity =>
        {
            entity.ToTable("ContentPresences");
            entity.HasKey(presence => new { presence.NodeId, presence.ContentId });
            entity.Property(presence => presence.WakeRequestedUtc)
                .HasConversion(nullableUtcInstant);
            entity.Property(presence => presence.NextWakeAllowedUtc)
                .HasConversion(nullableUtcInstant);
            entity.Property(presence => presence.SeedReadyUntilUtc)
                .HasConversion(nullableUtcInstant);
            entity.Property(presence => presence.UpdatedUtc)
                .HasConversion(utcInstant);
            entity.HasIndex(presence => presence.ContentId);
            entity.HasIndex(presence => presence.WakeRequestId);
            entity.HasIndex(presence => presence.SeedReadyUntilUtc);
            entity.HasOne(presence => presence.Node)
                .WithMany(node => node.Presences)
                .HasForeignKey(presence => presence.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(presence => presence.Content)
                .WithMany(content => content.Presences)
                .HasForeignKey(presence => presence.ContentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ResourceObservation>(entity =>
        {
            entity.ToTable("ResourceObservations");
            entity.HasKey(observation => new
            {
                observation.Algorithm,
                observation.DigestHex,
                observation.NodeId
            });
            entity.Property(observation => observation.Algorithm)
                .HasConversion<short>();
            entity.Property(observation => observation.DigestHex)
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(observation => observation.ObservedUtc)
                .HasConversion(utcInstant);
            entity.Property(observation => observation.ExpiresUtc)
                .HasConversion(utcInstant);
            entity.HasIndex(observation => new
            {
                observation.Algorithm,
                observation.DigestHex,
                observation.ExpiresUtc
            });
            entity.HasIndex(observation => observation.ContentId);
            entity.HasOne(observation => observation.Node)
                .WithMany(node => node.ResourceObservations)
                .HasForeignKey(observation => observation.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(observation => observation.Content)
                .WithMany(content => content.ResourceObservations)
                .HasForeignKey(observation => observation.ContentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
