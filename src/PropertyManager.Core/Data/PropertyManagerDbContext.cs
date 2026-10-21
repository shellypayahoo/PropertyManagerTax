using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Data;

public class PropertyManagerDbContext(DbContextOptions<PropertyManagerDbContext> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<LeaseDocument> LeaseDocuments => Set<LeaseDocument>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<MaintenanceRequest> MaintenanceRequests => Set<MaintenanceRequest>();
    public DbSet<RentPayment> RentPayments => Set<RentPayment>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<ImportedTransaction> ImportedTransactions => Set<ImportedTransaction>();
    public DbSet<CategorizationRule> CategorizationRules => Set<CategorizationRule>();

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
            e.HasMany(l => l.Tenants).WithMany(t => t.Leases).UsingEntity("LeaseTenants");
            e.Ignore(l => l.TenantNames);
        });

        modelBuilder.Entity<LeaseDocument>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(255).IsRequired();
            e.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
            e.Property(d => d.StorageKey).HasMaxLength(100).IsRequired();
            e.HasIndex(d => d.StorageKey).IsUnique();
        });

        modelBuilder.Entity<Expense>(e =>
        {
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.PaidWith).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Unit).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.MaintenanceRequest).WithMany(m => m.Expenses).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.Date);
            e.Ignore(x => x.IsShared);
        });

        modelBuilder.Entity<MaintenanceRequest>(e =>
        {
            e.Property(m => m.Title).HasMaxLength(200).IsRequired();
            e.Property(m => m.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(m => m.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.Ignore(m => m.IsOpen);
        });

        modelBuilder.Entity<RentPayment>(e =>
        {
            e.Property(p => p.Method).HasConversion<string>().HasMaxLength(20);
            e.HasOne(p => p.Lease).WithMany(l => l.Payments).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => p.Date);
            e.HasIndex(p => new { p.LeaseId, p.ForMonth });
        });

        modelBuilder.Entity<BankAccount>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(a => a.Name).IsUnique();
        });

        modelBuilder.Entity<ImportedTransaction>(e =>
        {
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.Fingerprint).HasMaxLength(100).IsRequired();
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(t => new { t.BankAccountId, t.Fingerprint }).IsUnique();
            e.HasOne(t => t.Expense).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.RentPayment).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CategorizationRule>(e =>
        {
            e.Property(r => r.MatchText).HasMaxLength(200).IsRequired();
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Category).HasConversion<string>().HasMaxLength(40);
            e.HasOne(r => r.Property).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Unit).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Tenant).WithMany().OnDelete(DeleteBehavior.Cascade);
        });
    }
}
