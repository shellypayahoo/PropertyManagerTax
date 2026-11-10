using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Assistant;

public record ExpenseCheckSummary(int MessagesChecked, int Suggested, int VendorsFailed, string? Problem = null)
{
    public static ExpenseCheckSummary Skipped(string reason) => new(0, 0, 0, reason);
}

/// <summary>
/// Reads new texts and emails from vendors with the Ask page's AI model and turns charges in them (invoices, bills,
/// "you owe $180") into <see cref="ExpenseSuggestion"/>s for the owner to approve. It only suggests; nothing is added
/// to the books here. Each message is checked once.
/// </summary>
public class ExpenseFinder(
    IDbContextFactory<PropertyManagerDbContext> dbFactory, IOptionsMonitor<AssistantOptions> options, TimeProvider clock,
    IDocumentStorage? storage = null, Func<AssistantProviderOptions, IChatClient>? clientFactory = null)
{
    /// <summary>Messages older than this are never checked, so turning the checker on doesn't re-suggest past bills.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(30);

    private const int MaxMessagesPerCall = 40;
    private const int ContextMessages = 5;
    private const int MaxAttachmentText = 4000;
    private const int MaxPhotosPerCall = 3;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Func<AssistantProviderOptions, IChatClient> createClient = clientFactory ?? (p => ChatClientFactory.Create(p));

    /// <summary>The AI model used: the Ask page's default, when it's set up.</summary>
    public AssistantProviderOptions? Provider
    {
        get
        {
            var chosen = options.CurrentValue.Default();
            return chosen is { IsConfigured: true } ? chosen : options.CurrentValue.Choices().FirstOrDefault(p => p.IsConfigured);
        }
    }

    public async Task<ExpenseCheckSummary> RunAsync(CancellationToken ct = default)
    {
        if (Provider is not { } provider)
        {
            return ExpenseCheckSummary.Skipped("No AI model is set up for the Ask page.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Anything older than the look-back is treated as already checked.
        var cutoff = clock.GetUtcNow() - LookBack;
        await db.TextMessages.Where(m => !m.ExpenseChecked && m.SentAt < cutoff).ExecuteUpdateAsync(u => u.SetProperty(m => m.ExpenseChecked, true), ct);
        await db.EmailMessages.Where(m => !m.ExpenseChecked && m.SentAt < cutoff).ExecuteUpdateAsync(u => u.SetProperty(m => m.ExpenseChecked, true), ct);

        var texts = await db.TextMessages.AsNoTracking().Include(m => m.Attachments)
            .Where(m => !m.ExpenseChecked && m.VendorId != null)
            .ToListAsync(ct);
        var emails = await db.EmailMessages.AsNoTracking().Include(m => m.Attachments)
            .Where(m => !m.ExpenseChecked && m.VendorId != null)
            .ToListAsync(ct);
        var vendorIds = texts.Select(m => m.VendorId!.Value).Concat(emails.Select(m => m.VendorId!.Value)).Distinct().ToList();
        if (vendorIds.Count == 0)
        {
            return new ExpenseCheckSummary(0, 0, 0);
        }

        var vendors = await db.Vendors.AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var properties = await db.Properties.AsNoTracking().Include(p => p.Units).OrderBy(p => p.Name).ToListAsync(ct);
        using var client = createClient(provider);

        int checkedCount = 0, suggested = 0, failed = 0;
        string? problem = null;
        foreach (var vendorId in vendorIds)
        {
            var batch = texts.Where(m => m.VendorId == vendorId).Select(Item.From)
                .Concat(emails.Where(m => m.VendorId == vendorId).Select(Item.From))
                .OrderBy(m => m.SentAt)
                .Take(MaxMessagesPerCall)
                .ToList();
            try
            {
                var found = await CheckVendorAsync(client, vendors[vendorId], batch, properties, ct);
                suggested += await SaveAsync(vendors[vendorId], batch, found, ct);
                await MarkCheckedAsync(batch, ct);
                checkedCount += batch.Count;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Left unchecked, so the next run tries this vendor again.
                failed++;
                problem = $"Couldn't check messages from {vendors[vendorId].Name}: {ex.Message}";
            }
        }
        return new ExpenseCheckSummary(checkedCount, suggested, failed, problem);
    }

    /// <summary>A text or email, with its attachments, as the model sees it.</summary>
    private sealed record Item(int? TextId, int? EmailId, DateTimeOffset SentAt, bool IsFromMe, string Kind, string Text, IReadOnlyList<ItemFile> Files)
    {
        public static Item From(TextMessage m) => new(m.Id, null, m.SentAt, m.IsFromMe, "text", m.Text,
            m.Attachments.Where(a => a.Status == TextAttachmentStatus.Saved && !a.IsVideo)
                .Select(a => new ItemFile(a.Id, null, a.FileName, a.Text,
                    a.PreviewKey ?? (a.IsImage ? a.StorageKey : null), a.PreviewKey is not null ? "image/jpeg" : a.ContentType)).ToList());

        public static Item From(EmailMessage m) => new(null, m.Id, m.SentAt, m.IsFromMe, "email", $"Subject: {m.Subject}\n{m.Text}",
            m.Attachments.Select(a => new ItemFile(null, a.Id, a.FileName, a.Text,
                a.ContentType is "image/jpeg" or "image/png" ? a.StorageKey : null, a.ContentType)).ToList());
    }

    /// <param name="PhotoKey">A picture the model can look at (iPhone photos as their JPEG copy), or null.</param>
    private sealed record ItemFile(int? TextAttachmentId, int? EmailAttachmentId, string FileName, string? Text, string? PhotoKey, string PhotoType);

    /// <summary>One charge as the model reports it; all optional, checked before use.</summary>
    internal sealed record Finding(
        int? Message, string? Date, decimal? Amount, string? Description, string? Category, string? Property, string? Unit,
        string? PaidWith, string? Evidence, int? ReceiptFile);

    private sealed record FindingList(List<Finding>? Expenses);

    private async Task<List<Finding>> CheckVendorAsync(IChatClient client, Vendor vendor, List<Item> batch, List<Property> properties, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var earliest = batch[0].SentAt;
        // A few earlier messages for context (e.g. the quote an invoice refers to).
        var context = (await db.TextMessages.AsNoTracking().Where(m => m.VendorId == vendor.Id && m.SentAt < earliest).OrderByDescending(m => m.SentAt).Take(ContextMessages).ToListAsync(ct))
            .Select(m => (m.SentAt, m.IsFromMe, Kind: "text", m.Text))
            .Concat((await db.EmailMessages.AsNoTracking().Where(m => m.VendorId == vendor.Id && m.SentAt < earliest).OrderByDescending(m => m.SentAt).Take(ContextMessages).ToListAsync(ct))
                .Select(m => (m.SentAt, m.IsFromMe, Kind: "email", Text: $"Subject: {m.Subject}\n{m.Text}")))
            .OrderByDescending(m => m.SentAt).Take(ContextMessages).OrderBy(m => m.SentAt)
            .ToList();

        var text = new StringBuilder();
        text.AppendLine($"Vendor: {vendor.Name}{(string.IsNullOrWhiteSpace(vendor.Trade) ? "" : $" ({vendor.Trade})")}");
        if (context.Count > 0)
        {
            text.AppendLine("\nEarlier messages, for context only (already checked; don't report charges from these):");
            foreach (var m in context)
            {
                text.AppendLine($"- {Local(m.SentAt)}, {m.Kind} {(m.IsFromMe ? "from the owner" : "from the vendor")}: {Clip(m.Text, 600)}");
            }
        }
        text.AppendLine("\nNew messages to check:");
        var files = new List<ItemFile>();
        var photos = new List<AIContent>();
        for (var i = 0; i < batch.Count; i++)
        {
            var m = batch[i];
            text.AppendLine($"[message {i + 1}] {Local(m.SentAt)}, {m.Kind} {(m.IsFromMe ? "from the owner" : "from the vendor")}: {Clip(m.Text, 3000)}");
            foreach (var f in m.Files)
            {
                files.Add(f);
                text.Append($"  [file {files.Count}] {f.FileName}");
                if (!string.IsNullOrWhiteSpace(f.Text))
                {
                    text.AppendLine($", its text: {Clip(f.Text, MaxAttachmentText)}");
                }
                else if (f.PhotoKey is not null && options.CurrentValue.AllowPhotos && storage is not null && photos.Count < MaxPhotosPerCall
                    && await ReadAsync(f.PhotoKey, ct) is { } bytes)
                {
                    text.AppendLine(" (photo attached below)");
                    photos.Add(new TextContent($"[file {files.Count}] {f.FileName}:"));
                    photos.Add(new DataContent(bytes, f.PhotoType));
                }
                else
                {
                    text.AppendLine(" (no readable text)");
                }
            }
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(properties)),
            new(ChatRole.User, [new TextContent(text.ToString()), .. photos]),
        ];
        var response = await client.GetResponseAsync(messages, cancellationToken: ct);
        return Parse(response.Text);
    }

    private string Instructions(List<Property> properties)
    {
        var places = string.Join("; ", properties.Select(p => $"{p.Name} (units {string.Join(", ", p.Units.OrderBy(u => u.Label).Select(u => u.Label))})"));
        var categories = string.Join(", ", Enum.GetValues<ExpenseCategory>().Select(c => c.DisplayName()));
        return $$"""
            You help the owner of rental properties keep their books. You read messages between the owner and one of their
            vendors and find charges the owner should record as an expense: an invoice or bill, a receipt, "the total is
            $180", "you owe me $250 for parts", or the owner confirming they paid the vendor ("sent you $180 on Zelle").
            Don't report: quotes or estimates that weren't accepted, money paid to the owner, deposits the owner gets back,
            scheduling, or messages without an amount. Report each charge once, even if several messages mention it: use
            the final total, and the message that states it. Today is {{clock.GetLocalNow():yyyy-MM-dd}}.

            The owner's properties: {{places}}. "All properties" is for costs of the whole business.
            Expense categories: {{categories}}.

            Answer with JSON only, no other text, in this shape:
            {"expenses": [{"message": 3, "date": "2026-10-20", "amount": 180.50, "description": "Kitchen faucet replacement",
              "category": "Repairs", "property": "1354 Taylor St NW", "unit": "B", "paidWith": "Zelle",
              "evidence": "Total is $180.50 for the faucet", "receiptFile": 2}]}
            - message: the number of the new message the charge is in.
            - date: the date of the charge or invoice (yyyy-MM-dd), else the message's date.
            - amount: a number in dollars, no "$".
            - property and unit: only when the messages say or clearly imply them; otherwise null. Use "All properties" for business-wide costs.
            - paidWith: Zelle, BankTransfer, Check, Cash, Card, Other, or null if not said.
            - evidence: the exact words from the message, under 200 characters.
            - receiptFile: the number of an attached invoice or receipt file for this charge, or null.
            If there are no charges, answer {"expenses": []}.
            """;
    }

    internal static List<Finding> Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new JsonException("The AI model didn't answer with a list of expenses.");
        }
        return JsonSerializer.Deserialize<FindingList>(text[start..(end + 1)], Json)?.Expenses ?? [];
    }

    private async Task<int> SaveAsync(Vendor vendor, List<Item> batch, List<Finding> findings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var files = batch.SelectMany(m => m.Files).ToList();
        var saved = 0;
        foreach (var f in findings)
        {
            if (f.Amount is not { } amount || amount <= 0 || f.Message is not { } number || number < 1 || number > batch.Count)
            {
                continue;
            }
            var message = batch[number - 1];
            var date = DateOnly.TryParse(f.Date, CultureInfo.InvariantCulture, out var d) ? d : DateOnly.FromDateTime(message.SentAt.ToLocalTime().DateTime);
            amount = Math.Round(amount, 2);

            // Already suggested (e.g. a re-run after an error)? Then don't suggest it again.
            var near = (await db.ExpenseSuggestions.AsNoTracking().Where(s => s.VendorId == vendor.Id).ToListAsync(ct))
                .Any(s => s.Amount == amount && Math.Abs(s.Date.DayNumber - date.DayNumber) <= 3);
            if (near)
            {
                continue;
            }

            var suggestion = new ExpenseSuggestion
            {
                Status = SuggestionStatus.Pending,
                CreatedAt = clock.GetUtcNow(),
                TextMessageId = message.TextId,
                EmailMessageId = message.EmailId,
                VendorId = vendor.Id,
                Date = date,
                Amount = amount,
                Description = Clip(string.IsNullOrWhiteSpace(f.Description) ? $"{vendor.Name} invoice" : f.Description.Trim(), 500),
                Category = (f.Category is null ? null : AssistantActions.ResolveCategory(f.Category)) ?? ExpenseCategory.Repairs,
                PaidWith = f.PaidWith is null ? null : AssistantActions.ResolveMethod(f.PaidWith),
                Evidence = Clip(string.IsNullOrWhiteSpace(f.Evidence) ? message.Text : f.Evidence.Trim(), 1000),
            };
            if (!string.IsNullOrWhiteSpace(f.Property))
            {
                var (place, error) = await AssistantActions.ResolvePlaceAsync(db, f.Property, f.Unit, ct);
                if (error is null)
                {
                    (suggestion.PropertyId, suggestion.UnitId, suggestion.IsShared) = (place.PropertyId, place.UnitId, place.PropertyId is null);
                }
                else if (f.Unit is not null && (await AssistantActions.ResolvePlaceAsync(db, f.Property, null, ct)) is { Error: null } withoutUnit)
                {
                    suggestion.PropertyId = withoutUnit.Place.PropertyId;
                }
            }
            if (f.ReceiptFile is { } fileNumber && fileNumber >= 1 && fileNumber <= files.Count)
            {
                (suggestion.TextAttachmentId, suggestion.EmailAttachmentId) = (files[fileNumber - 1].TextAttachmentId, files[fileNumber - 1].EmailAttachmentId);
            }
            suggestion.DuplicateOfExpenseId = await FindDuplicateAsync(db, vendor.Id, amount, date, ct);
            db.ExpenseSuggestions.Add(suggestion);
            saved++;
        }
        await db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>
    /// An expense that's probably the same charge: same amount, and either the same vendor within two weeks or no vendor
    /// within five days (e.g. a Zelle payment imported from the bank).
    /// </summary>
    internal static async Task<int?> FindDuplicateAsync(PropertyManagerDbContext db, int vendorId, decimal amount, DateOnly date, CancellationToken ct)
    {
        var (from, to) = (date.AddDays(-14), date.AddDays(14));
        var candidates = (await db.Expenses.AsNoTracking().Where(e => e.Date >= from && e.Date <= to).ToListAsync(ct))
            .Where(e => e.Amount == amount)
            .Where(e => e.VendorId == vendorId || (e.VendorId is null && Math.Abs(e.Date.DayNumber - date.DayNumber) <= 5))
            .OrderBy(e => Math.Abs(e.Date.DayNumber - date.DayNumber))
            .ToList();
        return candidates.FirstOrDefault()?.Id;
    }

    private async Task MarkCheckedAsync(List<Item> batch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var textIds = batch.Select(m => m.TextId).OfType<int>().ToList();
        var emailIds = batch.Select(m => m.EmailId).OfType<int>().ToList();
        await db.TextMessages.Where(m => textIds.Contains(m.Id)).ExecuteUpdateAsync(u => u.SetProperty(m => m.ExpenseChecked, true), ct);
        await db.EmailMessages.Where(m => emailIds.Contains(m.Id)).ExecuteUpdateAsync(u => u.SetProperty(m => m.ExpenseChecked, true), ct);
    }

    private async Task<byte[]?> ReadAsync(string key, CancellationToken ct)
    {
        await using var stream = await storage!.OpenReadAsync(key, ct);
        if (stream is null)
        {
            return null;
        }
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd ddd h:mm tt", Us);

    private static string Clip(string text, int length) => text.Length > length ? text[..length] + "…" : text;
}
