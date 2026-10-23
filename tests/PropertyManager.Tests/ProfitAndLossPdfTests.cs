using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Reports;
using PropertyManager.Core.Services;
using UglyToad.PdfPig;

namespace PropertyManager.Tests;

public sealed class ProfitAndLossPdfTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;

    public ProfitAndLossPdfTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var unit = db.Units.Include(u => u.Property).Single(u => u.Property!.Name.Contains("Taylor") && u.Label == "B");
        var lease = new Lease { Unit = unit, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
        lease.Payments.Add(new RentPayment { Date = new(2026, 1, 2), ForMonth = new(2026, 1, 1), Amount = 1000m });
        db.Leases.Add(lease);
        db.Expenses.AddRange(
            new Expense { PropertyId = unit.PropertyId, Date = new(2026, 2, 3), Category = ExpenseCategory.Repairs, Amount = 180m, Description = "Kitchen sink repair" },
            new Expense { PropertyId = null, Date = new(2026, 2, 4), Category = ExpenseCategory.LegalAndProfessionalFees, Amount = 400m, Description = "Accounting software" },
            new Expense { PropertyId = unit.PropertyId, Date = new(2026, 2, 5), Category = ExpenseCategory.MortgagePrincipal, Amount = 900m, Description = "Rocket principal" });
        db.SaveChanges();
    }

    public void Dispose() => connection.Dispose();

    private async Task<string> TextOf(bool includeDetail)
    {
        var bytes = await new ProfitAndLossPdf(new ReportService(dbFactory), dbFactory, TimeProvider.System).CreateAsync(2026, includeDetail);
        using var pdf = PdfDocument.Open(bytes);
        return string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public async Task Summary_ShowsScheduleELinesTotalsAndNotes()
    {
        var text = await TextOf(includeDetail: false);

        Assert.Contains("Tax Year 2026", text);
        Assert.Contains("1354 Taylor St NW, Washington, DC · 2 units", text);
        Assert.Contains("Rents received $1,000.00 $0.00 $1,000.00", text);
        // Repairs at Taylor plus half of the shared $400 (two units of four).
        Assert.Contains("Total expenses $380.00 $200.00 $580.00", text);
        Assert.Contains("Net income (loss) $620.00 ($200.00) $420.00", text);
        Assert.Contains("Includes $400.00 of expenses shared by all properties", text);
        Assert.Contains("mortgage principal $900.00 (1354 Taylor St NW $900.00)", text);
        Assert.DoesNotContain("Kitchen sink repair", text);
    }

    [Fact]
    public async Task Detail_ListsEveryItemWithTotals()
    {
        var text = await TextOf(includeDetail: true);

        Assert.Contains("Line 3 – Rents received", text);
        Assert.Contains("01/02/2026", text);
        Assert.Contains("Jane Sample – rent for January 2026", text);
        Assert.Contains("Line 14 – Repairs", text);
        Assert.Contains("Kitchen sink repair", text);
        Assert.Contains("Accounting software", text);
        Assert.Contains("Mortgage principal (not deductible)", text);
        Assert.Contains("Page 1 of", text);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
