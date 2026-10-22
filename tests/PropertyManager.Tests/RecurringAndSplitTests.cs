using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class RecurringAndSplitTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly List<Unit> units;

    public RecurringAndSplitTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        units = db.Units.Include(u => u.Property).OrderBy(u => u.Property!.Name).ThenBy(u => u.Label).ToList();
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    public void Dispose() => connection.Dispose();

    private async Task<RecurringExpense> AddHvacAsync(int dayOfMonth = 1)
    {
        await using var db = dbFactory.CreateDbContext();
        var hvac = new RecurringExpense
        {
            Name = "HVAC service", Category = ExpenseCategory.CleaningAndMaintenance, PaidWith = PaymentMethod.Card,
            Amount = 30m, DayOfMonth = dayOfMonth, StartMonth = new(2026, 1, 1),
            Targets = units.Select(u => new RecurringExpenseTarget { PropertyId = u.PropertyId, UnitId = u.Id, StartMonth = new(2026, 1, 1) }).ToList(),
        };
        db.RecurringExpenses.Add(hvac);
        await db.SaveChangesAsync();
        return hvac;
    }

    [Fact]
    public async Task Generates_EachUnitEachMonth_CatchingUpToToday_OnlyOnce()
    {
        await AddHvacAsync();
        var service = new RecurringExpenseService(dbFactory, clock);

        Assert.Equal(12, await service.GenerateDueAsync()); // Jan, Feb, Mar × 4 units
        Assert.Equal(0, await service.GenerateDueAsync());

        await using var db = dbFactory.CreateDbContext();
        var expenses = await db.Expenses.Include(e => e.Unit).ToListAsync();
        Assert.Equal(360m, expenses.Sum(e => e.Amount));
        Assert.All(expenses, e => Assert.Equal((ExpenseCategory.CleaningAndMaintenance, PaymentMethod.Card), (e.Category, e.PaidWith!.Value)));
        Assert.Equal(3, expenses.Count(e => e.UnitId == units[0].Id));
        Assert.Contains(expenses, e => e.Description == "HVAC service – Unit B" && e.Date == new DateOnly(2026, 3, 1));
    }

    [Fact]
    public async Task DayLaterThanToday_WaitsUntilThatDay_AndShortMonthsUseLastDay()
    {
        await AddHvacAsync(dayOfMonth: 31);
        var service = new RecurringExpenseService(dbFactory, clock);

        Assert.Equal(8, await service.GenerateDueAsync()); // Jan 31 and Feb 28; Mar 31 hasn't come yet
        await using var db = dbFactory.CreateDbContext();
        Assert.Contains(await db.Expenses.Select(e => e.Date).Distinct().ToListAsync(), d => d == new DateOnly(2026, 2, 28));
        Assert.DoesNotContain(await db.Expenses.Select(e => e.Date).Distinct().ToListAsync(), d => d.Month == 3);
    }

    [Fact]
    public async Task DeletedOccurrence_IsNotRecreated_AndPausedOrEndedStopsGenerating()
    {
        await AddHvacAsync();
        var service = new RecurringExpenseService(dbFactory, clock);
        await service.GenerateDueAsync();

        await using (var db = dbFactory.CreateDbContext())
        {
            await db.Expenses.Where(e => e.Date == new DateOnly(2026, 1, 1)).ExecuteDeleteAsync();
            var r = await db.RecurringExpenses.SingleAsync();
            r.EndDate = new DateOnly(2026, 4, 30);
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await service.GenerateDueAsync());

        clock.SetUtcNow(new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(4, await service.GenerateDueAsync()); // April only; it ended Apr 30
    }

    [Fact]
    public void NextOccurrence_RespectsDayPauseAndEnd()
    {
        var r = new RecurringExpense { DayOfMonth = 15, StartMonth = new(2026, 1, 1) };

        Assert.Equal(new DateOnly(2026, 3, 15), RecurringExpenseService.NextOccurrence(r, new DateOnly(2026, 3, 10)));
        Assert.Equal(new DateOnly(2026, 4, 15), RecurringExpenseService.NextOccurrence(r, new DateOnly(2026, 3, 15)));
        r.EndDate = new DateOnly(2026, 3, 31);
        Assert.Null(RecurringExpenseService.NextOccurrence(r, new DateOnly(2026, 3, 20)));
        r.EndDate = null;
        r.IsPaused = true;
        Assert.Null(RecurringExpenseService.NextOccurrence(r, new DateOnly(2026, 3, 10)));
    }

    [Fact]
    public async Task SplitEvenlyAcrossThreeUnits_PlacesEachPartOnItsUnit()
    {
        int id;
        await using (var db = dbFactory.CreateDbContext())
        {
            var cleaning = new Expense { PropertyId = units[0].PropertyId, Date = new(2026, 3, 1), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 100m, Description = "Cleaning crew" };
            db.Expenses.Add(cleaning);
            await db.SaveChangesAsync();
            id = cleaning.Id;
        }
        var targets = units.Take(3).ToList();
        var amounts = ExpenseSplitService.DivideEvenly(100m, 3);
        Assert.Equal([33.33m, 33.33m, 33.34m], amounts);

        await new ExpenseSplitService(dbFactory).SplitAsync(id,
            targets.Select((u, i) => new ExpenseSplitPart(ExpenseCategory.CleaningAndMaintenance, amounts[i], new ExpenseLocation(u.PropertyId, u.Id))).ToList());

        await using var check = dbFactory.CreateDbContext();
        var parts = await check.Expenses.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(targets.Select(u => (u.PropertyId, (int?)u.Id)), parts.Select(p => (p.PropertyId!.Value, p.UnitId)));
        Assert.All(parts, p => Assert.Equal("Cleaning crew", p.Description));

        var report = await new ReportService(dbFactory).GetAnnualProfitAndLossAsync(2026);
        var first = report.Single(r => r.PropertyId == targets[0].PropertyId);
        Assert.Equal(66.66m, first.TotalExpenses);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
