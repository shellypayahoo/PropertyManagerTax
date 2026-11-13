using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Reports;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class DepreciationTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly int taylorId;

    public DepreciationTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var taylor = db.Properties.Single(p => p.Name.Contains("Taylor"));
        // $600,000 with $10,000 closing costs and $170,000 of land: a $440,000 building, in service July 2020.
        (taylor.PurchaseDate, taylor.PurchasePrice, taylor.LandValue, taylor.BasisAdditions) = (new(2020, 7, 15), 600_000m, 170_000m, 10_000m);
        taylor.Assets.Add(new DepreciableAsset { Description = "Refrigerator", PlacedInService = new(2025, 3, 1), Cost = 2_000m, Life = AssetLife.FiveYear });
        db.SaveChanges();
        taylorId = taylor.Id;
    }

    public void Dispose() => connection.Dispose();

    // Percentages from IRS Publication 946, Table A-6 (27.5-year, mid-month), for $100,000.
    [Theory]
    [InlineData(1, 2024, 3484.85)]   // January: 3.485%
    [InlineData(7, 2024, 1666.67)]   // July: 1.667%
    [InlineData(12, 2024, 151.52)]   // December: 0.152%
    [InlineData(7, 2025, 3636.36)]   // every full year: 3.636%
    public void Building_follows_the_27_5_year_mid_month_table(int month, int year, decimal expected) =>
        Assert.Equal(expected, Depreciation.ForYear(100_000m, new DateOnly(2024, month, 10), AssetLife.ResidentialBuilding, year));

    [Fact]
    public void Building_is_fully_deducted_over_its_life_and_no_further()
    {
        var placed = new DateOnly(2024, 7, 10);
        var total = Enumerable.Range(2024, 40).Sum(y => Depreciation.ForYear(100_000m, placed, AssetLife.ResidentialBuilding, y));

        Assert.InRange(total, 99_999.90m, 100_000.10m);
        // Table A-6, July column: year 29 is 0.152% (the last half month).
        Assert.Equal(151.52m, Depreciation.ForYear(100_000m, placed, AssetLife.ResidentialBuilding, 2052));
        Assert.Equal(0m, Depreciation.ForYear(100_000m, placed, AssetLife.ResidentialBuilding, 2053));
        Assert.Equal(0m, Depreciation.ForYear(100_000m, placed, AssetLife.ResidentialBuilding, 2023));
    }

    [Fact]
    public void Equipment_follows_the_half_year_tables()
    {
        var placed = new DateOnly(2025, 3, 1);
        Assert.Equal([400m, 640m, 384m, 230.40m, 230.40m, 115.20m, 0m],
            Enumerable.Range(2025, 7).Select(y => Depreciation.ForYear(2_000m, placed, AssetLife.FiveYear, y)));
        Assert.Equal(500m, Depreciation.ForYear(10_000m, placed, AssetLife.FifteenYear, 2025));
        Assert.Equal(10_000m, Enumerable.Range(2025, 16).Sum(y => Depreciation.ForYear(10_000m, placed, AssetLife.FifteenYear, y)));
    }

    [Fact]
    public async Task Schedule_lists_the_building_and_improvements()
    {
        await using var db = dbFactory.CreateDbContext();
        var taylor = await db.Properties.Include(p => p.Assets).SingleAsync(p => p.Id == taylorId);

        var lines = Depreciation.Schedule(taylor, 2026);

        var building = lines[0];
        Assert.Equal(("Building", 440_000m, 16_000m), (building.Item, building.Basis, building.ThisYear));
        // 2020: 5.5 months; 2021-2025: five full years.
        Assert.Equal(7_333.33m + 5 * 16_000m, building.PriorYears);
        Assert.Equal(("Refrigerator", 400m, 640m), (lines[1].Item, lines[1].PriorYears, lines[1].ThisYear));

        // No land value: the building isn't depreciated, improvements still are.
        taylor.LandValue = null;
        Assert.Equal(["Refrigerator"], Depreciation.Schedule(taylor, 2026).Select(l => l.Item));
    }

    [Fact]
    public async Task Profit_and_loss_includes_calculated_depreciation()
    {
        var reports = new ReportService(dbFactory);

        var year = (await reports.GetAnnualProfitAndLossAsync(2026)).Single(p => p.PropertyId == taylorId);
        Assert.Equal(16_640m, year.CalculatedDepreciation);
        Assert.Equal(16_640m, year.Expenses.Single(e => e.Category == ExpenseCategory.Depreciation).Amount);

        // A quarter gets about a quarter of the year.
        var quarter = (await reports.GetProfitAndLossAsync(new(2026, 1, 1), new(2026, 3, 31))).Single(p => p.PropertyId == taylorId);
        Assert.InRange(quarter.CalculatedDepreciation, 4_000m, 4_200m);

        var detail = await reports.GetDetailAsync(2026, ExpenseCategory.Depreciation, taylorId);
        Assert.Equal(16_640m, detail.Total);
        Assert.Equal(2, detail.Lines.Count);
        Assert.StartsWith("Building (calculated", detail.Lines[0].Description);

        // The other property has no land value, so nothing is calculated for it.
        Assert.Equal(0m, (await reports.GetAnnualProfitAndLossAsync(2026)).Single(p => p.PropertyId != taylorId).CalculatedDepreciation);
    }

    [Fact]
    public async Task Depreciation_entered_by_hand_replaces_the_calculation()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            db.Expenses.Add(new Expense { PropertyId = taylorId, Date = new(2026, 12, 31), Amount = 15_000m, Category = ExpenseCategory.Depreciation, Description = "Per accountant" });
            await db.SaveChangesAsync();
        }

        var year = (await new ReportService(dbFactory).GetAnnualProfitAndLossAsync(2026)).Single(p => p.PropertyId == taylorId);

        Assert.Equal(0m, year.CalculatedDepreciation);
        Assert.Equal(15_000m, year.Expenses.Single(e => e.Category == ExpenseCategory.Depreciation).Amount);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
