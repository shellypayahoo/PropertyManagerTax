using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;

namespace PropertyManager.Core.Assistant;

/// <summary>
/// Read-only functions the assistant can call to look things up. Each returns plain text, one item per line,
/// so any model can read it. Nothing here changes data.
/// </summary>
public class AssistantTools(IDbContextFactory<PropertyManagerDbContext> dbFactory, TextMessageSearch search, ReportService reports, TimeProvider? time = null)
{
    private const int MaxMessages = 150;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private DateOnly Today => DateOnly.FromDateTime((time ?? TimeProvider.System).GetLocalNow().DateTime);

    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(SearchTextMessagesAsync, "search_text_messages"),
        AIFunctionFactory.Create(GetConversationAsync, "get_conversation"),
        AIFunctionFactory.Create(ListContactsAsync, "list_text_contacts"),
        AIFunctionFactory.Create(ListPropertiesAndTenantsAsync, "list_properties_and_tenants"),
        AIFunctionFactory.Create(GetRentStatusAsync, "get_rent_status"),
        AIFunctionFactory.Create(SearchExpensesAsync, "search_expenses"),
        AIFunctionFactory.Create(ListMaintenanceRequestsAsync, "list_maintenance_requests"),
        AIFunctionFactory.Create(GetProfitAndLossAsync, "get_profit_and_loss"),
    ];

    [Description("Searches imported text messages with tenants and vendors. Every word of the query must appear in a message (case-insensitive), so use one or two distinctive words, and try synonyms if nothing is found. Returns the newest matches in date order, each with date, time, sender, and text.")]
    public async Task<string> SearchTextMessagesAsync(
        [Description("Words to look for, e.g. \"leak\" or \"deposit\". Leave empty to list messages by contact or date only.")] string? query = null,
        [Description("Optional: a contact's name, tenant or vendor name, phone number, or email, to search only that conversation.")] string? contact = null,
        [Description("Optional start date, yyyy-MM-dd.")] string? fromDate = null,
        [Description("Optional end date, yyyy-MM-dd (inclusive).")] string? toDate = null,
        [Description("Maximum messages to return (default 50, up to 150).")] int limit = 50,
        CancellationToken ct = default)
    {
        var handles = await ResolveContactAsync(contact, ct);
        if (handles is { Count: 0 })
        {
            return $"No text contact matches \"{contact}\". Call list_text_contacts to see who's imported.";
        }
        var found = new List<TextMessage>();
        foreach (var handle in handles ?? [null])
        {
            found.AddRange(await search.SearchAsync(query, handle, StartOf(fromDate), EndOf(toDate), Math.Clamp(limit, 1, MaxMessages), ct));
        }
        var messages = found.OrderBy(m => m.SentAt).TakeLast(Math.Clamp(limit, 1, MaxMessages)).ToList();
        return messages.Count == 0
            ? "No messages found."
            : $"{messages.Count} message(s):\n" + await FormatAsync(messages, ct);
    }

    [Description("Returns a conversation with one contact in date order, to read context around a message. Use a date range to read a specific period.")]
    public async Task<string> GetConversationAsync(
        [Description("The contact's name, tenant or vendor name, phone number, or email.")] string contact,
        [Description("Optional start date, yyyy-MM-dd.")] string? fromDate = null,
        [Description("Optional end date, yyyy-MM-dd (inclusive).")] string? toDate = null,
        [Description("Maximum messages to return, newest kept (default 100, up to 150).")] int limit = 100,
        CancellationToken ct = default) =>
        await SearchTextMessagesAsync(null, contact, fromDate, toDate, limit, ct);

    [Description("Lists the people whose text messages are imported: name, phone or email, whether they're a tenant or vendor, message count, and last message date.")]
    public async Task<string> ListContactsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contacts = await db.MessageContacts.AsNoTracking().Include(c => c.Tenant).Include(c => c.Vendor)
            .Where(c => c.Included).ToListAsync(ct);
        if (contacts.Count == 0)
        {
            return "No text messages have been imported yet (Messages page).";
        }
        var text = new StringBuilder();
        foreach (var c in contacts.OrderBy(c => c.Name ?? c.Handle))
        {
            var role = c.Tenant is not null ? $"tenant {c.Tenant.FullName}" : c.Vendor is not null ? $"vendor {c.Vendor.Name}" : "not linked";
            text.AppendLine($"- {c.Name ?? "(no name)"} | {c.Handle} | {role} | {c.MessageCount} messages | last {c.LastMessageAt?.ToLocalTime():yyyy-MM-dd}");
        }
        return text.ToString();
    }

    [Description("Lists properties, units, current and past leases (tenants, dates, rent, deposit), tenant contact details, and vendors.")]
    public async Task<string> ListPropertiesAndTenantsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var properties = await db.Properties.AsNoTracking()
            .Include(p => p.Units).ThenInclude(u => u.Leases).ThenInclude(l => l.Tenants)
            .OrderBy(p => p.Name).ToListAsync(ct);
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Name).ToListAsync(ct);

        var text = new StringBuilder($"Today is {Today:yyyy-MM-dd}.\n");
        foreach (var property in properties)
        {
            text.AppendLine($"Property: {property.Name} ({property.FullAddress})");
            foreach (var unit in property.Units.OrderBy(u => u.Label))
            {
                text.AppendLine($"  Unit {unit.Label}: market rent {Money(unit.MarketRent)}");
                foreach (var lease in unit.Leases.OrderByDescending(l => l.StartDate))
                {
                    var state = lease.IsActiveOn(Today) ? "current" : lease.StartDate > Today ? "future" : "ended";
                    text.AppendLine($"    Lease #{lease.Id} ({state}): {lease.TenantNames}, {lease.StartDate:yyyy-MM-dd} to {(lease.EndDate is { } e ? e.ToString("yyyy-MM-dd") : "month-to-month")}, rent {Money(lease.MonthlyRent)}, deposit {Money(lease.SecurityDeposit)}");
                    foreach (var tenant in lease.Tenants)
                    {
                        text.AppendLine($"      Tenant {tenant.FullName}: phone {tenant.Phone ?? "-"}, email {tenant.Email ?? "-"}{(string.IsNullOrWhiteSpace(tenant.Notes) ? "" : ", notes: " + tenant.Notes)}");
                    }
                }
            }
        }
        if (vendors.Count > 0)
        {
            text.AppendLine("Vendors:");
            foreach (var v in vendors)
            {
                text.AppendLine($"  {v.Name} ({v.Trade ?? "no trade"}): phone {v.Phone ?? "-"}, email {v.Email ?? "-"}");
            }
        }
        return text.ToString();
    }

    [Description("Rent due and paid per lease for each month in a range: status (Paid, Partial, Unpaid, Upcoming), amount paid, and payment dates and methods.")]
    public async Task<string> GetRentStatusAsync(
        [Description("First month, yyyy-MM. Defaults to the current month.")] string? fromMonth = null,
        [Description("Last month, yyyy-MM. Defaults to fromMonth.")] string? toMonth = null,
        CancellationToken ct = default)
    {
        var first = ParseMonth(fromMonth) ?? RentPayment.MonthOf(Today);
        var last = ParseMonth(toMonth) ?? first;
        if (last < first || first.AddMonths(36) < last)
        {
            return "Choose a range of 1 to 36 months, with fromMonth before toMonth.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var leases = await db.Leases.AsNoTracking()
            .Include(l => l.Tenants).Include(l => l.Payments).Include(l => l.Unit).ThenInclude(u => u!.Property)
            .ToListAsync(ct);
        var text = new StringBuilder();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            text.AppendLine($"{month:MMMM yyyy}:");
            foreach (var lease in leases.Where(l => RentSchedule.IsDue(l, month)).OrderBy(l => l.Unit?.DisplayName))
            {
                var r = RentSchedule.ForMonth(lease, month, Today);
                var payments = string.Join(", ", r.Payments.Select(p => $"{Money(p.Amount)} on {p.Date:yyyy-MM-dd} by {p.Method}"));
                text.AppendLine($"  {lease.Unit?.DisplayName} ({lease.TenantNames}): due {Money(r.Due)}, paid {Money(r.Paid)}, {r.Status}{(payments.Length > 0 ? " [" + payments + "]" : "")}{(r.Warning is { } w ? " WARNING: " + w : "")}");
            }
        }
        return text.ToString();
    }

    [Description("Finds recorded expenses by date range, property, category, or words in the description, vendor, or notes. Returns each expense and the total.")]
    public async Task<string> SearchExpensesAsync(
        [Description("Start date, yyyy-MM-dd. Defaults to January 1 of this year.")] string? fromDate = null,
        [Description("End date, yyyy-MM-dd (inclusive). Defaults to today.")] string? toDate = null,
        [Description("Optional: part of a property name or street, e.g. \"Taylor\". Use \"shared\" for expenses shared by all properties.")] string? property = null,
        [Description("Optional: part of a category name, e.g. \"Repairs\", \"Utilities\", \"Mortgage interest\".")] string? category = null,
        [Description("Optional: words to find in the description, vendor, or notes.")] string? text = null,
        CancellationToken ct = default)
    {
        var from = ParseDate(fromDate) ?? new DateOnly(Today.Year, 1, 1);
        var to = ParseDate(toDate) ?? Today;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expenses = await db.Expenses.AsNoTracking()
            .Include(e => e.Property).Include(e => e.Unit).Include(e => e.Vendor)
            .Where(e => e.Date >= from && e.Date <= to)
            .ToListAsync(ct);

        var matches = expenses
            .Where(e => property is null
                || (property.Equals("shared", StringComparison.OrdinalIgnoreCase) ? e.IsShared : Contains(e.Property?.Name, property) || Contains(e.Property?.Street, property)))
            .Where(e => category is null || Contains(e.Category.DisplayName(), category))
            .Where(e => text is null || Contains(e.Description, text) || Contains(e.Vendor?.Name, text) || Contains(e.Notes, text))
            .OrderBy(e => e.Date)
            .ToList();
        if (matches.Count == 0)
        {
            return $"No expenses found from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.";
        }

        var result = new StringBuilder($"{matches.Count} expense(s) from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}, total {Money(matches.Sum(e => e.Amount))}:\n");
        foreach (var e in matches.Take(300))
        {
            var where = e.IsShared ? "All properties (shared)" : e.Unit?.DisplayName ?? e.Property?.Name;
            result.AppendLine($"- {e.Date:yyyy-MM-dd} | {Money(e.Amount)} | {e.Category.DisplayName()} | {where} | {e.Description}{(e.Vendor is null ? "" : " | vendor " + e.Vendor.Name)}{(e.PaidWith is { } m ? " | paid by " + m : "")}{(string.IsNullOrWhiteSpace(e.Notes) ? "" : " | notes: " + e.Notes)}");
        }
        if (matches.Count > 300)
        {
            result.AppendLine($"(first 300 shown; narrow the search to see the rest)");
        }
        return result.ToString();
    }

    [Description("Lists maintenance requests with unit, status, priority, dates, vendor, and description.")]
    public async Task<string> ListMaintenanceRequestsAsync(
        [Description("True to list only open requests (not completed or cancelled).")] bool openOnly = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var requests = await db.MaintenanceRequests.AsNoTracking()
            .Include(r => r.Unit).ThenInclude(u => u!.Property).Include(r => r.Vendor)
            .OrderByDescending(r => r.ReportedOn).ToListAsync(ct);
        var list = requests.Where(r => !openOnly || r.IsOpen).ToList();
        if (list.Count == 0)
        {
            return "No maintenance requests found.";
        }
        var text = new StringBuilder();
        foreach (var r in list)
        {
            text.AppendLine($"- {r.ReportedOn:yyyy-MM-dd} | {r.Unit?.DisplayName} | {r.Title} | {r.Status}, {r.Priority} priority{(r.ScheduledFor is { } s ? $" | scheduled {s:yyyy-MM-dd}" : "")}{(r.CompletedOn is { } c ? $" | completed {c:yyyy-MM-dd}" : "")}{(r.Vendor is null ? "" : " | vendor " + r.Vendor.Name)}{(string.IsNullOrWhiteSpace(r.Description) ? "" : " | " + r.Description)}");
        }
        return text.ToString();
    }

    [Description("The Schedule E profit and loss for a tax year, per property: rent received, scheduled rent, deductible expenses by Schedule E line (shared expenses already split by unit count), net income, and non-deductible amounts such as mortgage principal.")]
    public async Task<string> GetProfitAndLossAsync(
        [Description("Tax year, e.g. 2026.")] int year,
        CancellationToken ct = default)
    {
        var report = await reports.GetAnnualProfitAndLossAsync(year, ct);
        var text = new StringBuilder($"Profit and loss for {year}:\n");
        foreach (var p in report)
        {
            text.AppendLine($"{p.PropertyName}: rent received {Money(p.RentReceived)} (scheduled {Money(p.ScheduledRent)}), expenses {Money(p.TotalExpenses)}, net {Money(p.NetIncome)}");
            foreach (var e in p.Expenses.Where(e => e.Amount != 0m))
            {
                text.AppendLine($"  Line {e.Category.ScheduleELine()} {e.Category.DisplayName()}: {Money(e.Amount)}");
            }
            if (p.SharedExpenses != 0m)
            {
                text.AppendLine($"  (includes {Money(p.SharedExpenses)} share of expenses shared by all properties)");
            }
            foreach (var e in p.NotDeductible.Where(e => e.Amount != 0m))
            {
                text.AppendLine($"  Not deductible, {e.Category.DisplayName()}: {Money(e.Amount)}");
            }
        }
        return text.ToString();
    }

    /// <summary>Contact handles matching a name, phone, or email; null when no contact was given.</summary>
    private async Task<List<string?>?> ResolveContactAsync(string? contact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contact))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contacts = await db.MessageContacts.AsNoTracking().Include(c => c.Tenant).Include(c => c.Vendor)
            .Where(c => c.Included).ToListAsync(ct);
        var handle = ContactHandles.Normalize(contact);
        return contacts
            .Where(c => (handle is not null && c.Handle == handle)
                || Contains(c.Name, contact) || Contains(c.Tenant?.FullName, contact) || Contains(c.Vendor?.Name, contact))
            .Select(c => (string?)c.Handle)
            .ToList();
    }

    private async Task<string> FormatAsync(IReadOnlyList<TextMessage> messages, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await db.MessageContacts.AsNoTracking().Where(c => c.Name != null).ToDictionaryAsync(c => c.Handle, c => c.Name!, ct);
        string Name(string? handle) =>
            ContactHandles.Normalize(handle) is { } h && names.TryGetValue(h, out var name) ? name : handle ?? "unknown";

        var text = new StringBuilder();
        foreach (var m in messages)
        {
            var sender = m.IsFromMe ? "Me" : Name(m.SenderHandle ?? m.ContactHandle);
            var chat = m.IsGroupChat ? $" (group chat with {Name(m.ContactHandle)})" : m.IsFromMe ? $" (to {Name(m.ContactHandle)})" : "";
            var attachments = m.AttachmentCount > 0 ? $" [{m.AttachmentCount} attachment(s)]" : "";
            text.AppendLine($"[{m.SentAt.ToLocalTime().ToString("yyyy-MM-dd ddd h:mm tt", Us)}] {sender}{chat}: {m.Text}{attachments}");
        }
        return text.ToString();
    }

    private static bool Contains(string? value, string part) => value?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

    private static string Money(decimal amount) => amount.ToString("C", Us);

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, Us, out var date) ? date : null;

    private static DateOnly? ParseMonth(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM", Us, DateTimeStyles.None, out var month) ? month : ParseDate(value) is { } d ? RentPayment.MonthOf(d) : null;

    private static DateTimeOffset? StartOf(string? date) =>
        ParseDate(date) is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)) : null;

    private static DateTimeOffset? EndOf(string? date) =>
        ParseDate(date) is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local)) : null;
}
