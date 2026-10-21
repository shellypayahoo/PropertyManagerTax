using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Import;

/// <summary>One bank row on the import review screen, with how it will be recorded.</summary>
public class ImportRow(BankRow row)
{
    public BankRow Row { get; } = row;
    public bool AlreadyImported { get; set; }
    public bool Selected { get; set; }

    public TransactionKind Kind { get; set; } = TransactionKind.Ignored;
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Other;
    public int? VendorId { get; set; }
    public int? LeaseId { get; set; }

    /// <summary>Why the current suggestion was made, shown to the user.</summary>
    public string? Reason { get; set; }

    public string? Problem => Kind switch
    {
        TransactionKind.Expense when PropertyId is null => "Choose a property.",
        TransactionKind.RentPayment when LeaseId is null => "Choose the tenant.",
        _ => null,
    };
}

/// <summary>Everything the classifier needs to know about existing data.</summary>
public record ClassifierContext(
    IReadOnlyList<CategorizationRule> Rules,
    IReadOnlyList<Lease> Leases,
    IReadOnlyList<Vendor> Vendors,
    IReadOnlyDictionary<int, Expense> LastExpenseByVendor,
    IReadOnlyList<Expense> ManualExpenses,
    IReadOnlyList<RentPayment> ManualRentPayments);

/// <summary>Suggests how each imported bank row should be recorded. The user reviews and can change every suggestion.</summary>
public static class TransactionClassifier
{
    public static void Classify(ImportRow item, BankAccount account, ClassifierContext context)
    {
        var row = item.Row;
        item.PropertyId = item.UnitId = item.VendorId = item.LeaseId = null;
        item.Category = ExpenseCategory.Other;

        var rule = context.Rules.Where(r => r.Matches(row.Description)).MaxBy(r => r.MatchText.Length);
        if (rule is not null)
        {
            item.Kind = rule.Kind;
            item.PropertyId = rule.PropertyId;
            item.UnitId = rule.UnitId;
            item.Category = rule.Category ?? ExpenseCategory.Other;
            item.VendorId = rule.VendorId;
            item.LeaseId = rule.TenantId is { } tenantId ? FindLease(context.Leases.Where(l => l.Tenants.Any(t => t.Id == tenantId)), row.Date)?.Id : null;
            item.Reason = $"Rule: \"{rule.MatchText}\"";
        }
        else if (row.Amount > 0 && FindTenantLease(row, context) is ({ } lease, { } tenant))
        {
            item.Kind = TransactionKind.RentPayment;
            item.LeaseId = lease.Id;
            item.Reason = lease.Tenants.Count > 1
                ? $"Sender matches {tenant.FullName}, on the lease with {string.Join(" & ", lease.Tenants.Where(t => t.Id != tenant.Id).Select(t => t.FullName))}"
                : $"Sender matches tenant {tenant.FullName}";
        }
        else if (row.Description.Contains("transfer", StringComparison.OrdinalIgnoreCase))
        {
            item.Kind = TransactionKind.Transfer;
            item.Reason = "Looks like a transfer between your accounts";
        }
        else if (row.Amount < 0 && context.Vendors.FirstOrDefault(v => v.Name.Length >= 3 && row.Description.Contains(v.Name, StringComparison.OrdinalIgnoreCase)) is { } vendor)
        {
            item.Kind = TransactionKind.Expense;
            item.VendorId = vendor.Id;
            if (context.LastExpenseByVendor.TryGetValue(vendor.Id, out var last))
            {
                item.PropertyId = last.PropertyId;
                item.UnitId = last.UnitId;
                item.Category = last.Category;
            }
            item.Reason = $"Vendor {vendor.Name}";
        }
        else if (row.Amount < 0)
        {
            item.Kind = account.IsRentalOnly ? TransactionKind.Expense : TransactionKind.Ignored;
            item.Reason = account.IsRentalOnly ? null : "Personal account; mark as an expense if it was for a rental";
        }
        else
        {
            item.Kind = TransactionKind.Ignored;
            item.Reason = "Unrecognized deposit";
        }

        FlagPossibleDuplicate(item, context);
    }

    /// <summary>Flags rows that match something already entered by hand, such as an expense added from a vendor's text.</summary>
    private static void FlagPossibleDuplicate(ImportRow item, ClassifierContext context)
    {
        var row = item.Row;
        if (item.Kind == TransactionKind.Expense)
        {
            var match = context.ManualExpenses.FirstOrDefault(e =>
                e.Amount == -row.Amount && Math.Abs(e.Date.DayNumber - row.Date.DayNumber) <= 7);
            if (match is not null)
            {
                item.Kind = TransactionKind.Ignored;
                item.Reason = $"Possible duplicate of expense \"{match.Description}\" on {match.Date:d}";
            }
        }
        else if (item.Kind == TransactionKind.RentPayment)
        {
            var match = context.ManualRentPayments.FirstOrDefault(p =>
                p.LeaseId == item.LeaseId && p.Amount == row.Amount && Math.Abs(p.Date.DayNumber - row.Date.DayNumber) <= 5);
            if (match is not null)
            {
                item.Kind = TransactionKind.Ignored;
                item.Reason = $"Possible duplicate of rent payment recorded on {match.Date:d}";
                return;
            }

            // The month was already marked paid by hand, e.g. with "Mark paid" before importing.
            var month = RentPayment.MonthOf(row.Date);
            var lease = context.Leases.FirstOrDefault(l => l.Id == item.LeaseId);
            var paidByHand = context.ManualRentPayments.Where(p => p.LeaseId == item.LeaseId && p.ForMonth == month).Sum(p => p.Amount);
            if (lease is not null && paidByHand > 0m && paidByHand + row.Amount > lease.MonthlyRent)
            {
                item.Kind = TransactionKind.Ignored;
                item.Reason = $"{month:MMMM} rent is already recorded ({paidByHand:C0}); this would pay it twice";
            }
        }
    }

    /// <summary>
    /// Matches a deposit to a tenant when the description contains the tenant's first and last name, as Zelle
    /// descriptions do, and returns that tenant's lease. Any co-tenant's payment counts toward the shared lease.
    /// </summary>
    private static (Lease? Lease, Tenant? Tenant) FindTenantLease(BankRow row, ClassifierContext context)
    {
        var tenant = context.Leases
            .SelectMany(l => l.Tenants)
            .FirstOrDefault(t => IsNamedIn(t, row.Description));
        return tenant is null
            ? (null, null)
            : (FindLease(context.Leases.Where(l => l.Tenants.Any(t => t.Id == tenant.Id)), row.Date), tenant);
    }

    public static bool IsNamedIn(Tenant t, string description) =>
        t.FirstName.Length > 0 && t.LastName.Length > 0
        && description.Contains(t.FirstName, StringComparison.OrdinalIgnoreCase)
        && description.Contains(t.LastName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The lease active on the date, otherwise the most recent one that started before it.</summary>
    public static Lease? FindLease(IEnumerable<Lease> leases, DateOnly date)
    {
        var list = leases.ToList();
        return list.FirstOrDefault(l => l.IsActiveOn(date))
            ?? list.Where(l => l.StartDate <= date).MaxBy(l => l.StartDate)
            ?? list.MinBy(l => l.StartDate);
    }
}
