using Microsoft.EntityFrameworkCore;

namespace Permits.Api.Data;

public sealed class PermitsDbContext(DbContextOptions<PermitsDbContext> options) : DbContext(options)
{
    public DbSet<Permit> Permits => Set<Permit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Mirrors Data/schema.sql, which is the source of truth for the database.
        modelBuilder.Entity<Permit>(entity =>
        {
            entity.ToTable("Permits", "dbo");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.PermitNumber).HasMaxLength(50).IsRequired();
            entity.HasIndex(p => p.PermitNumber).IsUnique();
            entity.Property(p => p.Address).HasMaxLength(200);
            entity.Property(p => p.Folio).HasMaxLength(20);
            entity.Property(p => p.PermitType).HasMaxLength(10);
            entity.Property(p => p.Description).HasMaxLength(400);
            entity.Property(p => p.ResidentialCommercial).HasMaxLength(10);
            entity.Property(p => p.Status).HasMaxLength(20);
            entity.Property(p => p.EstimatedValue).HasPrecision(14, 2);
            entity.Property(p => p.ContractorName).HasMaxLength(200);
            entity.Property(p => p.Location).HasColumnType("geography");
        });
    }
}
