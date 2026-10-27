using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<RecurringExpenseTarget> RecurringExpenseTargets => Set<RecurringExpenseTarget>();
    public DbSet<RecurringExpenseRun> RecurringExpenseRuns => Set<RecurringExpenseRun>();
    public DbSet<TextMessage> TextMessages => Set<TextMessage>();
    public DbSet<MessageContact> MessageContacts => Set<MessageContact>();
    public DbSet<TextAttachment> TextAttachments => Set<TextAttachment>();
    public DbSet<ExpenseReceipt> ExpenseReceipts => Set<ExpenseReceipt>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<AskTurn> AskTurns => Set<AskTurn>();
    public DbSet<EmailFolder> EmailFolders => Set<EmailFolder>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<EmailAttachment> EmailAttachments => Set<EmailAttachment>();
    public DbSet<CalendarSource> CalendarSources => Set<CalendarSource>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

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
            e.HasOne(m => m.CalendarEvent).WithMany().OnDelete(DeleteBehavior.SetNull);
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
            e.HasOne(a => a.Property).WithMany().OnDelete(DeleteBehavior.SetNull);
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

        modelBuilder.Entity<RecurringExpense>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.Category).HasConversion<string>().HasMaxLength(40);
            e.Property(r => r.PaidWith).HasConversion<string>().HasMaxLength(20);
            e.HasOne(r => r.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasMany(r => r.Targets).WithOne(t => t.RecurringExpense).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringExpenseTarget>(e =>
        {
            e.HasOne(t => t.Property).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Unit).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringExpenseRun>(e =>
        {
            e.HasIndex(r => new { r.TargetId, r.Month }).IsUnique();
            e.HasOne(r => r.Target).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Expense).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TextMessage>(e =>
        {
            e.Property(m => m.Guid).HasMaxLength(100).IsRequired();
            e.HasIndex(m => m.Guid).IsUnique();
            e.Property(m => m.ChatIdentifier).HasMaxLength(200);
            e.Property(m => m.SenderHandle).HasMaxLength(200);
            // Stored as a number so SQLite can sort and compare message times.
            e.Property(m => m.SentAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            e.HasIndex(m => m.SentAt);
            e.Property(m => m.ContactHandle).HasMaxLength(200);
            e.HasIndex(m => m.ContactHandle);
            e.HasIndex(m => m.TenantId);
            e.HasIndex(m => m.VendorId);
            e.HasOne(m => m.Tenant).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EmailFolder>(e =>
        {
            e.Property(f => f.GraphId).HasMaxLength(500).IsRequired();
            e.HasIndex(f => f.GraphId).IsUnique();
            e.Property(f => f.Path).HasMaxLength(500).IsRequired();
            e.Property(f => f.LastSyncedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        });

        modelBuilder.Entity<EmailMessage>(e =>
        {
            e.Property(m => m.GraphId).HasMaxLength(500).IsRequired();
            e.HasIndex(m => m.GraphId).IsUnique();
            e.Property(m => m.ConversationId).HasMaxLength(500);
            e.Property(m => m.Subject).HasMaxLength(1000);
            e.Property(m => m.FromAddress).HasMaxLength(320);
            e.HasIndex(m => m.FromAddress);
            e.Property(m => m.FromName).HasMaxLength(200);
            // Stored as a number so SQLite can sort and compare email times.
            e.Property(m => m.SentAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            e.HasIndex(m => m.SentAt);
            e.Ignore(m => m.FromDisplay);
            e.HasOne(m => m.EmailFolder).WithMany(f => f.Messages).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Tenant).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EmailAttachment>(e =>
        {
            e.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            e.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
            e.Property(a => a.StorageKey).HasMaxLength(100).IsRequired();
            e.HasIndex(a => a.StorageKey).IsUnique();
            e.Ignore(a => a.HasText);
            e.HasOne(a => a.EmailMessage).WithMany(m => m.Attachments).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarSource>(e =>
        {
            e.Property(c => c.GraphId).HasMaxLength(500).IsRequired();
            e.HasIndex(c => c.GraphId).IsUnique();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.LastSyncedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        });

        modelBuilder.Entity<CalendarEvent>(e =>
        {
            e.Property(c => c.GraphId).HasMaxLength(500).IsRequired();
            e.HasIndex(c => c.GraphId).IsUnique();
            e.Property(c => c.Subject).HasMaxLength(500);
            e.Property(c => c.Location).HasMaxLength(500);
            e.Property(c => c.Preview).HasMaxLength(1000);
            // Stored as numbers so SQLite can sort and compare event times.
            e.Property(c => c.StartsAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            e.Property(c => c.EndsAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            e.HasIndex(c => c.StartsAt);
            e.HasOne(c => c.CalendarSource).WithMany(s => s.Events).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AskTurn>(e =>
        {
            e.Property(t => t.AskedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            e.Property(t => t.Provider).HasMaxLength(200);
        });

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.Value).HasMaxLength(2000);
        });

        modelBuilder.Entity<ExpenseReceipt>(e =>
        {
            e.Property(r => r.FileName).HasMaxLength(255).IsRequired();
            e.Property(r => r.ContentType).HasMaxLength(100).IsRequired();
            e.Property(r => r.StorageKey).HasMaxLength(100).IsRequired();
            e.HasIndex(r => r.StorageKey).IsUnique();
            e.Property(r => r.PreviewKey).HasMaxLength(100);
            e.Property(r => r.ThumbnailKey).HasMaxLength(100);
            e.Ignore(r => r.IsImage);
            e.HasOne(r => r.Expense).WithMany(x => x.Receipts).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TextAttachment>(e =>
        {
            e.Property(a => a.Guid).HasMaxLength(100).IsRequired();
            e.HasIndex(a => a.Guid).IsUnique();
            e.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            e.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.StorageKey).HasMaxLength(100);
            e.Property(a => a.PreviewKey).HasMaxLength(100);
            e.Property(a => a.ThumbnailKey).HasMaxLength(100);
            e.Ignore(a => a.IsImage);
            e.Ignore(a => a.IsVideo);
            e.HasOne(a => a.TextMessage).WithMany(m => m.Attachments).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageContact>(e =>
        {
            e.Property(c => c.Handle).HasMaxLength(200).IsRequired();
            e.HasIndex(c => c.Handle).IsUnique();
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.LastMessageAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            e.HasOne(c => c.Tenant).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.Vendor).WithMany().OnDelete(DeleteBehavior.SetNull);
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
