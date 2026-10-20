using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Services;
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
