using HooviePack.Files.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HooviePack.Files.Api.Infrastructure.Data;

public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options) : DbContext(options)
{
    public DbSet<FileRecord> Files => Set<FileRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("files");
        modelBuilder.Entity<FileRecord>(entity =>
        {
            entity.ToTable("Files");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.StorageKey).IsUnique();
            entity.HasIndex(x => x.LegacySourcePath).IsUnique();
            entity.Property(x => x.StorageKey).HasMaxLength(500);
            entity.Property(x => x.OriginalFileName).HasMaxLength(255);
            entity.Property(x => x.ContentType).HasMaxLength(100);
            entity.Property(x => x.UploadTokenHash).HasMaxLength(32);
            entity.Property(x => x.LegacySourcePath).HasMaxLength(500);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
    }
}
