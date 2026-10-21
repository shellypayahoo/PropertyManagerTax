using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Import;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public sealed class MortgageImportTests : IDisposable
{
    // Shaped like a typical servicer payment-history export, including a balance column that must not be used.
    private const string History = """
        Transaction Date,Description,Total Amount,Principal,Interest,Escrow,Principal Balance
        03/01/2026,Regular Payment,"$3,200.00",$700.00,"$1,900.00",$600.00,"$412,300.00"
        04/01/2026,Regular Payment,"$3,200.00",$703.10,"$1,896.90",$600.00,"$411,596.90"
        04/20/2026,County Tax Disbursement,"($2,400.00)",$0.00,$0.00,"($2,400.00)",
        05/12/2026,Hazard Insurance Disbursement,"($1,350.00)",$0.00,$0.00,"($1,350.00)",
        """;

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly MortgageImportService service;
    private readonly int taylorId;

    public MortgageImportTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        taylorId = db.Properties.Single(p => p.Name == "1354 Taylor St NW").Id;
        service = new MortgageImportService(dbFactory, TimeProvider.System);
    }

    public void Dispose() => connection.Dispose();

    private BankAccount NewLoan()
    {
        var rows = CsvParser.Parse(History);
        var (headers, _) = BankCsvReader.FindHeader(rows);
        var loan = MortgageCsvReader.DetectColumns(headers);
        loan.Name = "Rocket Mortgage – Taylor";
        loan.PropertyId = taylorId;
        return loan;
    }

    [Fact]
    public void DetectsColumns_IgnoringBalance_AndClassifiesRows()
    {
        var loan = NewLoan();
        Assert.Equal(("Transaction Date", "Total Amount", "Principal", "Interest", "Escrow"),
            (loan.DateColumn, loan.AmountColumn, loan.PrincipalColumn, loan.InterestColumn, loan.EscrowColumn));

        var result = MortgageCsvReader.Read(History, loan);

        Assert.Empty(result.Errors);
        Assert.Equal([MortgageRowKind.Payment, MortgageRowKind.Payment, MortgageRowKind.TaxPayout, MortgageRowKind.InsurancePayout], result.Rows.Select(r => r.Kind));
        Assert.Equal((3200m, 700m, 1900m, 600m), (result.Rows[0].Total, result.Rows[0].Principal, result.Rows[0].Interest, result.Rows[0].Escrow));
        Assert.Equal(2400m, result.Rows[2].Escrow);
    }

    [Fact]
    public async Task SplitsBankPayment_AddsUnmatchedPayment_AndEscrowPayouts()
    {
        int bankExpenseId;
        await using (var db = dbFactory.CreateDbContext())
        {
            // March's payment was imported from the bank as one expense; April's wasn't imported yet.
            var bank = new Expense { PropertyId = taylorId, Date = new(2026, 3, 2), Category = ExpenseCategory.MortgageInterest, Amount = 3200m, Description = "ROCKET MORTGAGE PMT", PaidWith = PaymentMethod.BankTransfer };
            db.Expenses.Add(bank);
            await db.SaveChangesAsync();
            bankExpenseId = bank.Id;
        }

        var loan = NewLoan();
        var rows = await service.PrepareAsync(loan, MortgageCsvReader.Read(History, loan).Rows);

        Assert.Equal([MortgageAction.SplitBankExpense, MortgageAction.AddExpenses, MortgageAction.AddPayout, MortgageAction.AddPayout], rows.Select(r => r.Action));
        Assert.Equal(bankExpenseId, rows[0].Match!.Id);

        Assert.Equal(new MortgageImportSummary(1, 1, 2, 0), await service.CommitAsync(loan, rows));

        var report = (await new ReportService(dbFactory).GetAnnualProfitAndLossAsync(2026)).Single(s => s.PropertyId == taylorId);
        Assert.Equal(
            [new CategoryTotal(ExpenseCategory.Insurance, 1350m), new CategoryTotal(ExpenseCategory.MortgageInterest, 3796.90m), new CategoryTotal(ExpenseCategory.Taxes, 2400m)],
            report.Expenses);
        Assert.Equal(
            [new CategoryTotal(ExpenseCategory.MortgagePrincipal, 1403.10m), new CategoryTotal(ExpenseCategory.EscrowDeposit, 1200m)],
            report.NotDeductible);

        await using var check = dbFactory.CreateDbContext();
        Assert.Equal("ROCKET MORTGAGE PMT", (await check.Expenses.SingleAsync(e => e.Id == bankExpenseId)).Description);
        Assert.Equal(1900m, (await check.Expenses.SingleAsync(e => e.Id == bankExpenseId)).Amount);
    }

    [Fact]
    public async Task ReimportSkipsRows_AndLaterBankImportIgnoresMortgagePayment()
    {
        var loan = NewLoan();
        await service.CommitAsync(loan, await service.PrepareAsync(loan, MortgageCsvReader.Read(History, loan).Rows));

        await using (var db = dbFactory.CreateDbContext())
        {
            loan = await db.BankAccounts.SingleAsync(a => a.IsMortgage);
        }
        var again = await service.PrepareAsync(loan, MortgageCsvReader.Read(History, loan).Rows);
        Assert.All(again, r => Assert.True(r.AlreadyImported));

        // The April payment then shows up in the business bank import.
        var bankImport = new ImportService(dbFactory, TimeProvider.System);
        var account = new BankAccount { Name = "Business", DateColumn = "Date", DescriptionColumn = "Description", AmountColumn = "Amount" };
        var bankRows = BankCsvReader.Read("Date,Description,Amount\n04/02/2026,ROCKET MORTGAGE PMT,-3200.00\n", account).Rows;
        var prepared = await bankImport.PrepareAsync(account, bankRows, await bankImport.LoadContextAsync());

        Assert.Equal(TransactionKind.Ignored, prepared[0].Kind);
        Assert.StartsWith("Already recorded from the Rocket Mortgage – Taylor payment history", prepared[0].Reason);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
