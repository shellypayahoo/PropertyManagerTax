using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class ReportDetailTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly ReportService reports;
    private readonly int taylorId;
    private readonly int firstId;

    public ReportDetailTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        taylorId = db.Properties.Single(p => p.Name == "1354 Taylor St NW").Id;
        firstId = db.Properties.Single(p => p.Name == "2228 1st St NW").Id;
        var taylorB = db.Units.Single(u => u.PropertyId == taylorId && u.Label == "B");
        var vendor = new Vendor { Name = "Sparkle Cleaning" };
        db.Expenses.AddRange(
            new Expense { PropertyId = taylorId, UnitId = taylorB.Id, Vendor = vendor, Date = new(2026, 3, 1), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 120m, Description = "Cleaning" },
            new Expense { PropertyId = taylorId, Date = new(2026, 2, 1), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 30m, Description = "HVAC service" },
            new Expense { PropertyId = firstId, Date = new(2026, 4, 1), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 60m, Description = "Gutter cleaning" },
            new Expense { PropertyId = null, Date = new(2026, 5, 1), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 100.01m, Description = "Supplies for all" },
            new Expense { PropertyId = taylorId, Date = new(2026, 5, 1), Category = ExpenseCategory.Repairs, Amount = 999m, Description = "Other category" },
            new Expense { PropertyId = taylorId, Date = new(2025, 5, 1), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 999m, Description = "Other year" });
        db.SaveChanges();
        reports = new ReportService(dbFactory);
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task PropertyDetail_TotalMatchesReportCell_IncludingSharedShare()
    {
        var report = await reports.GetAnnualProfitAndLossAsync(2026);

        foreach (var propertyId in new[] { taylorId, firstId })
        {
            var detail = await reports.GetDetailAsync(2026, ExpenseCategory.CleaningAndMaintenance, propertyId);
            var cell = report.Single(r => r.PropertyId == propertyId).Expenses.Single(e => e.Category == ExpenseCategory.CleaningAndMaintenance).Amount;
            Assert.Equal(cell, detail.Total);
        }

        var taylor = await reports.GetDetailAsync(2026, ExpenseCategory.CleaningAndMaintenance, taylorId);
        Assert.Equal(["HVAC service", "Cleaning", "Supplies for all"], taylor.Lines.Select(l => l.Description));
        Assert.Equal("1354 Taylor St NW – Unit B", taylor.Lines[1].Where);
        Assert.Equal("Sparkle Cleaning", taylor.Lines[1].Party);
        Assert.True(taylor.Lines[2].Shared);
        Assert.Equal(150m + taylor.SharedShare, taylor.Total);
    }

    [Fact]
    public async Task AllPropertiesDetail_ListsEveryItemAtFullAmount()
    {
        var detail = await reports.GetDetailAsync(2026, ExpenseCategory.CleaningAndMaintenance, null);

        Assert.Equal(4, detail.Lines.Count);
        Assert.Equal(310.01m, detail.Total);
        Assert.Equal("All properties", detail.Lines.Single(l => l.Shared).Where);
    }

    [Fact]
    public async Task RentDetail_ListsPaymentsWithTenantsAndMonth()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            var unit = await db.Units.SingleAsync(u => u.PropertyId == taylorId && u.Label == "A");
            var lease = new Lease { Unit = unit, StartDate = new(2025, 1, 1), MonthlyRent = 4500m, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }] };
            db.RentPayments.Add(new RentPayment { Lease = lease, Date = new(2026, 2, 2), ForMonth = new(2026, 2, 1), Amount = 4500m, Method = PaymentMethod.Zelle });
            await db.SaveChangesAsync();
        }

        var detail = await reports.GetDetailAsync(2026, null, taylorId);

        var line = Assert.Single(detail.Lines);
        Assert.Equal(("1354 Taylor St NW – Unit A", "Jane Sample – rent for February 2026", "Zelle", 4500m), (line.Where, line.Description, line.Party, line.Amount));
        Assert.Empty((await reports.GetDetailAsync(2026, null, firstId)).Lines);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
