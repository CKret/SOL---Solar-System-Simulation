using Microsoft.EntityFrameworkCore;
using Sol.Api.Data.Entities;

namespace Sol.Api.Data;

// Read context — uses EphemerisDb connection string (sol_reader login, SELECT only).
public sealed class SolReadDbContext(DbContextOptions<SolReadDbContext> options) : DbContext(options)
{
    public DbSet<BodyEntity>              Bodies              => Set<BodyEntity>();
    public DbSet<EphemerisSampleEntity>   EphemerisSamples    => Set<EphemerisSampleEntity>();
    public DbSet<EphemerisImportLogEntity> EphemerisImportLog => Set<EphemerisImportLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => SolModelConfig.Configure(modelBuilder);
}

// Write context — uses EphemerisDbWrite connection string (sol_user login, full DML).
public sealed class SolWriteDbContext(DbContextOptions<SolWriteDbContext> options) : DbContext(options)
{
    public DbSet<BodyEntity>               Bodies              => Set<BodyEntity>();
    public DbSet<EphemerisSampleEntity>    EphemerisSamples    => Set<EphemerisSampleEntity>();
    public DbSet<EphemerisImportLogEntity> EphemerisImportLog  => Set<EphemerisImportLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => SolModelConfig.Configure(modelBuilder);
}

internal static class SolModelConfig
{
    internal static void Configure(ModelBuilder m)
    {
        m.Entity<BodyEntity>(e =>
        {
            e.ToTable("Bodies", "dbo");
            e.HasKey(b => b.BodyId);
            e.Property(b => b.BodyId).UseIdentityColumn();
            e.Property(b => b.Slug).HasMaxLength(64);
            e.Property(b => b.DisplayName).HasMaxLength(128);
            e.Property(b => b.Kind).HasMaxLength(32);
            e.Property(b => b.Source).HasMaxLength(32);
            e.Property(b => b.JplHorizonsId).HasMaxLength(64);
            e.Property(b => b.SbdbDesig).HasMaxLength(64);
            e.Property(b => b.EphemerisMinStr).HasMaxLength(48);
            e.Property(b => b.EphemerisMaxStr).HasMaxLength(48);
            e.Property(b => b.PhysicsJson).HasColumnType("nvarchar(max)");
            e.Property(b => b.CreatedUtc).HasColumnType("datetime2(0)");
            e.Property(b => b.UpdatedUtc).HasColumnType("datetime2(0)");
            e.HasIndex(b => b.Slug).IsUnique().HasDatabaseName("UQ_Bodies_Slug");
            e.HasOne(b => b.Parent)
             .WithMany()
             .HasForeignKey(b => b.ParentBodyId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<EphemerisSampleEntity>(e =>
        {
            e.ToTable("EphemerisSamples", "dbo");
            e.HasKey(s => new { s.SampleJd, s.BodyId });
            e.Property(s => s.Frame).HasMaxLength(64);
            e.Property(s => s.Source).HasMaxLength(64);
            e.Property(s => s.CreatedUtc).HasColumnType("datetime2(0)");
            e.HasOne(s => s.Body)
             .WithMany()
             .HasForeignKey(s => s.BodyId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<EphemerisImportLogEntity>(e =>
        {
            e.ToTable("EphemerisImportLog", "dbo");
            e.HasKey(l => new { l.BodyId, l.StartJd, l.EndJd });
            e.Property(l => l.ImportedUtc).HasColumnType("datetime2(3)");
            e.HasOne(l => l.Body)
             .WithMany()
             .HasForeignKey(l => l.BodyId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
