using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class VendorServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly VendorService service;
    private readonly int keepId;
    private readonly int duplicateId;

    public VendorServiceTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.First();
        var keep = new Vendor { Name = "Ace Plumbing", Phone = "202-555-0100" };
        var duplicate = new Vendor { Name = "Ace Plumbing LLC", Trade = "Plumber", Phone = "202-555-0199", Email = "ace@example.com", Notes = "Ask for Sam" };
        db.Vendors.AddRange(keep, duplicate);
        db.Expenses.AddRange(
            new Expense { PropertyId = unit.PropertyId, Vendor = keep, Date = new(2026, 1, 5), Category = ExpenseCategory.Repairs, Amount = 100m, Description = "Sink" },
            new Expense { PropertyId = unit.PropertyId, Vendor = duplicate, Date = new(2026, 2, 5), Category = ExpenseCategory.Repairs, Amount = 200m, Description = "Toilet" },
            new Expense { PropertyId = unit.PropertyId, Vendor = duplicate, Date = new(2026, 3, 5), Category = ExpenseCategory.Repairs, Amount = 300m, Description = "Heater" });
        db.MaintenanceRequests.Add(new MaintenanceRequest { Unit = unit, Vendor = duplicate, Title = "Leak", ReportedOn = new(2026, 2, 1) });
        db.RecurringExpenses.Add(new RecurringExpense { Name = "Drain service", Vendor = duplicate, Amount = 50m, StartMonth = new(2026, 1, 1) });
        db.SaveChanges();
        (keepId, duplicateId) = (keep.Id, duplicate.Id);
        service = new VendorService(dbFactory);
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Usage_CountsEverythingThatUsesTheVendor()
    {
        var usage = await service.GetUsageAsync(duplicateId);

        Assert.Equal(new VendorUsage(2, 1, 1, 0, 0, 0), usage);
        Assert.Equal("2 expenses, 1 recurring expense, 1 maintenance request", usage.ToString());
        Assert.False(usage.IsUnused);
    }

    [Fact]
    public async Task Merge_MovesEverything_FillsMissingDetails_AndDeletesTheDuplicate()
    {
        await service.MergeAsync(duplicateId, keepId);

        await using var db = dbFactory.CreateDbContext();
        var keep = await db.Vendors.SingleAsync();
        Assert.Equal((keepId, "202-555-0100", "Plumber", "ace@example.com", "Ask for Sam"), (keep.Id, keep.Phone, keep.Trade, keep.Email, keep.Notes));
        Assert.All(await db.Expenses.ToListAsync(), e => Assert.Equal(keepId, e.VendorId));
        Assert.Equal(keepId, (await db.MaintenanceRequests.SingleAsync()).VendorId);
        Assert.Equal(keepId, (await db.RecurringExpenses.SingleAsync()).VendorId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.MergeAsync(keepId, keepId));
    }

    [Fact]
    public async Task Delete_KeepsItsExpensesWithoutAVendor()
    {
        await service.DeleteAsync(duplicateId);

        await using var db = dbFactory.CreateDbContext();
        Assert.Equal(["Ace Plumbing"], await db.Vendors.Select(v => v.Name).ToListAsync());
        Assert.Equal(3, await db.Expenses.CountAsync());
        Assert.Equal(2, await db.Expenses.CountAsync(e => e.VendorId == null));
        Assert.Null((await db.MaintenanceRequests.SingleAsync()).VendorId);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
