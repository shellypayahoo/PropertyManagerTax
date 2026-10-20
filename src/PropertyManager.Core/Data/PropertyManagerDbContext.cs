using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Data;

public class PropertyManagerDbContext(DbContextOptions<PropertyManagerDbContext> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<MaintenanceRequest> MaintenanceRequests => Set<MaintenanceRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Property>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Street).HasMaxLength(200).IsRequired();
            e.Ignore(p => p.FullAddress);
        });

        modelBuilder.Entity<Unit>(e =>
        {
            e.Property(u => u.Label).HasMaxLength(20).IsRequired();
            e.HasIndex(u => new { u.PropertyId, u.Label }).IsUnique();
            e.Ignore(u => u.DisplayName);
        });

        modelBuilder.Entity<Tenant>(e =>
        {
            e.Property(t => t.FirstName).HasMaxLength(100).IsRequired();
            e.Property(t => t.LastName).HasMaxLength(100).IsRequired();
            e.Ignore(t => t.FullName);
        });

        modelBuilder.Entity<Lease>(e =>
        {
            e.HasOne(l => l.Tenant).WithMany(t => t.Leases).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Expense>(e =>
        {
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(40);
            e.HasOne(x => x.Unit).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.MaintenanceRequest).WithMany(m => m.Expenses).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.Date);
        });

        modelBuilder.Entity<MaintenanceRequest>(e =>
        {
            e.Property(m => m.Title).HasMaxLength(200).IsRequired();
            e.Property(m => m.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(m => m.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.Ignore(m => m.IsOpen);
        });
    }
}
