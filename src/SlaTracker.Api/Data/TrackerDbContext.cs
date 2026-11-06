using Microsoft.EntityFrameworkCore;
using SlaTracker.Api.Domain;

namespace SlaTracker.Api.Data;

/// <summary>
/// Maps the domain onto the schema created by the T-SQL scripts. EF does not create or change tables:
/// the scripts own the schema, and the integration tests fail if the two drift apart.
/// </summary>
public class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("dbo");

        b.Entity<SlaPolicy>(e =>
        {
            e.ToTable("SlaPolicy");
            e.HasKey(x => x.Severity);
            e.Property(x => x.Severity).HasConversion<string>().HasColumnType("char(2)");
        });

        b.Entity<Release>(e =>
        {
            e.ToTable("Release");
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(30).IsUnicode(false);
            e.HasIndex(x => x.Version).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.Notes).HasMaxLength(2000);
        });

        b.Entity<Incident>(e =>
        {
            e.ToTable("Incident");
            e.HasKey(x => x.Id);
            e.Property(x => x.Ref).HasMaxLength(20).IsUnicode(false);
            e.HasIndex(x => x.Ref).IsUnique();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Service).HasMaxLength(100);
            e.Property(x => x.Severity).HasConversion<string>().HasColumnType("char(2)");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12).IsUnicode(false);
            e.Property(x => x.Assignee).HasMaxLength(100);
            e.Property(x => x.ResolutionSummary).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();     // optimistic concurrency
            e.HasOne(x => x.CausedByRelease).WithMany().HasForeignKey(x => x.CausedByReleaseId);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IncidentEvent>(e =>
        {
            e.ToTable("IncidentEvent");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Actor).HasMaxLength(100);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.HasSequence<long>("SeqIncidentRef", "dbo").StartsAt(1000).IncrementsBy(1);
    }
}
