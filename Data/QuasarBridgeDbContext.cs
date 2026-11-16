using Microsoft.EntityFrameworkCore;
using QuasarBridge.Data.Entities;

namespace QuasarBridge.Data;

public class QuasarBridgeDbContext(DbContextOptions<QuasarBridgeDbContext> options) : DbContext(options)
{
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<InterceptEvent> InterceptEvents => Set<InterceptEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoutingRule>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.Enabled, x.Priority });
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Route).HasMaxLength(32).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.ActionPayload).HasMaxLength(1024);
            e.Property(x => x.Description).HasMaxLength(512);
            e.Property(x => x.Regex).HasMaxLength(512);
        });

        modelBuilder.Entity<InterceptEvent>(e =>
        {
            e.HasIndex(x => x.At);
            e.Property(x => x.Direction).HasMaxLength(64);
            e.Property(x => x.RuleName).HasMaxLength(128);
            e.Property(x => x.Route).HasMaxLength(32);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ActionResult).HasMaxLength(512);
            e.Property(x => x.Preview).HasMaxLength(2000);
        });
    }
}
