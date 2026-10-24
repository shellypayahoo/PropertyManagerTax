using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Assistant;

/// <summary>
/// Read-only functions the assistant can call to look things up. Each returns plain text, one item per line,
/// so any model can read it. Nothing here changes data.
/// </summary>
public class AssistantTools(
    IDbContextFactory<PropertyManagerDbContext> dbFactory, TextMessageSearch search, ReportService reports, TimeProvider? time = null,
    IDocumentStorage? storage = null, IImageConverter? images = null, IOptions<FeatureOptions>? features = null)
{
    // Lookups for parts of the app turned off in "Features" aren't offered to the model.
    private static readonly string[] TextTools = ["search_text_messages", "get_conversation", "list_text_contacts"];
    private static readonly string[] EmailTools = ["search_emails", "read_email"];

    /// <summary>
    /// Asks the current AI model about a photo: (JPEG/PNG bytes, media type, question) → answer. Set by
    /// <see cref="AssistantService"/> for each question when photos are allowed; null means describe_photo isn't offered.
    /// </summary>
    public Func<byte[], string, string, CancellationToken, Task<string>>? PhotoDescriber { get; set; }

    private const int MaxMessages = 150;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private DateOnly Today => DateOnly.FromDateTime((time ?? TimeProvider.System).GetLocalNow().DateTime);

    public IList<AITool> All()
    {
        var on = features?.Value ?? new FeatureOptions();
        var tools = Base()
            .Where(t => (on.TextMessages || !TextTools.Contains(t.Name)) && (on.Email || !EmailTools.Contains(t.Name)))
            .ToList();
        if (PhotoDescriber is not null)
        {
            tools.Add(AIFunctionFactory.Create(DescribePhotoAsync, "describe_photo"));
        }
        return tools;
    }

    private IList<AITool> Base() =>
    [
        AIFunctionFactory.Create(SearchTextMessagesAsync, "search_text_messages"),
        AIFunctionFactory.Create(GetConversationAsync, "get_conversation"),
        AIFunctionFactory.Create(ListContactsAsync, "list_text_contacts"),
        AIFunctionFactory.Create(ListPropertiesAndTenantsAsync, "list_properties_and_tenants"),
        AIFunctionFactory.Create(GetRentStatusAsync, "get_rent_status"),
        AIFunctionFactory.Create(SearchExpensesAsync, "search_expenses"),
        AIFunctionFactory.Create(ListMaintenanceRequestsAsync, "list_maintenance_requests"),
        AIFunctionFactory.Create(GetProfitAndLossAsync, "get_profit_and_loss"),
        AIFunctionFactory.Create(SearchEmailsAsync, "search_emails"),
        AIFunctionFactory.Create(ReadEmailAsync, "read_email"),
        AIFunctionFactory.Create(ListDocumentsAsync, "list_documents"),
        AIFunctionFactory.Create(SearchDocumentsAsync, "search_documents"),
        AIFunctionFactory.Create(ReadDocumentAsync, "read_document"),
    ];

    // Kept modest so a question fits within small tokens-per-minute limits; the model can ask for more.
    private const int DocumentChunk = 12_000;

    [Description("Searches imported text messages with tenants and vendors. Every word of the query must appear in a message (case-insensitive), so use one or two distinctive words, and try synonyms if nothing is found. Returns the newest matches in date order, each with date, time, sender, and text.")]
    public async Task<string> SearchTextMessagesAsync(
        [Description("Words to look for, e.g. \"leak\" or \"deposit\". Leave empty to list messages by contact or date only.")] string? query = null,
        [Description("Optional: a contact's name, tenant or vendor name, phone number, or email, to search only that conversation.")] string? contact = null,
        [Description("Optional start date, yyyy-MM-dd.")] string? fromDate = null,
        [Description("Optional end date, yyyy-MM-dd (inclusive).")] string? toDate = null,
        [Description("Maximum messages to return (default 30, up to 150).")] int limit = 30,
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
        [Description("Maximum messages to return, newest kept (default 60, up to 150).")] int limit = 60,
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
            .Include(e => e.Property).Include(e => e.Unit).Include(e => e.Vendor).Include(e => e.Receipts)
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
            result.AppendLine($"- {e.Date:yyyy-MM-dd} | {Money(e.Amount)} | {e.Category.DisplayName()} | {where} | {e.Description}{(e.Vendor is null ? "" : " | vendor " + e.Vendor.Name)}{(e.PaidWith is { } m ? " | paid by " + m : "")}{(string.IsNullOrWhiteSpace(e.Notes) ? "" : " | notes: " + e.Notes)}"
                + (e.Receipts.Count == 0 ? " | no receipt" : " | receipt: " + string.Join(", ", e.Receipts.Select(r => $"{r.FileName} ({(r.IsImage ? "photo" : "document")} R{r.Id})"))));
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

    [Description("Searches imported emails with tenants and vendors (subject and newly written text; quoted earlier replies are left out). Every word must appear (case-insensitive), so use one or two distinctive words and try synonyms if nothing is found. Returns the newest matches in date order with date, sender, recipients, subject, text, and attachment names.")]
    public async Task<string> SearchEmailsAsync(
        [Description("Words to look for. Leave empty to list emails by contact or date only.")] string? query = null,
        [Description("Optional: a tenant's or vendor's name, a person's name, or an email address, to search only emails with them.")] string? contact = null,
        [Description("Optional start date, yyyy-MM-dd.")] string? fromDate = null,
        [Description("Optional end date, yyyy-MM-dd (inclusive).")] string? toDate = null,
        [Description("Maximum emails to return (default 10, up to 50).")] int limit = 10,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var emails = db.EmailMessages.AsNoTracking().Include(m => m.Attachments).AsQueryable();
        if (!string.IsNullOrWhiteSpace(contact))
        {
            var tenantIds = (await db.Tenants.AsNoTracking().ToListAsync(ct)).Where(t => Contains(t.FullName, contact)).Select(t => (int?)t.Id).ToList();
            var vendorIds = (await db.Vendors.AsNoTracking().ToListAsync(ct)).Where(v => Contains(v.Name, contact)).Select(v => (int?)v.Id).ToList();
            var pattern = $"%{contact.Trim().Replace("%", "").Replace("_", "")}%";
            emails = emails.Where(m => tenantIds.Contains(m.TenantId) || vendorIds.Contains(m.VendorId)
                || EF.Functions.Like(m.FromAddress, pattern) || EF.Functions.Like(m.FromName ?? "", pattern)
                || EF.Functions.Like(m.To, pattern) || EF.Functions.Like(m.Cc, pattern));
        }
        if (StartOf(fromDate) is { } from)
        {
            emails = emails.Where(m => m.SentAt >= from);
        }
        if (EndOf(toDate) is { } to)
        {
            emails = emails.Where(m => m.SentAt <= to);
        }
        foreach (var word in (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = $"%{word.Replace("%", "").Replace("_", "")}%";
            emails = emails.Where(m => EF.Functions.Like(m.Subject, pattern) || EF.Functions.Like(m.Text, pattern));
        }

        var found = await emails.OrderByDescending(m => m.SentAt).Take(Math.Clamp(limit, 1, 50)).ToListAsync(ct);
        if (found.Count == 0)
        {
            return await db.EmailMessages.AnyAsync(ct) ? "No emails found." : "No emails have been imported yet (Email page).";
        }
        found.Reverse();
        var text = new StringBuilder($"{found.Count} email(s):\n");
        foreach (var m in found)
        {
            var body = m.Text.Length > 800 ? m.Text[..800] + $" … (truncated; read_email {m.Id} for all of it)" : m.Text;
            text.AppendLine($"Email #{m.Id} [{m.SentAt.ToLocalTime().ToString("yyyy-MM-dd ddd h:mm tt", Us)}] From {(m.IsFromMe ? "Me" : m.FromDisplay)} to {m.To}{(m.Cc.Length > 0 ? ", cc " + m.Cc : "")}");
            text.AppendLine($"  Subject: {m.Subject}");
            text.AppendLine($"  {body.ReplaceLineEndings("\n  ")}");
            if (m.Attachments.Count > 0)
            {
                text.AppendLine($"  Attachments: {string.Join(", ", m.Attachments.Select(a => $"{a.FileName} (document E{a.Id})"))}");
            }
        }
        return text.ToString();
    }

    [Description("Reads one email in full, including the quoted earlier replies below it, with its attachments.")]
    public async Task<string> ReadEmailAsync(
        [Description("The email id from search_emails.")] int emailId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var m = await db.EmailMessages.AsNoTracking().Include(e => e.Attachments).FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (m is null)
        {
            return $"There's no email #{emailId}.";
        }
        var body = m.FullText.Length > DocumentChunk ? m.FullText[..DocumentChunk] + "\n(truncated)" : m.FullText;
        var attachments = m.Attachments.Count == 0 ? "none"
            : string.Join(", ", m.Attachments.Select(a => $"{a.FileName} (document E{a.Id}{(a.HasText ? "" : ", no readable text")})"));
        return $"""
            Email #{m.Id}
            Date: {m.SentAt.ToLocalTime().ToString("yyyy-MM-dd ddd h:mm tt", Us)}
            From: {(m.IsFromMe ? "Me (" + m.FromAddress + ")" : m.FromDisplay)}
            To: {m.To}{(m.Cc.Length > 0 ? "\nCc: " + m.Cc : "")}
            Subject: {m.Subject}
            Attachments: {attachments}

            {body}
            """;
    }

    [Description("Lists documents: files uploaded to leases (signed leases, addenda, inspection reports) and files attached to imported emails and texts, and expense receipts. Shows id, file name, where it's from, date, and whether its text can be read. Photos and scanned PDFs have no readable text.")]
    public async Task<string> ListDocumentsAsync(CancellationToken ct = default)
    {
        var documents = await LoadDocumentsAsync(ct);
        if (documents.Count == 0)
        {
            return "No documents have been uploaded (open a lease on the Leases page to upload) or imported with emails.";
        }
        var text = new StringBuilder();
        foreach (var d in documents)
        {
            var readable = !string.IsNullOrWhiteSpace(d.Text)
                ? $"{d.Text.Length:N0} characters{(Pages(d.Text) is > 0 and var pages ? $", {pages} page(s)" : "")}"
                : d.ContentType.StartsWith("image/") ? "picture, no readable text" : "no readable text (scanned or unreadable)";
            text.AppendLine($"- Document {d.Id}: {d.FileName} | {d.Source} | {d.Date.ToLocalTime():yyyy-MM-dd} | {readable}");
        }
        return text.ToString();
    }

    [Description("Searches the text of documents: files uploaded to leases and PDF/Word files attached to emails and texts, and expense receipts. Every word must appear somewhere in a document (case-insensitive). Returns matching documents with short excerpts around the words, each with its page and character position; use read_document to read more.")]
    public async Task<string> SearchDocumentsAsync(
        [Description("Words to look for, e.g. \"pets\" or \"late fee\".")] string query,
        [Description("Optional: only documents whose lease or email matches this tenant, sender, unit, or property (e.g. \"Taylor\").")] string? lease = null,
        CancellationToken ct = default)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "Give one or more words to search for.";
        }
        var documents = (await LoadDocumentsAsync(ct))
            .Where(d => !string.IsNullOrWhiteSpace(d.Text) && (lease is null || Contains(d.Source, lease)))
            .Where(d => words.All(w => Contains(d.Text, w)))
            .ToList();
        if (documents.Count == 0)
        {
            return "No documents contain all of those words.";
        }

        var result = new StringBuilder($"{documents.Count} document(s):\n");
        foreach (var d in documents.Take(10))
        {
            result.AppendLine($"Document {d.Id}: {d.FileName} ({d.Source})");
            foreach (var (hit, excerpt) in Excerpts(d.Text!, words))
            {
                result.AppendLine($"  [{PageAt(d.Text!, hit)}character {hit}] ...{excerpt}...");
            }
        }
        return result.ToString();
    }

    [Description("Reads the text of one document, 12,000 characters at a time. PDF pages are marked [Page N].")]
    public async Task<string> ReadDocumentAsync(
        [Description("The document id from list_documents, search_documents, or an email's attachments, e.g. \"L3\", \"E12\", or \"T7\".")] string documentId,
        [Description("Character position to start reading from (default 0, the beginning).")] int fromCharacter = 0,
        CancellationToken ct = default)
    {
        var id = documentId.Trim().TrimStart('#').ToUpperInvariant();
        var d = (await LoadDocumentsAsync(ct)).FirstOrDefault(x => x.Id == id || x.Id == "L" + id);
        if (d is null)
        {
            return $"There's no document {documentId}.";
        }
        if (string.IsNullOrWhiteSpace(d.Text))
        {
            return $"{d.FileName} has no readable text (picture or scanned document); the owner can open it in the app.";
        }
        var start = Math.Clamp(fromCharacter, 0, d.Text.Length);
        var end = Math.Min(start + DocumentChunk, d.Text.Length);
        var more = end < d.Text.Length ? $"\n(continues; call read_document with fromCharacter={end} for more)" : "\n(end of document)";
        return $"Document {d.Id}: {d.FileName} ({d.Source}), characters {start}-{end} of {d.Text.Length}:\n{d.Text[start..end]}{more}";
    }

    /// <summary>A lease document ("L" + id) or email attachment ("E" + id), described for the assistant.</summary>
    private record DocumentInfo(string Id, string FileName, string Source, DateTimeOffset Date, string ContentType, string? Text);

    private async Task<List<DocumentInfo>> LoadDocumentsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var leaseDocuments = await db.LeaseDocuments.AsNoTracking()
            .Include(d => d.Lease).ThenInclude(l => l!.Tenants)
            .Include(d => d.Lease).ThenInclude(l => l!.Unit).ThenInclude(u => u!.Property)
            .ToListAsync(ct);
        var attachments = await db.EmailAttachments.AsNoTracking()
            .Include(a => a.EmailMessage).ThenInclude(m => m!.Tenant)
            .Include(a => a.EmailMessage).ThenInclude(m => m!.Vendor)
            .ToListAsync(ct);
        var textFiles = await db.TextAttachments.AsNoTracking()
            .Include(a => a.TextMessage)
            .Where(a => a.Status == TextAttachmentStatus.Saved && !a.ContentType.StartsWith("image/") && !a.ContentType.StartsWith("video/"))
            .ToListAsync(ct);
        var names = await db.MessageContacts.AsNoTracking().Where(c => c.Name != null).ToDictionaryAsync(c => c.Handle, c => c.Name!, ct);
        var receipts = await db.ExpenseReceipts.AsNoTracking()
            .Include(r => r.Expense).ThenInclude(e => e!.Property)
            .Include(r => r.Expense).ThenInclude(e => e!.Vendor)
            .ToListAsync(ct);

        return leaseDocuments
            .OrderBy(d => d.Lease?.Unit?.DisplayName).ThenBy(d => d.UploadedAt.UtcTicks)
            .Select(d => new DocumentInfo("L" + d.Id, d.FileName, LeaseLabel(d.Lease), d.UploadedAt, d.ContentType, d.Text))
            .Concat(attachments
                .OrderBy(a => a.EmailMessage!.SentAt.UtcTicks)
                .Select(a => new DocumentInfo("E" + a.Id, a.FileName, EmailLabel(a.EmailMessage!), a.EmailMessage!.SentAt, a.ContentType, a.Text)))
            .Concat(textFiles
                .OrderBy(a => a.TextMessage!.SentAt.UtcTicks)
                .Select(a => new DocumentInfo("T" + a.Id, a.FileName,
                    $"sent by text {(a.TextMessage!.IsFromMe ? "from Me to" : "from")} {names.GetValueOrDefault(a.TextMessage.ContactHandle) ?? a.TextMessage.ContactHandle}, {a.TextMessage.SentAt.ToLocalTime():yyyy-MM-dd}",
                    a.TextMessage.SentAt, a.ContentType, a.Text)))
            .Concat(receipts
                .OrderBy(r => r.Expense!.Date)
                .Select(r => new DocumentInfo("R" + r.Id, r.FileName,
                    $"receipt for expense {r.Expense!.Date:yyyy-MM-dd} {Money(r.Expense.Amount)} {r.Expense.Description}{(r.Expense.Vendor is null ? "" : " (" + r.Expense.Vendor.Name + ")")}, {(r.Expense.IsShared ? "all properties" : r.Expense.Property?.Name)}",
                    r.UploadedAt, r.ContentType, r.Text)))
            .ToList();
    }

    private static string EmailLabel(EmailMessage m)
    {
        var who = m.Tenant?.FullName ?? m.Vendor?.Name ?? (m.IsFromMe ? m.To : m.FromDisplay);
        return $"attached to email #{m.Id} \"{m.Subject}\" {(m.IsFromMe ? "from Me to" : "from")} {who}, {m.SentAt.ToLocalTime():yyyy-MM-dd}";
    }

    private static string LeaseLabel(Lease? lease) =>
        lease is null ? "no lease" : $"lease file, {lease.Unit?.DisplayName}, lease #{lease.Id} with {lease.TenantNames}, {lease.StartDate:yyyy-MM-dd} to {(lease.EndDate is { } e ? e.ToString("yyyy-MM-dd") : "month-to-month")}";

    private static int Pages(string text) => text.Split("[Page ").Length - 1;

    private static string PageAt(string text, int index)
    {
        var marker = text.LastIndexOf("[Page ", Math.Min(index, text.Length - 1), StringComparison.Ordinal);
        if (marker < 0)
        {
            return "";
        }
        var close = text.IndexOf(']', marker);
        return close < 0 ? "" : text[(marker + 1)..close] + ", ";
    }

    /// <summary>
    /// Up to 8 excerpts around the first occurrences of each word, merged where they overlap, each with the position
    /// of its first match.
    /// </summary>
    private static List<(int Hit, string Text)> Excerpts(string text, IReadOnlyList<string> words)
    {
        const int around = 200;
        var hits = new List<int>();
        foreach (var word in words)
        {
            for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase), n = 0; i >= 0 && n < 4; i = text.IndexOf(word, i + word.Length, StringComparison.OrdinalIgnoreCase), n++)
            {
                hits.Add(i);
            }
        }
        var ranges = new List<(int Hit, int Start, int End)>();
        foreach (var hit in hits.Order())
        {
            var (start, end) = (Math.Max(0, hit - around), Math.Min(text.Length, hit + around));
            if (ranges.Count > 0 && start <= ranges[^1].End)
            {
                ranges[^1] = ranges[^1] with { End = Math.Max(ranges[^1].End, end) };
            }
            else
            {
                ranges.Add((hit, start, end));
            }
        }
        return ranges.Take(8).Select(r => (r.Hit, string.Join(' ', text[r.Start..r.End].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))).ToList();
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
            var attachments = m.Attachments.Count > 0
                ? " [" + string.Join("; ", m.Attachments.Select(Describe)) + "]"
                : m.AttachmentCount > 0 ? $" [{m.AttachmentCount} attachment(s)]" : "";
            text.AppendLine($"[{m.SentAt.ToLocalTime().ToString("yyyy-MM-dd ddd h:mm tt", Us)}] {sender}{chat}: {m.Text}{attachments}");
        }
        return text.ToString();
    }

    private static string Describe(TextAttachment a) => a.Status switch
    {
        TextAttachmentStatus.NotOnMac => $"{a.FileName}, not downloaded",
        TextAttachmentStatus.TooLarge => $"{a.FileName}, too large to copy",
        _ when a.IsImage => $"photo {a.FileName} (attachment T{a.Id})",
        _ when a.IsVideo => $"video {a.FileName}",
        _ => $"file {a.FileName} (document T{a.Id})",
    };

    [Description("Looks at a photo sent by text (id \"T…\") or email (id \"E…\"), or a receipt photo (id \"R…\"), and answers a question about it, e.g. what damage it shows or what a document in it says. Use when a message's text doesn't say enough and the photo matters to the question.")]
    public async Task<string> DescribePhotoAsync(
        [Description("The photo's attachment id from a message, e.g. \"T12\" or \"E5\".")] string attachmentId,
        [Description("What to find out from the photo, in the context of the owner's question.")] string question,
        CancellationToken ct = default)
    {
        if (PhotoDescriber is null || storage is null)
        {
            return "Looking at photos is turned off.";
        }
        var id = attachmentId.Trim().TrimStart('#').ToUpperInvariant();
        if (id.Length < 2 || !int.TryParse(id[1..], out var number))
        {
            return $"\"{attachmentId}\" isn't a photo id; use the T… or E… id shown with the message.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        (byte[] Bytes, string Type)? photo = null;
        string label;
        if (id[0] == 'T' && await db.TextAttachments.AsNoTracking().Include(a => a.TextMessage).FirstOrDefaultAsync(a => a.Id == number, ct) is { IsImage: true } text)
        {
            label = $"{text.FileName}, texted{(text.TextMessage!.IsFromMe ? " by Me" : "")} on {text.TextMessage.SentAt.ToLocalTime().ToString("yyyy-MM-dd", Us)}";
            photo = text.PreviewKey is { } preview ? (await ReadAsync(preview, ct), "image/jpeg")
                : text.StorageKey is { } original && WebImageType(text.ContentType) is { } type ? (await ReadAsync(original, ct), type)
                : null;
        }
        else if (id[0] == 'R' && await db.ExpenseReceipts.AsNoTracking().Include(r => r.Expense).FirstOrDefaultAsync(r => r.Id == number, ct) is { IsImage: true } receipt)
        {
            label = $"receipt {receipt.FileName} for {receipt.Expense!.Description}, {Money(receipt.Expense.Amount)} on {receipt.Expense.Date.ToString("yyyy-MM-dd", Us)}";
            photo = receipt.PreviewKey is { } preview ? (await ReadAsync(preview, ct), "image/jpeg")
                : WebImageType(receipt.ContentType) is { } type ? (await ReadAsync(receipt.StorageKey, ct), type)
                : null;
        }
        else if (id[0] == 'E' && await db.EmailAttachments.AsNoTracking().Include(a => a.EmailMessage).FirstOrDefaultAsync(a => a.Id == number, ct) is { } email
            && email.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            label = $"{email.FileName}, emailed on {email.EmailMessage!.SentAt.ToLocalTime().ToString("yyyy-MM-dd", Us)} (subject \"{email.EmailMessage.Subject}\")";
            photo = await EmailPhotoAsync(email, ct);
        }
        else
        {
            return $"There's no photo {attachmentId}.";
        }

        if (photo is not { } p)
        {
            return $"{label}: this picture's format can't be sent to the model (it can be opened in the app).";
        }
        return $"{label}: {await PhotoDescriber(p.Bytes, p.Type, question, ct)}";
    }

    private async Task<byte[]> ReadAsync(string key, CancellationToken ct)
    {
        await using var stream = await storage!.OpenReadAsync(key, ct) ?? throw new FileNotFoundException("The photo file is missing.");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    /// <summary>An emailed picture, converted to a JPEG of at most 1600 pixels when the converter is available.</summary>
    private async Task<(byte[], string)?> EmailPhotoAsync(EmailAttachment email, CancellationToken ct)
    {
        var bytes = await ReadAsync(email.StorageKey, ct);
        if (images is not null)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"pm-photo-{Guid.NewGuid():N}{Path.GetExtension(email.FileName)}");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct);
                if (await images.ToJpegAsync(temp, TextMessageImportService.PreviewPixels, ct) is { } jpeg)
                {
                    return (jpeg, "image/jpeg");
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }
        return WebImageType(email.ContentType) is { } type ? (bytes, type) : null;
    }

    private static string? WebImageType(string contentType) =>
        contentType.ToLowerInvariant() is "image/jpeg" or "image/png" or "image/gif" or "image/webp" ? contentType.ToLowerInvariant() : null;

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
