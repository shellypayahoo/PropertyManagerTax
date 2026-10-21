using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Core.Import;

public enum MortgageAction
{
    /// <summary>Split the matching mortgage payment already imported from the bank into interest, principal, and escrow.</summary>
    SplitBankExpense,

    /// <summary>No matching bank expense: add interest, principal, and escrow as new expenses.</summary>
    AddExpenses,

    /// <summary>Add an escrow payout as a Taxes or Insurance expense.</summary>
    AddPayout,

    Ignore,
}

/// <summary>One lender row on the mortgage import review screen.</summary>
public class MortgageImportRow(MortgageRow row)
{
    public MortgageRow Row { get; } = row;
    public bool AlreadyImported { get; set; }
    public MortgageAction Action { get; set; }

    /// <summary>The bank-imported expense this payment will split, when <see cref="Action"/> is <see cref="MortgageAction.SplitBankExpense"/>.</summary>
    public Expense? Match { get; set; }

    public IReadOnlyList<ExpenseSplitPart> PaymentParts =>
        new[]
        {
            new ExpenseSplitPart(ExpenseCategory.MortgageInterest, Row.Interest),
            new ExpenseSplitPart(ExpenseCategory.MortgagePrincipal, Row.Principal),
            new ExpenseSplitPart(ExpenseCategory.EscrowDeposit, Row.Escrow),
        }
        .Where(p => p.Amount != 0m)
        .ToList();

    /// <summary>Any part of the total not explained by principal, interest, and escrow (e.g. a late fee).</summary>
    public decimal Unexplained => Row.Total - Row.Principal - Row.Interest - Row.Escrow;
}

public record MortgageImportSummary(int PaymentsSplit, int PaymentsAdded, int Payouts, int Ignored);

/// <summary>
/// Imports a lender's payment history. Each payment is matched to the bank expense for the same amount (within
/// <see cref="MatchWindowDays"/> days) and that expense is split, so the payment is never counted twice.
/// </summary>
public class MortgageImportService(IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock)
{
    public const int MatchWindowDays = 10;

    public async Task<List<MortgageImportRow>> PrepareAsync(BankAccount loan, IReadOnlyList<MortgageRow> rows, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = loan.Id == 0
            ? []
            : (await db.ImportedTransactions.Where(t => t.BankAccountId == loan.Id).Select(t => t.Fingerprint).ToListAsync(ct)).ToHashSet();

        // Candidate bank payments: expenses on this property that haven't been split into mortgage parts yet.
        var candidates = await db.Expenses.AsNoTracking()
            .Where(e => e.PropertyId == loan.PropertyId
                && e.Category != ExpenseCategory.MortgagePrincipal
                && e.Category != ExpenseCategory.EscrowDeposit)
            .ToListAsync(ct);
        var claimed = new HashSet<int>();

        var items = new List<MortgageImportRow>();
        foreach (var row in rows.OrderBy(r => r.Date))
        {
            var item = new MortgageImportRow(row) { AlreadyImported = existing.Contains(row.Fingerprint) };
            if (!item.AlreadyImported)
            {
                item.Action = row.Kind switch
                {
                    MortgageRowKind.Payment => MortgageAction.AddExpenses,
                    MortgageRowKind.TaxPayout or MortgageRowKind.InsurancePayout => MortgageAction.AddPayout,
                    _ => MortgageAction.Ignore,
                };
                if (row.Kind == MortgageRowKind.Payment)
                {
                    item.Match = candidates
                        .Where(e => !claimed.Contains(e.Id) && e.Amount == row.Total && Math.Abs(e.Date.DayNumber - row.Date.DayNumber) <= MatchWindowDays)
                        .MinBy(e => Math.Abs(e.Date.DayNumber - row.Date.DayNumber));
                    if (item.Match is not null)
                    {
                        claimed.Add(item.Match.Id);
                        item.Action = MortgageAction.SplitBankExpense;
                    }
                }
            }
            items.Add(item);
        }
        return items;
    }

    public async Task<MortgageImportSummary> CommitAsync(BankAccount loan, IEnumerable<MortgageImportRow> rows, CancellationToken ct = default)
    {
        if (loan.PropertyId is null)
        {
            throw new InvalidOperationException("Choose the property this loan is on.");
        }

        var toImport = rows.Where(r => !r.AlreadyImported).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        loan.IsMortgage = true;
        var account = loan.Id == 0 ? db.BankAccounts.Add(loan).Entity : await db.BankAccounts.SingleAsync(a => a.Id == loan.Id, ct);
        if (loan.Id != 0)
        {
            db.Entry(account).CurrentValues.SetValues(loan);
        }

        int split = 0, added = 0, payouts = 0, ignored = 0;
        var now = clock.GetUtcNow();
        foreach (var item in toImport)
        {
            var row = item.Row;
            var description = string.IsNullOrWhiteSpace(row.Description) ? $"{loan.Name} payment" : $"{loan.Name}: {row.Description}";
            Expense? first = null;

            switch (item.Action)
            {
                case MortgageAction.SplitBankExpense when item.Match is not null:
                    var original = await db.Expenses.SingleOrDefaultAsync(e => e.Id == item.Match.Id, ct)
                        ?? throw new InvalidOperationException($"The bank expense for {row.Date:d} no longer exists. Re-open the file and try again.");
                    var parts = item.PaymentParts.ToList();
                    if (item.Unexplained != 0m)
                    {
                        parts.Add(new ExpenseSplitPart(ExpenseCategory.Other, item.Unexplained));
                    }
                    first = ExpenseSplitService.Split(db, original, parts)[0];
                    split++;
                    break;

                case MortgageAction.AddExpenses:
                    foreach (var part in item.PaymentParts)
                    {
                        var expense = NewExpense(loan, row.Date, description, part.Category, part.Amount);
                        db.Expenses.Add(expense);
                        first ??= expense;
                    }
                    added++;
                    break;

                case MortgageAction.AddPayout:
                    var category = row.Kind == MortgageRowKind.InsurancePayout ? ExpenseCategory.Insurance : ExpenseCategory.Taxes;
                    first = NewExpense(loan, row.Date, $"{description} (paid from escrow)", category, row.Escrow != 0m ? row.Escrow : row.Total);
                    db.Expenses.Add(first);
                    payouts++;
                    break;

                default:
                    ignored++;
                    break;
            }

            db.ImportedTransactions.Add(new ImportedTransaction
            {
                BankAccount = account,
                Date = row.Date,
                Description = description,
                // Stored as money out so bank imports can recognize the same payment.
                Amount = -row.Total,
                Fingerprint = row.Fingerprint,
                Kind = first is null ? TransactionKind.Ignored : TransactionKind.Expense,
                Expense = first,
                ImportedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MortgageImportSummary(split, added, payouts, ignored);
    }

    private static Expense NewExpense(BankAccount loan, DateOnly date, string description, ExpenseCategory category, decimal amount) => new()
    {
        PropertyId = loan.PropertyId,
        Date = date,
        Description = description,
        Category = category,
        Amount = amount,
        PaidWith = PaymentMethod.BankTransfer,
        Notes = $"Imported from {loan.Name} payment history",
    };
}
