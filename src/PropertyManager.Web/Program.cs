using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Import;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;
using PropertyManager.Web.Components;

// All amounts are US dollars, so format currency and dates as en-US regardless of the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Blazor Server circuits are long-lived, so use a factory and create a short-lived DbContext per operation.
builder.Services.AddDbContextFactory<PropertyManagerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("PropertyManager")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<VendorService>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<PropertyManager.Core.Reports.ProfitAndLossPdf>();
builder.Services.AddScoped<PropertyManager.Core.Reports.OperatingReports>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<MortgageImportService>();
builder.Services.AddScoped<LeaseDocumentService>();
builder.Services.AddScoped<ExpenseSplitService>();
builder.Services.AddScoped<RecurringExpenseService>();
builder.Services.AddSingleton<PropertyManager.Core.Messaging.IImageConverter, PropertyManager.Core.Messaging.SipsImageConverter>();
builder.Services.AddScoped<PropertyManager.Core.Messaging.TextMessageImportService>();
builder.Services.AddScoped<PropertyManager.Core.Messaging.TextMessageSearch>();
builder.Services.AddScoped<PropertyManager.Core.Messaging.MessageRetentionService>();

// AI models for the Ask page. API keys belong in user secrets or environment variables (see README), not appsettings.json.
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.Section));
builder.Services.AddScoped<AssistantTools>();
builder.Services.AddScoped<AssistantService>();

// Tenant emails from a Hotmail/Outlook.com folder, read-only through Microsoft Graph.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.AddSingleton<EmailSignIn>();
builder.Services.AddSingleton<IMailTokenProvider>(sp => sp.GetRequiredService<EmailSignIn>());
builder.Services.AddHttpClient<IMailSource, GraphMailSource>();
builder.Services.AddScoped<EmailSyncService>();
builder.Services.AddScoped<PropertyManager.Core.Calendar.CalendarSyncService>();

// Uploaded documents live on disk; swap this registration for an Azure Blob implementation when hosting.
builder.Services.AddSingleton<IDocumentStorage>(new LocalDocumentStorage(
    Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["DocumentStorage:LocalPath"] ?? "App_Data/documents")));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PropertyManagerDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);
    await scope.ServiceProvider.GetRequiredService<RecurringExpenseService>().GenerateDueAsync();
    // Documents uploaded before text reading existed are read once, so the Ask page can search them.
    await scope.ServiceProvider.GetRequiredService<LeaseDocumentService>().ReadMissingTextAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Lease documents are uploaded with a regular browser form post rather than over the Blazor
// connection, which is more reliable for large files and across browsers (notably Safari).
app.MapPost("/leases/{id:int}/documents", async (int id, IFormFileCollection files, LeaseDocumentService documents, CancellationToken ct) =>
{
    var result = await documents.AddAsync(id, files.Select(f => (f.FileName, f.Length, (Func<Stream>)f.OpenReadStream)), ct);
    var query = $"uploaded={result.Saved}";
    if (files.Count == 0)
    {
        query += "&uploadError=" + Uri.EscapeDataString("Choose a file first.");
    }
    else if (result.Problems.Count > 0)
    {
        query += "&uploadError=" + Uri.EscapeDataString(string.Join(" ", result.Problems));
    }
    return Results.Redirect($"/leases/{id}?{query}#documents");
})
.WithMetadata(new RequestSizeLimitAttribute(LeaseDocumentService.MaxFileBytes * LeaseDocumentService.MaxFilesPerUpload))
.WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = LeaseDocumentService.MaxFileBytes * LeaseDocumentService.MaxFilesPerUpload });

app.MapGet("/documents/{id:int}", async (int id, bool? download, IDbContextFactory<PropertyManagerDbContext> dbFactory,
    IDocumentStorage storage, HttpContext http, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var document = await db.LeaseDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    var stream = document is null ? null : await storage.OpenReadAsync(document.StorageKey, ct);
    if (stream is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.XContentTypeOptions = "nosniff";
    return download == true
        ? Results.File(stream, document!.ContentType, document.FileName)
        : Results.File(stream, document!.ContentType);
});
app.MapGet("/reports/{year:int}/profit-and-loss.pdf", async (int year, bool? detail, PropertyManager.Core.Reports.ProfitAndLossPdf pdf, CancellationToken ct) =>
    Results.File(await pdf.CreateAsync(year, detail == true, ct), "application/pdf", PropertyManager.Core.Reports.ProfitAndLossPdf.FileName(year)));

app.MapPost("/expenses/{id:int}/receipts", async (int id, IFormFileCollection files, ReceiptService receipts, CancellationToken ct) =>
{
    var result = await receipts.AddAsync(id, files.Select(f => (f.FileName, f.Length, (Func<Stream>)f.OpenReadStream)), ct);
    var query = $"receiptsFor={id}&receiptsAdded={result.Saved}";
    if (result.Problems.Count > 0)
    {
        query += "&receiptError=" + Uri.EscapeDataString(string.Join(" ", result.Problems));
    }
    return Results.Redirect($"/expenses?{query}#expense-{id}");
})
.WithMetadata(new RequestSizeLimitAttribute(LeaseDocumentService.MaxFileBytes * LeaseDocumentService.MaxFilesPerUpload))
.WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = LeaseDocumentService.MaxFileBytes * LeaseDocumentService.MaxFilesPerUpload });

app.MapGet("/receipts/{id:int}", async (int id, string? size, IDbContextFactory<PropertyManagerDbContext> dbFactory,
    IDocumentStorage storage, HttpContext http, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var receipt = await db.ExpenseReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    var (key, type) = size switch
    {
        "thumb" when receipt?.ThumbnailKey is { } k => (k, "image/jpeg"),
        "preview" when receipt?.PreviewKey is { } k => (k, "image/jpeg"),
        _ => (receipt?.StorageKey, receipt?.ContentType),
    };
    var stream = key is null ? null : await storage.OpenReadAsync(key, ct);
    if (stream is null)
    {
        return Results.NotFound();
    }
    http.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(stream, type ?? "application/octet-stream");
});

// size: "thumb" (small JPEG), "preview" (JPEG up to 1600px), or omitted for the original file.
app.MapGet("/text-attachments/{id:int}", async (int id, string? size, bool? download, IDbContextFactory<PropertyManagerDbContext> dbFactory,
    IDocumentStorage storage, HttpContext http, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var attachment = await db.TextAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    var (key, type) = size switch
    {
        "thumb" when attachment?.ThumbnailKey is { } k => (k, "image/jpeg"),
        "preview" when attachment?.PreviewKey is { } k => (k, "image/jpeg"),
        _ => (attachment?.StorageKey, attachment?.ContentType),
    };
    var stream = key is null ? null : await storage.OpenReadAsync(key, ct);
    if (stream is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.CacheControl = "private, max-age=86400";
    return download == true
        ? Results.File(stream, attachment!.ContentType, attachment.FileName)
        : Results.File(stream, type ?? "application/octet-stream", enableRangeProcessing: true);
});

app.MapGet("/email-attachments/{id:int}", async (int id, bool? download, IDbContextFactory<PropertyManagerDbContext> dbFactory,
    IDocumentStorage storage, HttpContext http, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var attachment = await db.EmailAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    var stream = attachment is null ? null : await storage.OpenReadAsync(attachment.StorageKey, ct);
    if (stream is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.XContentTypeOptions = "nosniff";
    return download == true
        ? Results.File(stream, attachment!.ContentType, attachment.FileName)
        : Results.File(stream, attachment!.ContentType);
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
