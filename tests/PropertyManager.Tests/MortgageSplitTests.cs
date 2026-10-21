using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class MortgageSplitTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly int taylorId;

    public MortgageSplitTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        taylorId = db.Properties.Single(p => p.Name == "1354 Taylor St NW").Id;
    }

    public void Dispose() => connection.Dispose();

    private async Task<int> AddMortgagePaymentAsync()
    {
        await using var db = dbFactory.CreateDbContext();
        var payment = new Expense
        {
            PropertyId = taylorId, Date = new(2026, 3, 1), Category = ExpenseCategory.MortgageInterest,
            Amount = 3200m, Description = "ROCKET MORTGAGE PMT", PaidWith = PaymentMethod.BankTransfer,
        };
        db.Expenses.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    [Fact]
    public async Task Split_CreatesOneExpensePerPart_KeepingDetails()
    {
        var id = await AddMortgagePaymentAsync();

        await new ExpenseSplitService(dbFactory).SplitAsync(id,
        [
            new(ExpenseCategory.MortgageInterest, 1900m),
            new(ExpenseCategory.MortgagePrincipal, 700m),
            new(ExpenseCategory.Taxes, 450m),
            new(ExpenseCategory.Insurance, 150m),
        ]);

        await using var db = dbFactory.CreateDbContext();
        var parts = await db.Expenses.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(
            [(ExpenseCategory.MortgageInterest, 1900m), (ExpenseCategory.MortgagePrincipal, 700m), (ExpenseCategory.Taxes, 450m), (ExpenseCategory.Insurance, 150m)],
            parts.Select(p => (p.Category, p.Amount)));
        Assert.Equal(id, parts[0].Id);
        Assert.All(parts, p => Assert.Equal((taylorId, new DateOnly(2026, 3, 1), "ROCKET MORTGAGE PMT", PaymentMethod.BankTransfer), (p.PropertyId!.Value, p.Date, p.Description, p.PaidWith!.Value)));
    }

    [Fact]
    public async Task Split_RejectsPartsThatDontAddUp()
    {
        var id = await AddMortgagePaymentAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ExpenseSplitService(dbFactory).SplitAsync(id,
            [new(ExpenseCategory.MortgageInterest, 1900m), new(ExpenseCategory.MortgagePrincipal, 700m)]));
        Assert.Contains("add up", ex.Message);
    }

    [Fact]
    public async Task Report_LeavesPrincipalOut_AndReportsItSeparately()
    {
        var id = await AddMortgagePaymentAsync();
        await new ExpenseSplitService(dbFactory).SplitAsync(id,
            [new(ExpenseCategory.MortgageInterest, 1900m), new(ExpenseCategory.MortgagePrincipal, 1300m)]);

        var taylor = (await new ReportService(dbFactory).GetAnnualProfitAndLossAsync(2026)).Single(s => s.PropertyId == taylorId);

        Assert.Equal(new CategoryTotal(ExpenseCategory.MortgageInterest, 1900m), Assert.Single(taylor.Expenses));
        Assert.Equal(1900m, taylor.TotalExpenses);
        Assert.Equal(1300m, taylor.NotDeductible);
    }

    [Fact]
    public void PrincipalHasNoScheduleELine()
    {
        Assert.Null(ExpenseCategory.MortgagePrincipal.ScheduleELine());
        Assert.Equal(12, ExpenseCategory.MortgageInterest.ScheduleELine());
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
