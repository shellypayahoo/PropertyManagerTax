using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Reports;
using PropertyManager.Core.Services;
using UglyToad.PdfPig;

namespace PropertyManager.Tests;

public sealed class ReportPeriodTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly int taylorId;

    public ReportPeriodTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        taylorId = unit.PropertyId;
        var lease = new Lease { Unit = unit, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
        foreach (var m in new[] { 1, 4, 7, 10 })
        {
            lease.Payments.Add(new RentPayment { Date = new(2026, m, 2), ForMonth = new(2026, m, 1), Amount = 1000m });
        }
        db.Leases.Add(lease);
        db.Expenses.AddRange(
            new Expense { PropertyId = taylorId, Date = new(2026, 2, 10), Category = ExpenseCategory.Repairs, Amount = 100m, Description = "Q1 repair" },
            new Expense { PropertyId = taylorId, Date = new(2026, 9, 30), Category = ExpenseCategory.Repairs, Amount = 300m, Description = "Q3 repair" },
            new Expense { PropertyId = taylorId, Date = new(2026, 10, 1), Category = ExpenseCategory.Repairs, Amount = 900m, Description = "Q4 repair" });
        db.SaveChanges();
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public void Periods_CoverTheRightDates()
    {
        Assert.Equal(new ReportPeriod(new(2026, 4, 1), new(2026, 6, 30)), ReportPeriod.Quarter(2026, 2));
        Assert.Equal(new ReportPeriod(new(2026, 1, 1), new(2026, 9, 30)), ReportPeriod.LastCompletedQuarters(new(2026, 10, 26), 3));
        Assert.Equal(new ReportPeriod(new(2025, 4, 1), new(2025, 12, 31)), ReportPeriod.LastCompletedQuarters(new(2026, 2, 10), 3));
        Assert.Equal("2026", ReportPeriod.Year(2026).Label);
        Assert.Equal("Jan 1 – Sep 30, 2026", ReportPeriod.LastCompletedQuarters(new(2026, 10, 26), 3).Label);
        Assert.Equal("Apr 1 – Dec 31, 2025", new ReportPeriod(new(2025, 4, 1), new(2025, 12, 31)).Label);
        Assert.Equal("Oct 1, 2025 – Jun 30, 2026", new ReportPeriod(new(2025, 10, 1), new(2026, 6, 30)).Label);
    }

    [Fact]
    public async Task ProfitAndLoss_ForThreeQuarters_IncludesOnlyThatPeriod()
    {
        var reports = new ReportService(dbFactory);

        var taylor = (await reports.GetProfitAndLossAsync(new(2026, 1, 1), new(2026, 9, 30))).Single(s => s.PropertyId == taylorId);
        var detail = await reports.GetDetailAsync(new(2026, 1, 1), new(2026, 9, 30), ExpenseCategory.Repairs, taylorId);

        Assert.Equal((3000m, 9000m, 400m, 2600m), (taylor.RentReceived, taylor.ScheduledRent, taylor.TotalExpenses, taylor.NetIncome));
        Assert.Equal(["Q1 repair", "Q3 repair"], detail.Lines.Select(l => l.Description));
        Assert.Equal(1300m, (await reports.GetAnnualProfitAndLossAsync(2026)).Single(s => s.PropertyId == taylorId).TotalExpenses);
    }

    [Fact]
    public async Task Pdf_ForAPeriod_SaysWhichDates()
    {
        var period = ReportPeriod.LastCompletedQuarters(new(2026, 10, 26), 3);
        var bytes = await new ProfitAndLossPdf(new ReportService(dbFactory), dbFactory, TimeProvider.System).CreateAsync(period, includeDetail: true);

        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
        Assert.Contains("Rental Income and Expenses – Jan 1 – Sep 30, 2026", text);
        Assert.Contains("January 1, 2026 through September 30, 2026", text);
        Assert.Contains("Q3 repair", text);
        Assert.DoesNotContain("Q4 repair", text);
        Assert.Equal("Rental P&L 2026-01-01 to 2026-09-30.pdf", ProfitAndLossPdf.FileName(period));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
