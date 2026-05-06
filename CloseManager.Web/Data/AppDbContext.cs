using Microsoft.EntityFrameworkCore;

namespace CloseManager.Web.Data;

/// <summary>
/// EF Core database context.
/// Phase 1: User table only. Full schema added in Phase 2.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Entities.User> Users => Set<Entities.User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Entities.User>(e =>
        {
            e.ToTable("User");
            e.HasKey(u => u.UserId);
            e.Property(u => u.EntraObjectId).IsRequired();
            e.HasIndex(u => u.EntraObjectId).IsUnique();
            e.Property(u => u.Upn).HasMaxLength(256).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(u => u.RowVersion).IsRowVersion();
        });
    }
}
