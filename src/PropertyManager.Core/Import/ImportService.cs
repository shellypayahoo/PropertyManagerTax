using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Import;

public record ImportSummary(int Expenses, int RentPayments, int Transfers, int Ignored);

public class ImportService(IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock)
{
    public async Task<ClassifierContext> LoadContextAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rules = await db.CategorizationRules.AsNoTracking().ToListAsync(ct);
        var leases = await db.Leases.AsNoTracking().Include(l => l.Tenants).Include(l => l.Unit).ToListAsync(ct);
        var vendors = await db.Vendors.AsNoTracking().ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().ToListAsync(ct);
        var payments = await db.RentPayments.AsNoTracking().ToListAsync(ct);

        var importedExpenseIds = (await db.ImportedTransactions.Where(t => t.ExpenseId != null).Select(t => t.ExpenseId!.Value).ToListAsync(ct)).ToHashSet();
        var importedPaymentIds = (await db.ImportedTransactions.Where(t => t.RentPaymentId != null).Select(t => t.RentPaymentId!.Value).ToListAsync(ct)).ToHashSet();

        var lastByVendor = expenses
            .Where(e => e.VendorId is not null)
            .GroupBy(e => e.VendorId!.Value)
            .ToDictionary(g => g.Key, g => g.MaxBy(e => e.Date)!);

        return new ClassifierContext(
            rules,
            leases,
            vendors,
            lastByVendor,
            expenses.Where(e => !importedExpenseIds.Contains(e.Id)).ToList(),
            payments.Where(p => !importedPaymentIds.Contains(p.Id)).ToList());
    }

    /// <summary>Builds review rows for parsed bank rows, marking rows already imported into this account.</summary>
    public async Task<List<ImportRow>> PrepareAsync(BankAccount account, IReadOnlyList<BankRow> rows, ClassifierContext context, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = account.Id == 0
            ? []
            : (await db.ImportedTransactions.Where(t => t.BankAccountId == account.Id).Select(t => t.Fingerprint).ToListAsync(ct)).ToHashSet();

        var items = rows.Select(r => new ImportRow(r) { AlreadyImported = existing.Contains(r.Fingerprint) }).ToList();
        foreach (var item in items.Where(i => !i.AlreadyImported))
        {
            TransactionClassifier.Classify(item, account, context);
        }
        return items;
    }

    public async Task<ImportSummary> CommitAsync(BankAccount account, IEnumerable<ImportRow> rows, CancellationToken ct = default)
    {
        var toImport = rows.Where(r => !r.AlreadyImported).ToList();
        if (toImport.FirstOrDefault(r => r.Problem is not null) is { } invalid)
        {
            throw new InvalidOperationException($"{invalid.Row.Date:d} {invalid.Row.Description}: {invalid.Problem}");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var entity = account.Id == 0 ? db.BankAccounts.Add(account).Entity : await db.BankAccounts.SingleAsync(a => a.Id == account.Id, ct);
        if (account.Id != 0)
        {
            // Save any column-layout changes made during this import.
            db.Entry(entity).CurrentValues.SetValues(account);
        }

        var now = clock.GetUtcNow();
        foreach (var item in toImport)
        {
            var imported = new ImportedTransaction
            {
                BankAccount = entity,
                Date = item.Row.Date,
                Description = item.Row.Description,
                Amount = item.Row.Amount,
                Fingerprint = item.Row.Fingerprint,
                Kind = item.Kind,
                ImportedAt = now,
            };

            if (item.Kind == TransactionKind.Expense)
            {
                imported.Expense = new Expense
                {
                    PropertyId = item.Shared ? null : item.PropertyId,
                    UnitId = item.Shared ? null : item.UnitId,
                    VendorId = item.VendorId,
                    Category = item.Category,
                    Date = item.Row.Date,
                    // Money out is negative in the bank file; a refund (money in) becomes a negative expense.
                    Amount = -item.Row.Amount,
                    Description = item.Row.Description,
                    PaidWith = PaymentMethod.BankTransfer,
                    Notes = $"Imported from {account.Name}",
                };
            }
            else if (item.Kind == TransactionKind.RentPayment)
            {
                imported.RentPayment = new RentPayment
                {
                    LeaseId = item.LeaseId!.Value,
                    Date = item.Row.Date,
                    ForMonth = RentPayment.MonthOf(item.Row.Date),
                    Amount = item.Row.Amount,
                    Method = item.Row.Description.Contains("zelle", StringComparison.OrdinalIgnoreCase) ? PaymentMethod.Zelle : PaymentMethod.BankTransfer,
                    Notes = item.Row.Description,
                };
            }

            db.ImportedTransactions.Add(imported);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ImportSummary(
            toImport.Count(r => r.Kind == TransactionKind.Expense),
            toImport.Count(r => r.Kind == TransactionKind.RentPayment),
            toImport.Count(r => r.Kind == TransactionKind.Transfer),
            toImport.Count(r => r.Kind == TransactionKind.Ignored));
    }

    public async Task<CategorizationRule> AddRuleAsync(CategorizationRule rule, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.CategorizationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }
}
