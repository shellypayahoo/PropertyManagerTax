using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Reports;

/// <summary>
/// One zip for the tax preparer with a year's books: the profit and loss PDF, every expense and rent payment as
/// spreadsheets, the 1099 vendor list, and all the receipts.
/// </summary>
public class AccountantPacket(
    IDbContextFactory<PropertyManagerDbContext> dbFactory, ProfitAndLossPdf profitAndLoss, Form1099Report form1099, IDocumentStorage storage,
    TimeProvider clock, IOptionsMonitor<BusinessOptions>? business = null)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string FileName(int year) => $"Accountant packet {year}.zip";

    public async Task WriteAsync(int year, Stream output, CancellationToken ct = default)
    {
        var (from, to) = (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expenses = (await db.Expenses.AsNoTracking()
                .Include(e => e.Property).Include(e => e.Unit).Include(e => e.Vendor).Include(e => e.Receipts)
                .Where(e => e.Date >= from && e.Date <= to)
                .ToListAsync(ct))
            .OrderBy(e => e.Date).ThenBy(e => e.Id).ToList();
        var payments = (await db.RentPayments.AsNoTracking()
                .Include(p => p.Lease).ThenInclude(l => l!.Tenants)
                .Include(p => p.Lease).ThenInclude(l => l!.Unit).ThenInclude(u => u!.Property)
                .Where(p => p.Date >= from && p.Date <= to)
                .ToListAsync(ct))
            .OrderBy(p => p.Date).ThenBy(p => p.Id).ToList();

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await AddAsync(zip, $"Profit and loss {year}.pdf", await profitAndLoss.CreateAsync(year, includeDetail: true, ct), ct);

        // Receipts first, so the expense list can say which files go with each expense.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var receiptNames = new Dictionary<int, List<string>>();
        var missing = 0;
        foreach (var expense in expenses)
        {
            foreach (var receipt in expense.Receipts.OrderBy(r => r.Id))
            {
                await using var file = await storage.OpenReadAsync(receipt.StorageKey, ct);
                if (file is null)
                {
                    missing++;
                    continue;
                }
                var name = Unique(used, Safe($"{expense.Date:yyyy-MM-dd} {expense.Vendor?.Name ?? expense.Description} {expense.Amount.ToString("0.00", CultureInfo.InvariantCulture)}"),
                    Path.GetExtension(receipt.FileName));
                var entry = zip.CreateEntry($"Receipts/{name}", CompressionLevel.Fastest);
                await using (var stream = entry.Open())
                {
                    await file.CopyToAsync(stream, ct);
                }
                (receiptNames.TryGetValue(expense.Id, out var list) ? list : receiptNames[expense.Id] = []).Add(name);
            }
        }

        await AddAsync(zip, $"Expenses {year}.csv", Csv.File(
        [
            Csv.Line("Date", "Property", "Unit", "Category", "Schedule E line", "Deductible", "Description", "Vendor", "Paid with", "Amount", "Notes", "Receipts"),
            .. expenses.Select(e => Csv.Line(e.Date, e.IsShared ? "All properties (shared)" : e.Property?.Name, e.Unit?.Label, e.Category.DisplayName(),
                e.Category.ScheduleELine(), e.Category.IsDeductible(), e.Description, e.Vendor?.Name, e.PaidWith, e.Amount, e.Notes,
                string.Join("; ", receiptNames.GetValueOrDefault(e.Id) ?? []))),
        ]), ct);
        await AddAsync(zip, $"Rent received {year}.csv", Csv.File(
        [
            Csv.Line("Date received", "For month", "Property", "Unit", "Tenants", "Method", "Amount", "Notes"),
            .. payments.Select(p => Csv.Line(p.Date, p.ForMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture), p.Lease?.Unit?.Property?.Name, p.Lease?.Unit?.Label,
                p.Lease?.TenantNames, p.Method, p.Amount, p.Notes)),
        ]), ct);
        await AddAsync(zip, $"1099 vendors {year}.csv", await form1099.CsvAsync(year, ct: ct), ct);

        var properties = await db.Properties.AsNoTracking().Include(p => p.Assets).OrderBy(p => p.Name).ToListAsync(ct);
        // Depreciation entered by hand as an expense replaces the calculated amount for that property.
        var enteredByHand = expenses.Where(e => e.Category == ExpenseCategory.Depreciation && e.PropertyId != null).Select(e => e.PropertyId!.Value).ToHashSet();
        var schedule = properties.Where(p => !enteredByHand.Contains(p.Id)).SelectMany(p => Depreciation.Schedule(p, year)).ToList();
        if (schedule.Count > 0)
        {
            await AddAsync(zip, $"Depreciation {year}.csv", Csv.File(
            [
                Csv.Line("Property", "Item", "Placed in service", "Cost or basis", "Recovery period", $"Before {year}", $"{year} deduction", "Total so far", "Remaining"),
                .. schedule.Select(l => Csv.Line(l.PropertyName, l.Item, l.PlacedInService, l.Basis, l.Life.ShortName(), l.PriorYears, l.ThisYear, l.Accumulated, l.Remaining)),
            ]), ct);
        }

        var deductible = expenses.Where(e => e.Category.IsDeductible()).Sum(e => e.Amount);
        var readme = string.Create(Us, $"""
            {(string.IsNullOrWhiteSpace(business?.CurrentValue.Name) ? "Rental properties" : business!.CurrentValue.Name)}: books for {year}
            Made {clock.GetLocalNow():MMMM d, yyyy} by Property Manager.

            In this folder
            - Profit and loss {year}.pdf: income and expenses by property and Schedule E line, with the items behind each line.
            - Expenses {year}.csv: all {expenses.Count} expenses. "Receipts" names the files in the Receipts folder.
            - Rent received {year}.csv: all {payments.Count} rent payments, by the date received.
            - 1099 vendors {year}.csv: what each vendor was paid and who looks to need a Form 1099-NEC
              (threshold used: {Form1099Report.DefaultThreshold(year):C0}; card payments and non-service payments left out).
            - Depreciation {year}.csv: {(schedule.Count > 0 ? "the depreciation schedule (MACRS, calculated by the app) for each building and improvement." : "not included; nothing is set up to be depreciated.")}
            - Receipts: {receiptNames.Values.Sum(v => v.Count)} receipt and invoice files, named by date, vendor, and amount.{(missing > 0 ? $"\n  ({missing} receipt file(s) couldn't be found and were left out.)" : "")}

            Totals
            - Rent received: {payments.Sum(p => p.Amount):C}
            - Deductible expenses paid: {deductible:C}{(schedule.Count > 0 ? $"\n- Depreciation (calculated, not a payment): {schedule.Sum(l => l.ThisYear):C}" : "")}
            - Not deductible (mortgage principal, escrow deposits): {expenses.Sum(e => e.Amount) - deductible:C}

            Notes
            - Expenses marked "All properties (shared)" are split across the properties by number of units on the profit and loss.
            - Depreciation is calculated from each property's purchase details and improvements (see the schedule), unless it
              was entered by hand as an expense. Bonus depreciation and Section 179 aren't included.
            - {expenses.Count(e => e.Receipts.Count == 0)} expense(s) have no receipt attached.
            - This is a bookkeeping summary, not tax advice.
            """);
        await AddAsync(zip, "Read me first.txt", Encoding.UTF8.GetBytes(readme.ReplaceLineEndings("\r\n")), ct);
    }

    private static async Task AddAsync(ZipArchive zip, string name, byte[] content, CancellationToken ct)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(content, ct);
    }

    /// <summary>A file name without characters that Windows or macOS won't accept.</summary>
    private static string Safe(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.' or '_' or '&' or '(' or ')' ? c : ' ').ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }

    private static string Unique(HashSet<string> used, string name, string extension)
    {
        var ext = extension.Length is > 0 and <= 6 ? extension.ToLowerInvariant() : "";
        var candidate = name + ext;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{name} ({n}){ext}";
        }
        return candidate;
    }
}
