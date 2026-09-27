using Microsoft.EntityFrameworkCore;
using PortableAgent.Persistence.Sqlite.Entities;

namespace PortableAgent.Persistence.Sqlite;

public sealed class PortableAgentDbContext(DbContextOptions<PortableAgentDbContext> options) : DbContext(options)
{
    public DbSet<RunStateEntity> Runs => Set<RunStateEntity>();
    public DbSet<ExecutionEventEntity> ExecutionEvents => Set<ExecutionEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<RunStateEntity>();
        run.ToTable("Runs");
        run.HasKey(r => r.RunId);
        run.Property(r => r.RuntimeDefinitionId).UseCollation("BINARY");
        run.Property(r => r.Version).IsConcurrencyToken();
        var events = modelBuilder.Entity<ExecutionEventEntity>();
        events.ToTable("ExecutionEvents");
        events.HasKey(e => e.EventId);
        events.HasIndex(e => new { e.RunId, e.Sequence }).IsUnique();
        events.HasOne<RunStateEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}
