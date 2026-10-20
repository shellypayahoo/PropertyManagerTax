using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Import;

namespace PropertyManager.Tests;

public sealed class ImportServiceTests : IDisposable
{
    private const string BusinessCsv = """
        Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #
        CREDIT,02/02/2026,"ONLINE TRANSFER FROM CHK ...1234",13400.00,ACCT_XFER,20000.00,,
        DEBIT,02/10/2026,"PEPCO BILL PAY",-182.33,ACH_DEBIT,19817.67,,
        DEBIT,02/09/2026,"WILBUR LAWN CARE",-180.00,ACH_DEBIT,19637.67,,
        DEBIT,02/15/2026,"HOME DEPOT #2501",-125.40,DEBIT_CARD,19512.27,,
        """;

    private const string ZelleCsv = """
        Date,Description,Amount
        02/01/2026,"Zelle payment from JANE SAMPLE Conf# 1",4500.00
        02/01/2026,"Zelle payment from SOMEONE ELSE Conf# 2",50.00
        02/02/2026,"Online transfer to CHK ...9999",-13400.00
        02/05/2026,"Zelle payment to WILBUR GREEN",-180.00
        """;

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly ImportService service;

    public ImportServiceTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        service = new ImportService(dbFactory, TimeProvider.System);
    }

    public void Dispose() => connection.Dispose();

    private async Task<(Unit TaylorA, Lease Lease)> SetUpTenantAsync()
    {
        await using var db = dbFactory.CreateDbContext();
        var taylorA = await db.Units.Include(u => u.Property).SingleAsync(u => u.Property!.Name == "1354 Taylor St NW" && u.Label == "A");
        var lease = new Lease { Unit = taylorA, Tenant = new Tenant { FirstName = "Jane", LastName = "Sample" }, StartDate = new(2024, 1, 1), MonthlyRent = 4500m };
        db.Leases.Add(lease);
        // Wilbur's lawn bill, entered by hand from his text and paid by personal Zelle.
        db.Expenses.Add(new Expense { PropertyId = taylorA.PropertyId, Date = new(2026, 2, 4), Category = ExpenseCategory.CleaningAndMaintenance, Amount = 180m, Description = "Lawn", PaidWith = PaymentMethod.Zelle });
        db.CategorizationRules.Add(new CategorizationRule { MatchText = "PEPCO", Kind = TransactionKind.Expense, PropertyId = taylorA.PropertyId, Category = ExpenseCategory.Utilities });
        await db.SaveChangesAsync();
        return (taylorA, lease);
    }

    private async Task<List<ImportRow>> PrepareAsync(BankAccount account, string csv)
    {
        var rows = CsvParser.Parse(csv);
        var (headers, start) = BankCsvReader.FindHeader(rows);
        var detected = BankCsvReader.DetectColumns(headers, rows, start);
        account.DateColumn = detected.DateColumn;
        account.DescriptionColumn = detected.DescriptionColumn;
        account.AmountColumn = detected.AmountColumn;
        var parsed = BankCsvReader.Read(csv, account);
        return await service.PrepareAsync(account, parsed.Rows, await service.LoadContextAsync());
    }

    [Fact]
    public async Task ZelleAccount_MatchesTenantRent_AndSkipsTransfersAndPersonalSpending()
    {
        var (_, lease) = await SetUpTenantAsync();
        var account = new BankAccount { Name = "Personal (Zelle)", IsRentalOnly = false };

        var rows = await PrepareAsync(account, ZelleCsv);

        Assert.Equal(TransactionKind.RentPayment, rows[0].Kind);
        Assert.Equal(lease.Id, rows[0].LeaseId);
        Assert.Equal(TransactionKind.Ignored, rows[1].Kind);
        Assert.Equal(TransactionKind.Transfer, rows[2].Kind);
        Assert.Equal(TransactionKind.Ignored, rows[3].Kind);

        var summary = await service.CommitAsync(account, rows);
        Assert.Equal(new ImportSummary(0, 1, 1, 2), summary);

        await using var db = dbFactory.CreateDbContext();
        var payment = await db.RentPayments.SingleAsync();
        Assert.Equal(4500m, payment.Amount);
        Assert.Equal(PaymentMethod.Zelle, payment.Method);
    }

    [Fact]
    public async Task BusinessAccount_AppliesRules_FlagsDuplicates_AndRequiresProperty()
    {
        var (taylorA, _) = await SetUpTenantAsync();
        var account = new BankAccount { Name = "Business checking" };

        var rows = await PrepareAsync(account, BusinessCsv);

        Assert.Equal(TransactionKind.Transfer, rows[0].Kind);
        Assert.Equal((TransactionKind.Expense, ExpenseCategory.Utilities, taylorA.PropertyId), (rows[1].Kind, rows[1].Category, rows[1].PropertyId));
        Assert.Equal(TransactionKind.Ignored, rows[2].Kind);
        Assert.StartsWith("Possible duplicate", rows[2].Reason);
        Assert.Equal(TransactionKind.Expense, rows[3].Kind);
        Assert.Equal("Choose a property.", rows[3].Problem);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(account, rows));

        rows[3].PropertyId = taylorA.PropertyId;
        rows[3].Category = ExpenseCategory.Supplies;
        Assert.Equal(new ImportSummary(2, 0, 1, 1), await service.CommitAsync(account, rows));

        await using var db = dbFactory.CreateDbContext();
        var homeDepot = await db.Expenses.SingleAsync(e => e.Description == "HOME DEPOT #2501");
        Assert.Equal(125.40m, homeDepot.Amount);
        Assert.Equal(ExpenseCategory.Supplies, homeDepot.Category);
    }

    [Fact]
    public async Task ReimportingSameFile_MarksEveryRowAlreadyImported()
    {
        await SetUpTenantAsync();
        var account = new BankAccount { Name = "Personal (Zelle)", IsRentalOnly = false };
        await service.CommitAsync(account, await PrepareAsync(account, ZelleCsv));

        await using (var db = dbFactory.CreateDbContext())
        {
            account = await db.BankAccounts.SingleAsync();
        }
        var again = await PrepareAsync(account, ZelleCsv);

        Assert.All(again, r => Assert.True(r.AlreadyImported));
        Assert.Equal(new ImportSummary(0, 0, 0, 0), await service.CommitAsync(account, again));
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
