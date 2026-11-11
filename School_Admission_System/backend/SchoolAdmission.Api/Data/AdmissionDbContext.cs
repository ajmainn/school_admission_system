using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api.Domain;

namespace SchoolAdmission.Api.Data;

public sealed class AdmissionDbContext(DbContextOptions<AdmissionDbContext> options) : DbContext(options)
{
    public DbSet<AdmissionClass> Classes => Set<AdmissionClass>();
    public DbSet<AdmissionHold> Holds => Set<AdmissionHold>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdmissionClass>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.HasMany(x => x.Holds).WithOne(x => x.AdmissionClass)
                .HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AdmissionHold>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.StudentId).IsRequired().HasMaxLength(17);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(x => new { x.ClassId, x.Status, x.ExpiresAtUtc });
            entity.HasIndex(x => new { x.StudentId, x.ClassId });
        });

        // Allocation and duplicate checks are serialized by the class row lock in AdmissionService.
    }
}
