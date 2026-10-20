using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
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
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<ImportService>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
