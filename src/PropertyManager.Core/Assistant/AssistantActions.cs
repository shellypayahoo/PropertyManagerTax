using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using PropertyManager.Core.Calendar;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Core.Assistant;

public enum ProposalState { Pending, Applied, Dismissed, Failed, Expired }

/// <summary>
/// A change the assistant proposed. Nothing happens until the owner clicks Apply on the Ask page, which runs it.
/// </summary>
public class ProposedChange
{
    public int Number { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Details { get; init; } = [];
    public ProposalState State { get; internal set; }

    /// <summary>What happened when it was applied, or why it failed.</summary>
    public string? Result { get; internal set; }

    internal Func<CancellationToken, Task<string>> Apply { get; init; } = _ => Task.FromResult("");

    /// <summary>
    /// A proposal read back from saved history. One still pending can't be applied any more (the app restarted
    /// since it was made), so it's marked expired.
    /// </summary>
    public static ProposedChange Restored(int number, string summary, IReadOnlyList<string> details, ProposalState state, string? result) => new()
    {
        Number = number,
        Summary = summary,
        Details = details,
        State = state == ProposalState.Pending ? ProposalState.Expired : state,
        Result = state == ProposalState.Pending ? "This proposal is from before the app restarted; ask again to make it." : result,
    };
}

/// <summary>
/// The assistant's ways of changing data: each tool checks its inputs and adds a <see cref="ProposedChange"/> to the
/// conversation for the owner to approve. Also reads files attached to the conversation.
/// </summary>
public partial class AssistantActions(
    IDbContextFactory<PropertyManagerDbContext> dbFactory, ReceiptService receipts, LeaseDocumentService leaseDocuments, TimeProvider? time = null)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The conversation being answered; set by <see cref="AssistantService"/> for each question.</summary>
    public AssistantConversation? Conversation { get; set; }

    private DateOnly Today => DateOnly.FromDateTime((time ?? TimeProvider.System).GetLocalNow().DateTime);

    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(ReadUploadedFileAsync, "read_uploaded_file"),
        AIFunctionFactory.Create(ProposeAddExpenseAsync, "propose_add_expense"),
        AIFunctionFactory.Create(ProposeUpdateExpenseAsync, "propose_update_expense"),
        AIFunctionFactory.Create(ProposeRecordRentPaymentAsync, "propose_record_rent_payment"),
        AIFunctionFactory.Create(ProposeAddMaintenanceRequestAsync, "propose_add_maintenance_request"),
        AIFunctionFactory.Create(ProposeUpdateMaintenanceRequestAsync, "propose_update_maintenance_request"),
        AIFunctionFactory.Create(ProposeAddVendorAsync, "propose_add_vendor"),
        AIFunctionFactory.Create(ProposeUpdateTenantAsync, "propose_update_tenant"),
        AIFunctionFactory.Create(ProposeAttachFileAsync, "propose_attach_file"),
    ];

    [Description("Reads a file the owner attached in this conversation (PDF, Word, CSV, or text), 20,000 characters at a time.")]
    public Task<string> ReadUploadedFileAsync(
        [Description("The attached file's name.")] string fileName,
        [Description("Character position to start from (default 0).")] int fromCharacter = 0)
    {
        if (FindFile(fileName) is not { } file)
        {
            return Task.FromResult(NoSuchFile(fileName));
        }
        if (file.IsImage)
        {
            return Task.FromResult($"{file.Name} is a photo; it was shown with the question it was attached to.");
        }
        if (string.IsNullOrWhiteSpace(file.Text))
        {
            return Task.FromResult($"{file.Name} has no readable text.");
        }
        var start = Math.Clamp(fromCharacter, 0, file.Text.Length);
        var end = Math.Min(start + 20_000, file.Text.Length);
        return Task.FromResult($"{file.Name}, characters {start}-{end} of {file.Text.Length}:\n{file.Text[start..end]}"
            + (end < file.Text.Length ? $"\n(continues; read from {end})" : "\n(end of file)"));
    }

    [Description("Proposes adding an expense. The owner must click Apply before it's recorded. Use for bills, receipts, and invoices; attach an uploaded receipt with attachFileName.")]
    public async Task<string> ProposeAddExpenseAsync(
        [Description("Date paid, yyyy-MM-dd.")] string date,
        [Description("Property name or street (e.g. \"Taylor\"), or \"shared\" for costs of the whole business.")] string property,
        [Description("Amount in dollars, e.g. 180.50.")] decimal amount,
        [Description("Short description, e.g. \"Kitchen faucet\".")] string description,
        [Description("Schedule E category, e.g. Repairs, Supplies, Utilities, Cleaning and maintenance, Insurance, Taxes, Mortgage interest, Legal and other professional fees, Advertising, Other.")] string category,
        [Description("Optional unit label, e.g. \"B\".")] string? unit = null,
        [Description("Optional vendor name; a new vendor is added if it doesn't exist.")] string? vendor = null,
        [Description("Optional: how it was paid (Zelle, BankTransfer, Check, Cash, Card, Other).")] string? paidWith = null,
        [Description("Optional notes.")] string? notes = null,
        [Description("Optional: name of an uploaded file to attach as the receipt.")] string? attachFileName = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (ParseDate(date) is not { } day) return $"\"{date}\" isn't a date; use yyyy-MM-dd.";
        if (amount <= 0) return "The amount must be more than zero.";
        if (string.IsNullOrWhiteSpace(description)) return "Give a short description.";
        var (place, placeError) = await ResolvePlaceAsync(db, property, unit, ct);
        if (placeError is not null) return placeError;
        if (ResolveCategory(category) is not { } cat) return UnknownCategory(category);
        PaymentMethod? method = null;
        if (!string.IsNullOrWhiteSpace(paidWith))
        {
            if (ResolveMethod(paidWith) is not { } m) return $"\"{paidWith}\" isn't a payment method; use {string.Join(", ", Enum.GetNames<PaymentMethod>())}.";
            method = m;
        }
        var (vendorId, vendorName, newVendor) = await ResolveVendorAsync(db, vendor, ct);
        UploadedFile? file = null;
        if (!string.IsNullOrWhiteSpace(attachFileName) && (file = FindFile(attachFileName)) is null) return NoSuchFile(attachFileName);

        var details = new List<string>
        {
            $"Date: {day:MMM d, yyyy}",
            $"Where: {place.Label}",
            $"Category: {cat.DisplayName()}",
            $"Amount: {Money(amount)}",
            $"Description: {description.Trim()}",
        };
        if (vendorName is not null) details.Add($"Vendor: {vendorName}{(newVendor ? " (new vendor)" : "")}");
        if (method is not null) details.Add($"Paid with: {method}");
        if (!string.IsNullOrWhiteSpace(notes)) details.Add($"Notes: {notes.Trim()}");
        if (file is not null) details.Add($"Receipt: {file.Name}");

        return Propose($"Add expense: {Money(amount)} {cat.DisplayName()}, {description.Trim()} ({place.Label}, {day:MMM d, yyyy})", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            var expense = new Expense
            {
                Date = day, PropertyId = place.PropertyId, UnitId = place.UnitId, Category = cat, Amount = amount,
                Description = description.Trim(), PaidWith = method, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                VendorId = vendorId ?? (newVendor ? (await AddVendorAsync(write, vendorName!, token)).Id : null),
            };
            write.Expenses.Add(expense);
            await write.SaveChangesAsync(token);
            var attached = file is null ? "" : await AttachAsync(file, expense.Id, null, token);
            return $"Added expense #{expense.Id}.{attached}";
        });
    }

    [Description("Proposes changing an existing expense (find its number with search_expenses). Only the fields given change. The owner must click Apply.")]
    public async Task<string> ProposeUpdateExpenseAsync(
        [Description("The expense number, e.g. 42 for Expense #42.")] int expenseId,
        [Description("New date, yyyy-MM-dd.")] string? date = null,
        [Description("New property name, or \"shared\".")] string? property = null,
        [Description("New unit label (with property).")] string? unit = null,
        [Description("New category.")] string? category = null,
        [Description("New amount.")] decimal? amount = null,
        [Description("New description.")] string? description = null,
        [Description("New vendor name.")] string? vendor = null,
        [Description("New payment method.")] string? paidWith = null,
        [Description("New notes.")] string? notes = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expense = await db.Expenses.AsNoTracking().Include(e => e.Property).Include(e => e.Unit).Include(e => e.Vendor).FirstOrDefaultAsync(e => e.Id == expenseId, ct);
        if (expense is null) return $"There's no expense #{expenseId}.";

        var details = new List<string>();
        DateOnly? newDate = null;
        if (date is not null)
        {
            if (ParseDate(date) is not { } d) return $"\"{date}\" isn't a date; use yyyy-MM-dd.";
            newDate = d;
            details.Add($"Date: {expense.Date:MMM d, yyyy} → {d:MMM d, yyyy}");
        }
        Place? newPlace = null;
        if (property is not null || unit is not null)
        {
            var (place, error) = await ResolvePlaceAsync(db, property ?? expense.Property?.Name ?? "shared", unit, ct);
            if (error is not null) return error;
            newPlace = place;
            details.Add($"Where: {PlaceLabel(expense)} → {place.Label}");
        }
        ExpenseCategory? newCategory = null;
        if (category is not null)
        {
            if (ResolveCategory(category) is not { } c) return UnknownCategory(category);
            newCategory = c;
            details.Add($"Category: {expense.Category.DisplayName()} → {c.DisplayName()}");
        }
        if (amount is { } a)
        {
            if (a <= 0) return "The amount must be more than zero.";
            details.Add($"Amount: {Money(expense.Amount)} → {Money(a)}");
        }
        if (description is not null) details.Add($"Description: {expense.Description} → {description.Trim()}");
        PaymentMethod? newMethod = null;
        if (paidWith is not null)
        {
            if (ResolveMethod(paidWith) is not { } m) return $"\"{paidWith}\" isn't a payment method; use {string.Join(", ", Enum.GetNames<PaymentMethod>())}.";
            newMethod = m;
            details.Add($"Paid with: {expense.PaidWith?.ToString() ?? "—"} → {m}");
        }
        var (vendorId, vendorName, newVendor) = await ResolveVendorAsync(db, vendor, ct);
        if (vendorName is not null) details.Add($"Vendor: {expense.Vendor?.Name ?? "—"} → {vendorName}{(newVendor ? " (new vendor)" : "")}");
        if (notes is not null) details.Add($"Notes: {notes.Trim()}");
        if (details.Count == 0) return "Say what to change.";

        return Propose($"Change expense #{expense.Id} ({expense.Description}, {Money(expense.Amount)}, {expense.Date:MMM d, yyyy})", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            var e = await write.Expenses.FindAsync([expenseId], token) ?? throw new InvalidOperationException($"Expense #{expenseId} no longer exists.");
            if (newDate is { } d) e.Date = d;
            if (newPlace is { } p) (e.PropertyId, e.UnitId) = (p.PropertyId, p.UnitId);
            if (newCategory is { } c) e.Category = c;
            if (amount is { } amt) e.Amount = amt;
            if (description is not null) e.Description = description.Trim();
            if (newMethod is { } m) e.PaidWith = m;
            if (notes is not null) e.Notes = notes.Trim();
            if (vendorName is not null) e.VendorId = vendorId ?? (await AddVendorAsync(write, vendorName, token)).Id;
            await write.SaveChangesAsync(token);
            return $"Updated expense #{e.Id}.";
        });
    }

    [Description("Proposes recording a rent payment on a tenant's lease. The owner must click Apply.")]
    public async Task<string> ProposeRecordRentPaymentAsync(
        [Description("Tenant name, or the unit, e.g. \"Taylor B\".")] string tenantOrUnit,
        [Description("Amount paid.")] decimal amount,
        [Description("Date received, yyyy-MM-dd.")] string date,
        [Description("Month the rent is for, yyyy-MM (defaults to the month received).")] string? forMonth = null,
        [Description("How it was paid: Zelle, BankTransfer, Check, Cash, Card, Other (default Zelle).")] string? method = null,
        [Description("Optional notes.")] string? notes = null,
        CancellationToken ct = default)
    {
        if (ParseDate(date) is not { } received) return $"\"{date}\" isn't a date; use yyyy-MM-dd.";
        if (amount <= 0) return "The amount must be more than zero.";
        DateOnly month;
        if (forMonth is null)
        {
            month = RentPayment.MonthOf(received);
        }
        else if (DateOnly.TryParseExact(forMonth, "yyyy-MM", Us, DateTimeStyles.None, out var m))
        {
            month = m;
        }
        else if (ParseDate(forMonth) is { } fm)
        {
            month = RentPayment.MonthOf(fm);
        }
        else
        {
            return $"\"{forMonth}\" isn't a month; use yyyy-MM.";
        }
        var payMethod = PaymentMethod.Zelle;
        if (method is not null)
        {
            if (ResolveMethod(method) is not { } pm) return $"\"{method}\" isn't a payment method; use {string.Join(", ", Enum.GetNames<PaymentMethod>())}.";
            payMethod = pm;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var leases = await db.Leases.AsNoTracking()
            .Include(l => l.Tenants).Include(l => l.Payments).Include(l => l.Unit).ThenInclude(u => u!.Property)
            .ToListAsync(ct);
        var unitCounts = await UnitCountsAsync(db, ct);
        var matches = leases
            .Where(l => RentSchedule.IsDue(l, month))
            .Where(l => l.Tenants.Any(t => Contains(t.FullName, tenantOrUnit) || Contains(tenantOrUnit, t.FullName) || Contains(tenantOrUnit, t.LastName))
                || (l.Unit is { } u && MentionsUnit(tenantOrUnit, u, unitCounts)))
            .ToList();
        if (matches.Count != 1)
        {
            var due = leases.Where(l => RentSchedule.IsDue(l, month)).Select(l => $"{l.Unit?.DisplayName} ({l.TenantNames})");
            return matches.Count == 0
                ? $"No lease with rent due for {month:MMMM yyyy} matches \"{tenantOrUnit}\". Leases due then: {string.Join("; ", due)}."
                : $"\"{tenantOrUnit}\" matches more than one lease: {string.Join("; ", matches.Select(l => $"{l.Unit?.DisplayName} ({l.TenantNames})"))}. Be more specific.";
        }
        var lease = matches[0];
        var warning = RentSchedule.CheckNewPayment(lease, month, received, amount, Today);
        var details = new List<string>
        {
            $"Lease: {lease.Unit?.DisplayName} ({lease.TenantNames})",
            $"Amount: {Money(amount)} received {received:MMM d, yyyy} by {payMethod}",
            $"For: {month:MMMM yyyy} (rent {Money(lease.MonthlyRent)})",
        };
        if (!string.IsNullOrWhiteSpace(notes)) details.Add($"Notes: {notes.Trim()}");
        if (warning is not null) details.Add($"⚠ {warning}");

        return Propose($"Record rent: {Money(amount)} from {lease.TenantNames} for {month:MMMM yyyy}", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            write.RentPayments.Add(new RentPayment { LeaseId = lease.Id, Date = received, ForMonth = month, Amount = amount, Method = payMethod, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() });
            await write.SaveChangesAsync(token);
            return "Recorded the payment.";
        });
    }

    [Description("Proposes adding a maintenance request. The owner must click Apply.")]
    public async Task<string> ProposeAddMaintenanceRequestAsync(
        [Description("The unit, e.g. \"Taylor B\" or \"2228 1st St unit A\".")] string unit,
        [Description("Short title of the issue, e.g. \"Kitchen sink leak\".")] string title,
        [Description("Low, Normal, High, or Emergency (default Normal).")] string? priority = null,
        [Description("Optional details.")] string? description = null,
        [Description("Optional vendor name.")] string? vendor = null,
        [Description("Date reported, yyyy-MM-dd (default today).")] string? reportedOn = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (target, error) = await ResolveUnitAsync(db, unit, ct);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(title)) return "Give the issue a short title.";
        var level = MaintenancePriority.Normal;
        if (priority is not null && !Enum.TryParse(priority, true, out level)) return $"\"{priority}\" isn't a priority; use Low, Normal, High, or Emergency.";
        var reported = Today;
        if (reportedOn is not null)
        {
            if (ParseDate(reportedOn) is not { } r) return $"\"{reportedOn}\" isn't a date; use yyyy-MM-dd.";
            reported = r;
        }
        var (vendorId, vendorName, newVendor) = await ResolveVendorAsync(db, vendor, ct);

        var details = new List<string> { $"Unit: {target!.DisplayName}", $"Issue: {title.Trim()}", $"Priority: {level}", $"Reported: {reported:MMM d, yyyy}" };
        if (!string.IsNullOrWhiteSpace(description)) details.Add($"Details: {description.Trim()}");
        if (vendorName is not null) details.Add($"Vendor: {vendorName}{(newVendor ? " (new vendor)" : "")}");

        return Propose($"New maintenance request: {title.Trim()} ({target.DisplayName}, {level})", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            var request = new MaintenanceRequest
            {
                UnitId = target.Id, Title = title.Trim(), Priority = level, ReportedOn = reported,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                VendorId = vendorId ?? (newVendor ? (await AddVendorAsync(write, vendorName!, token)).Id : null),
            };
            write.MaintenanceRequests.Add(request);
            await write.SaveChangesAsync(token);
            return $"Added request #{request.Id}.";
        });
    }

    [Description("Proposes changing a maintenance request (find its number with list_maintenance_requests): status, priority, dates, vendor, title, or details. The owner must click Apply.")]
    public async Task<string> ProposeUpdateMaintenanceRequestAsync(
        [Description("The request number, e.g. 7 for Request #7.")] int requestId,
        [Description("Open, Scheduled, InProgress, Completed, or Cancelled.")] string? status = null,
        [Description("Low, Normal, High, or Emergency.")] string? priority = null,
        [Description("Scheduled date, yyyy-MM-dd.")] string? scheduledFor = null,
        [Description("Completed date, yyyy-MM-dd (status becomes Completed).")] string? completedOn = null,
        [Description("Vendor name.")] string? vendor = null,
        [Description("New title.")] string? title = null,
        [Description("New details.")] string? description = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.MaintenanceRequests.AsNoTracking().Include(m => m.Unit).ThenInclude(u => u!.Property).Include(m => m.Vendor)
            .FirstOrDefaultAsync(m => m.Id == requestId, ct);
        if (request is null) return $"There's no maintenance request #{requestId}.";

        var details = new List<string>();
        MaintenanceStatus? newStatus = null;
        if (status is not null)
        {
            if (!Enum.TryParse<MaintenanceStatus>(status.Replace(" ", ""), true, out var s)) return $"\"{status}\" isn't a status; use Open, Scheduled, InProgress, Completed, or Cancelled.";
            newStatus = s;
        }
        MaintenancePriority? newPriority = null;
        if (priority is not null)
        {
            if (!Enum.TryParse<MaintenancePriority>(priority, true, out var p)) return $"\"{priority}\" isn't a priority; use Low, Normal, High, or Emergency.";
            newPriority = p;
            details.Add($"Priority: {request.Priority} → {p}");
        }
        DateOnly? scheduled = null, completed = null;
        if (scheduledFor is not null)
        {
            if (ParseDate(scheduledFor) is not { } d) return $"\"{scheduledFor}\" isn't a date; use yyyy-MM-dd.";
            scheduled = d;
            details.Add($"Scheduled for: {d:MMM d, yyyy}");
        }
        if (completedOn is not null)
        {
            if (ParseDate(completedOn) is not { } d) return $"\"{completedOn}\" isn't a date; use yyyy-MM-dd.";
            completed = d;
            newStatus = MaintenanceStatus.Completed;
            details.Add($"Completed on: {d:MMM d, yyyy}");
        }
        if (newStatus is { } ns) details.Insert(0, $"Status: {request.Status} → {ns}");
        var (vendorId, vendorName, newVendor) = await ResolveVendorAsync(db, vendor, ct);
        if (vendorName is not null) details.Add($"Vendor: {request.Vendor?.Name ?? "—"} → {vendorName}{(newVendor ? " (new vendor)" : "")}");
        if (title is not null) details.Add($"Title: {request.Title} → {title.Trim()}");
        if (description is not null) details.Add($"Details: {description.Trim()}");
        if (details.Count == 0) return "Say what to change.";

        return Propose($"Update maintenance request #{request.Id}: {request.Title} ({request.Unit?.DisplayName})", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            var m = await write.MaintenanceRequests.FindAsync([requestId], token) ?? throw new InvalidOperationException($"Request #{requestId} no longer exists.");
            if (newStatus is { } st)
            {
                m.Status = st;
                m.CompletedOn = st == MaintenanceStatus.Completed ? completed ?? m.CompletedOn ?? Today : null;
            }
            if (newPriority is { } p) m.Priority = p;
            if (scheduled is { } sd) m.ScheduledFor = sd;
            if (title is not null) m.Title = title.Trim();
            if (description is not null) m.Description = description.Trim();
            if (vendorName is not null) m.VendorId = vendorId ?? (await AddVendorAsync(write, vendorName, token)).Id;
            await write.SaveChangesAsync(token);
            return $"Updated request #{m.Id}.";
        });
    }

    [Description("Proposes adding a vendor (plumber, HVAC company, cleaner…). The owner must click Apply.")]
    public async Task<string> ProposeAddVendorAsync(
        [Description("Vendor name.")] string name,
        [Description("Trade, e.g. Plumber.")] string? trade = null,
        [Description("Phone.")] string? phone = null,
        [Description("Email.")] string? email = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the vendor's name.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = (await db.Vendors.AsNoTracking().ToListAsync(ct)).FirstOrDefault(v => string.Equals(v.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return $"{existing.Name} is already a vendor.";
        var details = new List<string> { $"Name: {name.Trim()}" };
        if (!string.IsNullOrWhiteSpace(trade)) details.Add($"Trade: {trade.Trim()}");
        if (!string.IsNullOrWhiteSpace(phone)) details.Add($"Phone: {phone.Trim()}");
        if (!string.IsNullOrWhiteSpace(email)) details.Add($"Email: {email.Trim()}");
        return Propose($"Add vendor: {name.Trim()}", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            var v = await AddVendorAsync(write, name.Trim(), token, trade, phone, email);
            return $"Added vendor {v.Name}.";
        });
    }

    [Description("Proposes updating a tenant's phone, email, or notes. The owner must click Apply.")]
    public async Task<string> ProposeUpdateTenantAsync(
        [Description("The tenant's name.")] string tenant,
        [Description("New phone.")] string? phone = null,
        [Description("New email.")] string? email = null,
        [Description("New notes (replaces the old notes).")] string? notes = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var matches = (await db.Tenants.AsNoTracking().ToListAsync(ct)).Where(t => Contains(t.FullName, tenant) || Contains(tenant, t.FullName)).ToList();
        if (matches.Count != 1) return matches.Count == 0 ? $"No tenant matches \"{tenant}\"." : $"\"{tenant}\" matches {string.Join(", ", matches.Select(t => t.FullName))}. Be more specific.";
        var t = matches[0];
        var details = new List<string>();
        if (phone is not null) details.Add($"Phone: {t.Phone ?? "—"} → {phone.Trim()}");
        if (email is not null) details.Add($"Email: {t.Email ?? "—"} → {email.Trim()}");
        if (notes is not null) details.Add($"Notes: {notes.Trim()}");
        if (details.Count == 0) return "Say what to change.";
        return Propose($"Update tenant {t.FullName}", details, async token =>
        {
            await using var write = await dbFactory.CreateDbContextAsync(token);
            var row = await write.Tenants.FindAsync([t.Id], token) ?? throw new InvalidOperationException($"{t.FullName} no longer exists.");
            if (phone is not null) row.Phone = phone.Trim();
            if (email is not null) row.Email = email.Trim();
            if (notes is not null) row.Notes = notes.Trim();
            await write.SaveChangesAsync(token);
            return $"Updated {row.FullName}.";
        });
    }

    [Description("Proposes saving a file the owner attached in this conversation: as a receipt on an expense (expenseId) or as a document on a lease (leaseId). The owner must click Apply.")]
    public async Task<string> ProposeAttachFileAsync(
        [Description("The attached file's name.")] string fileName,
        [Description("Expense number to attach it to as a receipt.")] int? expenseId = null,
        [Description("Lease number to attach it to as a lease document.")] int? leaseId = null,
        CancellationToken ct = default)
    {
        if (FindFile(fileName) is not { } file) return NoSuchFile(fileName);
        if ((expenseId is null) == (leaseId is null)) return "Give either an expense number or a lease number.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        string target;
        if (expenseId is { } eid)
        {
            var e = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == eid, ct);
            if (e is null) return $"There's no expense #{eid}.";
            target = $"expense #{e.Id} ({e.Description}, {Money(e.Amount)}, {e.Date:MMM d, yyyy})";
        }
        else
        {
            var l = await db.Leases.AsNoTracking().Include(x => x.Tenants).Include(x => x.Unit).ThenInclude(u => u!.Property).FirstOrDefaultAsync(x => x.Id == leaseId, ct);
            if (l is null) return $"There's no lease #{leaseId}.";
            target = $"lease #{l.Id} ({l.Unit?.DisplayName}, {l.TenantNames})";
        }
        return Propose($"Save {file.Name} to {target}", [$"File: {file.Name}", $"As: {(expenseId is not null ? "receipt" : "lease document")}"],
            async token => (await AttachAsync(file, expenseId, leaseId, token)).Trim());
    }

    // ---- helpers ----

    private string Propose(string summary, IReadOnlyList<string> details, Func<CancellationToken, Task<string>> apply)
    {
        var conversation = Conversation ?? throw new InvalidOperationException("No conversation.");
        var change = new ProposedChange { Number = conversation.Proposals.Count + 1, Summary = summary, Details = details, Apply = apply };
        conversation.Proposals.Add(change);
        conversation.NewProposals.Add(change);
        return $"Proposed change {change.Number}: {summary}. Nothing changes until the owner clicks Apply on the card under your answer. Tell them what you proposed; don't say it's done.";
    }

    private UploadedFile? FindFile(string name) =>
        Conversation?.Files.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Conversation?.Files.FirstOrDefault(f => f.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private string NoSuchFile(string name) =>
        Conversation?.Files.Count is > 0
            ? $"No attached file is named \"{name}\". Attached: {string.Join(", ", Conversation.Files.Select(f => f.Name))}."
            : "No files are attached in this conversation.";

    private async Task<string> AttachAsync(UploadedFile file, int? expenseId, int? leaseId, CancellationToken ct)
    {
        IEnumerable<(string, long, Func<Stream>)> upload = [(file.Name, file.Content.LongLength, () => new MemoryStream(file.Content))];
        var result = expenseId is { } e ? await receipts.AddAsync(e, upload, ct) : await leaseDocuments.AddAsync(leaseId!.Value, upload, ct);
        return result.Saved == 1
            ? $" Saved {file.Name} {(expenseId is not null ? "as its receipt" : "to the lease")}."
            : $" Couldn't save {file.Name}: {string.Join(" ", result.Problems)}";
    }

    private record Place(int? PropertyId, int? UnitId, string Label);

    private static async Task<(Place Place, string? Error)> ResolvePlaceAsync(PropertyManagerDbContext db, string property, string? unit, CancellationToken ct)
    {
        if (Regex.IsMatch(property.Trim(), @"^(shared|all( properties)?)$", RegexOptions.IgnoreCase))
        {
            return (new Place(null, null, "All properties (shared)"), null);
        }
        var properties = await db.Properties.AsNoTracking().Include(p => p.Units).ToListAsync(ct);
        var matches = properties.Where(p => Contains(p.Name, property) || Contains(p.Street, property) || EventMatcher.MentionsProperty(property, p)).ToList();
        if (matches.Count != 1)
        {
            return (default!, matches.Count == 0
                ? $"No property matches \"{property}\". Properties: {string.Join("; ", properties.Select(p => p.Name))}, or \"shared\"."
                : $"\"{property}\" matches {string.Join(" and ", matches.Select(p => p.Name))}. Be more specific.");
        }
        var match = matches[0];
        if (string.IsNullOrWhiteSpace(unit))
        {
            return (new Place(match.Id, null, match.Name), null);
        }
        var u = match.Units.FirstOrDefault(x => string.Equals(x.Label, unit.Trim().Replace("Unit ", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase));
        return u is null
            ? (default!, $"{match.Name} has no unit \"{unit}\". Units: {string.Join(", ", match.Units.Select(x => x.Label))}.")
            : (new Place(match.Id, u.Id, $"{match.Name} – Unit {u.Label}"), null);
    }

    private static async Task<(Unit? Unit, string? Error)> ResolveUnitAsync(PropertyManagerDbContext db, string text, CancellationToken ct)
    {
        var units = await db.Units.AsNoTracking().Include(u => u.Property).ToListAsync(ct);
        var unitCounts = await UnitCountsAsync(db, ct);
        var matches = units.Where(u => MentionsUnit(text, u, unitCounts)).ToList();
        return matches.Count == 1 ? (matches[0], null)
            : (null, matches.Count == 0
                ? $"No unit matches \"{text}\". Units: {string.Join("; ", units.Select(u => u.DisplayName))}."
                : $"\"{text}\" matches {string.Join("; ", matches.Select(u => u.DisplayName))}. Say which unit.");
    }

    private static async Task<Dictionary<int, int>> UnitCountsAsync(PropertyManagerDbContext db, CancellationToken ct) =>
        (await db.Units.AsNoTracking().Select(u => u.PropertyId).ToListAsync(ct)).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Whether text like "Taylor B" or "2228 1st St unit A" names this unit (its property, and its label unless it's the only unit).</summary>
    private static bool MentionsUnit(string text, Unit unit, Dictionary<int, int> unitCounts)
    {
        if (unit.Property is not { } property || !(Contains(text, property.Name) || EventMatcher.MentionsProperty(text, property)))
        {
            return false;
        }
        var rest = Regex.Replace(text, Regex.Escape(property.Name), " ", RegexOptions.IgnoreCase);
        return unitCounts.GetValueOrDefault(property.Id) == 1 || Regex.IsMatch(rest, $@"(?<![\w]){Regex.Escape(unit.Label)}(?![\w])", RegexOptions.IgnoreCase);
    }

    private static ExpenseCategory? ResolveCategory(string text)
    {
        var t = text.Trim();
        var all = Enum.GetValues<ExpenseCategory>();
        return all.Cast<ExpenseCategory?>().FirstOrDefault(c => string.Equals(c!.Value.ToString(), t.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) || string.Equals(c.Value.DisplayName(), t, StringComparison.OrdinalIgnoreCase))
            ?? (all.Where(c => c.DisplayName().Contains(t, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);
    }

    private static string UnknownCategory(string text) =>
        $"\"{text}\" isn't a category. Use one of: {string.Join(", ", Enum.GetValues<ExpenseCategory>().Select(c => c.DisplayName()))}.";

    private static PaymentMethod? ResolveMethod(string text) =>
        Enum.TryParse<PaymentMethod>(text.Replace(" ", ""), true, out var m) ? m
        : text.Contains("credit", StringComparison.OrdinalIgnoreCase) || text.Contains("debit", StringComparison.OrdinalIgnoreCase) ? PaymentMethod.Card
        : text.Contains("transfer", StringComparison.OrdinalIgnoreCase) || text.Contains("ach", StringComparison.OrdinalIgnoreCase) ? PaymentMethod.BankTransfer
        : null;

    /// <summary>An existing vendor by name, or the name of a new one to add when applied.</summary>
    private static async Task<(int? Id, string? Name, bool IsNew)> ResolveVendorAsync(PropertyManagerDbContext db, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null, false);
        }
        var vendors = await db.Vendors.AsNoTracking().ToListAsync(ct);
        var match = vendors.FirstOrDefault(v => string.Equals(v.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? (vendors.Where(v => Contains(v.Name, name.Trim()) || Contains(name, v.Name)).ToList() is { Count: 1 } one ? one[0] : null);
        return match is null ? (null, name.Trim(), true) : (match.Id, match.Name, false);
    }

    private static async Task<Vendor> AddVendorAsync(PropertyManagerDbContext db, string name, CancellationToken ct, string? trade = null, string? phone = null, string? email = null)
    {
        var existing = (await db.Vendors.ToListAsync(ct)).FirstOrDefault(v => string.Equals(v.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }
        var vendor = new Vendor
        {
            Name = name.Trim(),
            Trade = string.IsNullOrWhiteSpace(trade) ? null : trade.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
        };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync(ct);
        return vendor;
    }

    private static string PlaceLabel(Expense e) => e.IsShared ? "All properties (shared)" : e.Unit is null ? e.Property?.Name ?? "" : $"{e.Property?.Name} – Unit {e.Unit.Label}";

    private static bool Contains(string? value, string part) => !string.IsNullOrWhiteSpace(part) && value?.Contains(part.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    private static string Money(decimal amount) => amount.ToString("C", Us);

    private static DateOnly? ParseDate(string? value) => DateOnly.TryParse(value, Us, out var date) ? date : null;
}
