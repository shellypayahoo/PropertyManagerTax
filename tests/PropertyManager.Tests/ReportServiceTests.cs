using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class ReportServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;

    public ReportServiceTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public void ScheduledRent_FullYearLease_IsTwelveMonths()
    {
        var lease = new Lease { StartDate = new(2025, 6, 1), MonthlyRent = 4400m };

        var rent = ReportService.ScheduledRent(lease, new(2026, 1, 1), new(2026, 12, 31));

        Assert.Equal(12 * 4400m, rent);
    }

    [Fact]
    public void ScheduledRent_ProratesPartialFirstAndLastMonths()
    {
        // Starts Mar 16 (16 of 31 days), ends Jun 15 (15 of 30 days).
        var lease = new Lease { StartDate = new(2026, 3, 16), EndDate = new(2026, 6, 15), MonthlyRent = 3100m };

        var rent = ReportService.ScheduledRent(lease, new(2026, 1, 1), new(2026, 12, 31));

        Assert.Equal(1600m + 3100m + 3100m + 1550m, rent);
    }

    [Fact]
    public void ScheduledRent_LeaseOutsideRange_IsZero()
    {
        var lease = new Lease { StartDate = new(2024, 1, 1), EndDate = new(2024, 12, 31), MonthlyRent = 1000m };

        Assert.Equal(0m, ReportService.ScheduledRent(lease, new(2026, 1, 1), new(2026, 12, 31)));
    }

    [Fact]
    public async Task AnnualProfitAndLoss_GroupsExpensesByCategoryPerProperty()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db);
            var taylorA = await db.Units.SingleAsync(u => u.Property!.Name == "1354 Taylor St NW" && u.Label == "A");
            var tenant = new Tenant { FirstName = "Test", LastName = "Tenant" };
            db.Leases.Add(new Lease { Unit = taylorA, Tenant = tenant, StartDate = new(2026, 1, 1), MonthlyRent = 4500m });
            db.Expenses.AddRange(
                new Expense { PropertyId = taylorA.PropertyId, Date = new(2026, 2, 1), Category = ExpenseCategory.Repairs, Amount = 250m, Description = "Faucet" },
                new Expense { PropertyId = taylorA.PropertyId, Date = new(2026, 5, 1), Category = ExpenseCategory.Repairs, Amount = 100.50m, Description = "Outlet" },
                new Expense { PropertyId = taylorA.PropertyId, Date = new(2026, 3, 31), Category = ExpenseCategory.Taxes, Amount = 3000m, Description = "Property tax" },
                new Expense { PropertyId = taylorA.PropertyId, Date = new(2025, 12, 31), Category = ExpenseCategory.Repairs, Amount = 999m, Description = "Prior year" });
            await db.SaveChangesAsync();
        }

        var statements = await new ReportService(dbFactory).GetAnnualProfitAndLossAsync(2026);

        var taylor = statements.Single(s => s.PropertyName == "1354 Taylor St NW");
        Assert.Equal(12 * 4500m, taylor.RentIncome);
        Assert.Equal(
            [new CategoryTotal(ExpenseCategory.Repairs, 350.50m), new CategoryTotal(ExpenseCategory.Taxes, 3000m)],
            taylor.Expenses);
        Assert.Equal(54000m - 3350.50m, taylor.NetIncome);

        var first = statements.Single(s => s.PropertyName == "2228 1st St NW");
        Assert.Equal(0m, first.RentIncome);
        Assert.Empty(first.Expenses);
    }

    [Fact]
    public async Task Seeder_CreatesPortfolio_AndIsIdempotent()
    {
        await using var db = dbFactory.CreateDbContext();
        await DbSeeder.SeedAsync(db);
        await DbSeeder.SeedAsync(db);

        var units = await db.Units.Include(u => u.Property).ToListAsync();
        Assert.Equal(2, await db.Properties.CountAsync());
        Assert.Equal(4, units.Count);
        Assert.Equal(14400m, units.Sum(u => u.MarketRent));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
