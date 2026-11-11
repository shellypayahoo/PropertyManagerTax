using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Reports;

/// <summary>What a vendor was paid in a year, for deciding who gets a Form 1099-NEC.</summary>
/// <param name="Reportable">Payments that count toward the 1099: for services, and not by card.</param>
/// <param name="PaidByCard">Card payments, which the card company reports instead.</param>
/// <param name="NotForServices">Interest, insurance, taxes, utilities, and the like, which don't go on a 1099-NEC.</param>
public record Vendor1099(
    Vendor Vendor, decimal Reportable, decimal PaidByCard, decimal NotForServices, int Payments, bool W9InDocuments, bool Needs1099)
{
    public bool HasW9 => Vendor.HasW9 || W9InDocuments;
}

/// <summary>
/// Lists the year's vendors with what counts toward a 1099-NEC. A bookkeeping aid: the owner marks vendors that are
/// exempt (corporations, stores, utilities) and confirms the list with their tax preparer.
/// </summary>
public class Form1099Report(IDbContextFactory<PropertyManagerDbContext> dbFactory)
{
    /// <summary>The federal threshold: $600 through 2025, $2,000 for payments made in 2026 and later.</summary>
    public static decimal DefaultThreshold(int year) => year >= 2026 ? 2000m : 600m;

    /// <summary>Whether payments in a category are for services (or parts and materials supplied with them).</summary>
    public static bool CountsToward1099(ExpenseCategory category) => category is not (
        ExpenseCategory.AutoAndTravel or ExpenseCategory.Insurance or ExpenseCategory.MortgageInterest or ExpenseCategory.OtherInterest
        or ExpenseCategory.Taxes or ExpenseCategory.Utilities or ExpenseCategory.Depreciation
        or ExpenseCategory.MortgagePrincipal or ExpenseCategory.EscrowDeposit);

    /// <summary>Every vendor paid in the year, those needing a 1099 first, then by amount.</summary>
    public async Task<List<Vendor1099>> GetAsync(int year, decimal? threshold = null, CancellationToken ct = default)
    {
        var limit = threshold ?? DefaultThreshold(year);
        var (from, to) = (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().Include(e => e.Vendor)
            .Where(e => e.VendorId != null && e.Date >= from && e.Date <= to)
            .ToListAsync(ct);
        var taxForms = await db.BusinessDocuments.AsNoTracking()
            .Where(d => d.Kind == BusinessDocumentKind.TaxForm)
            .Select(d => d.Description + " " + d.FileName)
            .ToListAsync(ct);

        return expenses.GroupBy(e => e.VendorId!.Value)
            .Select(g =>
            {
                var vendor = g.First().Vendor!;
                var services = g.Where(e => CountsToward1099(e.Category)).ToList();
                var reportable = services.Where(e => e.PaidWith != PaymentMethod.Card).Sum(e => e.Amount);
                return new Vendor1099(
                    vendor,
                    reportable,
                    services.Where(e => e.PaidWith == PaymentMethod.Card).Sum(e => e.Amount),
                    g.Where(e => !CountsToward1099(e.Category)).Sum(e => e.Amount),
                    g.Count(),
                    taxForms.Any(t => t.Contains(vendor.Name.Trim(), StringComparison.OrdinalIgnoreCase)),
                    !vendor.Exempt1099 && reportable >= limit);
            })
            .OrderByDescending(v => v.Needs1099).ThenByDescending(v => v.Reportable).ThenBy(v => v.Vendor.Name)
            .ToList();
    }

    public async Task<byte[]> CsvAsync(int year, decimal? threshold = null, CancellationToken ct = default)
    {
        var rows = await GetAsync(year, threshold, ct);
        return Csv.File(
        [
            Csv.Line("Vendor", "Trade", "Phone", "Email", "Counts toward 1099", "Paid by card (not reported)", "Not for services (not reported)",
                "Payments", "Needs 1099-NEC", "W-9 on file", "Marked exempt"),
            .. rows.Select(r => Csv.Line(r.Vendor.Name, r.Vendor.Trade, r.Vendor.Phone, r.Vendor.Email, r.Reportable, r.PaidByCard, r.NotForServices,
                r.Payments, r.Needs1099, r.HasW9, r.Vendor.Exempt1099)),
        ]);
    }
}
