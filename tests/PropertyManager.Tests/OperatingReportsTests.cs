using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Reports;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class OperatingReportsTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly OperatingReports reports;
    private readonly int taylorId;

    public OperatingReportsTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        taylorId = unit.PropertyId;

        // Basement lease from January: Jan on time, Feb 9 days late, Mar partly paid, Apr nothing, May not due yet.
        var lease = new Lease { Unit = unit, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
        lease.Payments.AddRange(
        [
            new RentPayment { Date = new(2026, 1, 2), ForMonth = new(2026, 1, 1), Amount = 1000m },
            new RentPayment { Date = new(2026, 2, 3), ForMonth = new(2026, 2, 1), Amount = 400m },
            new RentPayment { Date = new(2026, 2, 10), ForMonth = new(2026, 2, 1), Amount = 600m },
            new RentPayment { Date = new(2026, 3, 1), ForMonth = new(2026, 3, 1), Amount = 500m },
        ]);
        db.Leases.Add(lease);

        var plumber = new Vendor { Name = "Ace Plumbing", Trade = "Plumber" };
        var lender = new Vendor { Name = "Rocket Mortgage" };
        db.Expenses.AddRange(
            new Expense { PropertyId = taylorId, Vendor = plumber, Date = new(2026, 1, 15), Category = ExpenseCategory.Repairs, Amount = 180m, Description = "Sink" },
            new Expense { PropertyId = taylorId, Vendor = plumber, Date = new(2026, 3, 2), Category = ExpenseCategory.Repairs, Amount = 220m, Description = "Toilet" },
            new Expense { PropertyId = taylorId, Vendor = lender, Date = new(2026, 1, 1), Category = ExpenseCategory.MortgageInterest, Amount = 700m, Description = "Interest" },
            new Expense { PropertyId = taylorId, Vendor = lender, Date = new(2026, 1, 1), Category = ExpenseCategory.MortgagePrincipal, Amount = 300m, Description = "Principal" },
            new Expense { PropertyId = null, Date = new(2026, 1, 20), Category = ExpenseCategory.LegalAndProfessionalFees, Amount = 100m, Description = "Software" });
        db.SaveChanges();

        reports = new OperatingReports(dbFactory, new FixedTime(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task CashFlow_CountsPrincipalAsMoneyOut_AndKeepsARunningTotal()
    {
        var all = await reports.GetCashFlowAsync(2026);

        Assert.Equal(12, all.Count);
        var jan = all[0];
        Assert.Equal((1000m, 980m, 300m, 1280m, -280m), (jan.RentIn, jan.Expenses, jan.LoanAndEscrow, jan.TotalOut, jan.Net));
        Assert.Equal(1000m, all[1].Net);
        Assert.Equal(-280m + 1000m + 280m, all[2].Cumulative);
        Assert.Equal(all[2].Cumulative, all[11].Cumulative);
    }

    [Fact]
    public async Task CashFlow_ForOneProperty_IncludesItsShareOfSharedExpenses()
    {
        var taylor = await reports.GetCashFlowAsync(2026, taylorId);
        var firstStId = (await dbFactory.CreateDbContext().Properties.SingleAsync(p => p.Id != taylorId)).Id;
        var firstSt = await reports.GetCashFlowAsync(2026, firstStId);

        // Each property has two of the four units, so each carries $50 of the $100 software bill.
        Assert.Equal(180m + 700m + 50m, taylor[0].Expenses);
        Assert.Equal(50m, firstSt[0].Expenses);
        Assert.Equal(0m, firstSt[0].RentIn);
    }

    [Fact]
    public async Task RentCollection_FlagsLatePartialAndMissedMonths()
    {
        var row = Assert.Single(await reports.GetRentCollectionAsync(2026, graceDays: 5));

        Assert.Equal(12, row.Months.Count);
        var (jan, feb, mar, apr, may) = (row.Months[0], row.Months[1], row.Months[2], row.Months[3], row.Months[4]);
        Assert.Equal((RentStatus.Paid, 0), (jan.Status, jan.DaysLate));
        Assert.Equal((RentStatus.Paid, 9), (feb.Status, feb.DaysLate));
        Assert.Equal(RentStatus.Partial, mar.Status);
        Assert.True(mar.IsLate);
        Assert.Equal((RentStatus.Unpaid, 19), (apr.Status, apr.DaysLate));
        Assert.Equal((RentStatus.Upcoming, 0), (may.Status, may.DaysLate));
        Assert.Equal((4000m, 2500m, 1500m), (row.Due, row.Paid, row.Balance));
        Assert.Equal((3, 1, 1), (row.LateMonths, row.MissedMonths, row.PartialMonths));
    }

    [Fact]
    public async Task RentCollection_WithinGracePeriod_IsNotLate()
    {
        var early = new OperatingReports(dbFactory, new FixedTime(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero)));

        var april = (await early.GetRentCollectionAsync(2026, graceDays: 5)).Single().Months[3];

        Assert.Equal((RentStatus.Unpaid, false), (april.Status, april.IsLate));
        Assert.Equal(0, (await early.GetRentCollectionAsync(2026, graceDays: 10)).Single().Months[1].DaysLate);
    }

    [Fact]
    public async Task VendorSpending_IsLargestFirst_WithUnassignedLast()
    {
        var vendors = await reports.GetVendorSpendingAsync(new(2026, 1, 1), new(2026, 12, 31));

        Assert.Equal(["Rocket Mortgage", "Ace Plumbing", "No vendor recorded"], vendors.Select(v => v.Name));
        Assert.Equal([1000m, 400m, 100m], vendors.Select(v => v.Total));
        Assert.Equal(["Mortgage interest", "Mortgage principal (not deductible)"], vendors[0].Categories);
        Assert.Equal(["All properties"], vendors[2].Properties);
        Assert.Equal(["Sink", "Toilet"], vendors[1].Items.Select(e => e.Description));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
