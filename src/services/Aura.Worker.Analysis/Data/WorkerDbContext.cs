using Microsoft.EntityFrameworkCore;
using Aura.Worker.Analysis.Data.Entities;

namespace Aura.Worker.Analysis.Data;

public class WorkerDbContext : DbContext
{
    public WorkerDbContext(DbContextOptions<WorkerDbContext> options) : base(options)
    {
    }

    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<AnalysisRecord> Analyses => Set<AnalysisRecord>();
    public DbSet<AnalysisRequestRecord> AnalysisRequests => Set<AnalysisRequestRecord>();
    public DbSet<OutboxRecord> OutboxMessages => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ADR-005 D3: Worker map đúng schema canonical "analysis"
        modelBuilder.HasDefaultSchema("analysis");

        modelBuilder.Entity<JobRecord>(entity =>
        {
            entity.ToTable("jobs", "analysis");
            entity.HasKey(e => e.JobId);
            entity.HasIndex(e => new { e.AnalysisId, e.AttemptNo });
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<AnalysisRecord>(entity =>
        {
            entity.ToTable("images", "analysis");
            entity.HasKey(e => e.AnalysisId);
            entity.HasIndex(e => e.BatchId);
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<AnalysisRequestRecord>(entity =>
        {
            entity.ToTable("analysis_requests", "analysis");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<OutboxRecord>(entity =>
        {
            entity.ToTable("outbox_events", "analysis");
            entity.HasKey(e => e.OutboxId);
            entity.Ignore(e => e.Status);
            entity.Ignore(e => e.AvailableAt);
        });
    }
}
