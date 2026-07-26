using Microsoft.EntityFrameworkCore;
using OpenNet.Server.Domain;

namespace OpenNet.Server.Infrastructure;

public sealed class TraversalDbContext(DbContextOptions<TraversalDbContext> options)
    : DbContext(options)
{
    public DbSet<TraversalServer> TraversalServers => Set<TraversalServer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TraversalServer>(entity =>
        {
            entity.ToTable("TraversalServers");
            entity.HasKey(server => server.Id);
            entity.Property(server => server.Name).HasMaxLength(100).IsRequired();
            entity.Property(server => server.IPv4Address).HasMaxLength(15).IsRequired();
            entity.Property(server => server.IPv6Address).HasMaxLength(45);
            entity.Property(server => server.AlternateIPv4Address).HasMaxLength(15);
            entity.HasIndex(server => new { server.IPv4Address, server.ApiPort }).IsUnique();
            entity.HasIndex(server => new { server.IsEnabled, server.Priority, server.LastHeartbeatUtc });
        });
    }
}
