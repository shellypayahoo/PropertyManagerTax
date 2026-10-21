using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Import;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class SharedExpenseTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;

    public SharedExpenseTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public void Allocation_SplitsByUnitCount_AndKeepsPennies()
    {
        var properties = new List<Property>
        {
            new() { Id = 1, Units = [new(), new()] },
            new() { Id = 2, Units = [new(), new(), new()] },
            new() { Id = 3, Units = [new()] },
        };
        var shared = new[] { new Expense { Category = ExpenseCategory.Other, Amount = 100m } };

        var split = ReportService.AllocateSharedExpenses(properties, shared);

        Assert.Equal([33.33m, 50.00m, 16.67m], split.Select(s => s.Amount));
        Assert.Equal(100m, split.Sum(s => s.Amount));
    }

    [Fact]
    public async Task Report_AddsSharedExpensesToEachPropertyByUnits()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            var taylor = await db.Properties.SingleAsync(p => p.Name == "1354 Taylor St NW");
            db.Expenses.AddRange(
                new Expense { PropertyId = null, Date = new(2026, 1, 5), Category = ExpenseCategory.Other, Amount = 120.50m, Description = "Azure" },
                new Expense { PropertyId = null, Date = new(2026, 2, 5), Category = ExpenseCategory.Other, Amount = 120.50m, Description = "Azure" },
                new Expense { PropertyId = taylor.Id, Date = new(2026, 3, 1), Category = ExpenseCategory.Other, Amount = 10m, Description = "Keys" });
            await db.SaveChangesAsync();
        }

        var statements = await new ReportService(dbFactory).GetAnnualProfitAndLossAsync(2026);

        var taylorRow = statements.Single(s => s.PropertyName == "1354 Taylor St NW");
        var firstRow = statements.Single(s => s.PropertyName == "2228 1st St NW");
        // Both properties have 2 units, so the $241 is split evenly.
        Assert.Equal(120.50m, firstRow.SharedExpenses);
        Assert.Equal(120.50m, taylorRow.SharedExpenses);
        Assert.Equal(130.50m, Assert.Single(taylorRow.Expenses).Amount);
        Assert.Equal(241m + 10m, statements.Sum(s => s.TotalExpenses));
    }

    [Fact]
    public async Task Import_SharedRowAndRule_RecordExpenseForAllProperties()
    {
        var service = new ImportService(dbFactory, TimeProvider.System);
        await using (var db = dbFactory.CreateDbContext())
        {
            db.CategorizationRules.Add(new CategorizationRule { MatchText = "MICROSOFT AZURE", Kind = TransactionKind.Expense, PropertyId = null, Category = ExpenseCategory.Other });
            await db.SaveChangesAsync();
        }
        var account = new BankAccount { Name = "Business", DateColumn = "Date", DescriptionColumn = "Description", AmountColumn = "Amount" };
        var parsed = BankCsvReader.Read("""
            Date,Description,Amount
            03/02/2026,MICROSOFT AZURE MSBILL.INFO,-212.17
            03/05/2026,GITHUB INC,-4.00
            """, account);

        var rows = await service.PrepareAsync(account, parsed.Rows, await service.LoadContextAsync());
        Assert.True(rows[0].Shared);
        Assert.Null(rows[0].Problem);
        Assert.Equal("Choose a property.", rows[1].Problem);

        rows[1].Shared = true;
        await service.CommitAsync(account, rows);

        await using var check = dbFactory.CreateDbContext();
        Assert.All(await check.Expenses.ToListAsync(), e => Assert.True(e.IsShared));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
