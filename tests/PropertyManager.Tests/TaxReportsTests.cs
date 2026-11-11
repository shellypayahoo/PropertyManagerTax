using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Reports;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class TaxReportsTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-tax-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly Form1099Report report;

    public TaxReportsTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        storage = new LocalDocumentStorage(root);
        report = new Form1099Report(dbFactory);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var taylor = db.Properties.Single(p => p.Name.Contains("Taylor"));
        var plumber = new Vendor { Name = "Ace Plumbing", Trade = "Plumber" };
        var painter = new Vendor { Name = "Leo", Trade = "Handyman" };
        var store = new Vendor { Name = "Home Depot", Exempt1099 = true };
        var lender = new Vendor { Name = "Rocket Mortgage" };
        Expense E(Vendor v, int month, decimal amount, ExpenseCategory category, PaymentMethod? paid = PaymentMethod.Zelle, int year = 2026) =>
            new() { Vendor = v, Property = taylor, Date = new(year, month, 10), Amount = amount, Category = category, Description = $"{v.Name} {month}", PaidWith = paid };
        db.Expenses.AddRange(
            E(plumber, 3, 1500m, ExpenseCategory.Repairs), E(plumber, 8, 900m, ExpenseCategory.Repairs),
            E(plumber, 9, 400m, ExpenseCategory.Supplies, PaymentMethod.Card),
            E(painter, 5, 1800m, ExpenseCategory.CleaningAndMaintenance),
            E(painter, 5, 700m, ExpenseCategory.CleaningAndMaintenance, year: 2025),
            E(store, 4, 3000m, ExpenseCategory.Supplies),
            E(lender, 1, 12000m, ExpenseCategory.MortgageInterest), E(lender, 1, 6000m, ExpenseCategory.MortgagePrincipal));
        db.BusinessDocuments.Add(new BusinessDocument { Kind = BusinessDocumentKind.TaxForm, Description = "W-9 Ace Plumbing", FileName = "w9.pdf", StorageKey = "x" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Threshold_is_600_through_2025_and_2000_from_2026()
    {
        Assert.Equal(600m, Form1099Report.DefaultThreshold(2025));
        Assert.Equal(2000m, Form1099Report.DefaultThreshold(2026));
    }

    [Fact]
    public async Task Vendors_paid_enough_for_services_need_a_1099()
    {
        var rows = await report.GetAsync(2026);

        var ace = rows.Single(r => r.Vendor.Name == "Ace Plumbing");
        Assert.Equal((2400m, 400m, true, true), (ace.Reportable, ace.PaidByCard, ace.Needs1099, ace.HasW9));
        var leo = rows.Single(r => r.Vendor.Name == "Leo");
        Assert.Equal((1800m, false, false), (leo.Reportable, leo.Needs1099, leo.HasW9));
        Assert.False(rows.Single(r => r.Vendor.Name == "Home Depot").Needs1099);
        var lender = rows.Single(r => r.Vendor.Name == "Rocket Mortgage");
        Assert.Equal((0m, 18000m, false), (lender.Reportable, lender.NotForServices, lender.Needs1099));
        Assert.Equal("Ace Plumbing", rows[0].Vendor.Name);

        // With the old $600 threshold, Leo would need one too.
        Assert.True((await report.GetAsync(2026, 600m)).Single(r => r.Vendor.Name == "Leo").Needs1099);
        Assert.True((await report.GetAsync(2025)).Single(r => r.Vendor.Name == "Leo").Needs1099);
    }

    [Fact]
    public async Task Spreadsheet_lists_every_vendor_and_keeps_formulas_out()
    {
        await using (var db = dbFactory.CreateDbContext())
        {
            db.Vendors.Single(v => v.Name == "Leo").Name = "=HYPERLINK(\"x\")";
            await db.SaveChangesAsync();
        }

        var csv = Encoding.UTF8.GetString(await report.CsvAsync(2026)).TrimStart('﻿');

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("Ace Plumbing,Plumber,,,2400.00,400.00,0.00,3,Yes,Yes,No", lines[1]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
    }

    [Fact]
    public async Task Packet_has_the_books_and_the_receipts()
    {
        var key = await storage.SaveAsync(new MemoryStream([1, 2, 3]), "invoice.pdf");
        await using (var db = dbFactory.CreateDbContext())
        {
            var expense = db.Expenses.Single(e => e.Description == "Ace Plumbing 3");
            db.ExpenseReceipts.Add(new ExpenseReceipt { ExpenseId = expense.Id, FileName = "invoice.pdf", ContentType = "application/pdf", StorageKey = key });
            var lease = new Lease { UnitId = db.Units.First().Id, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2026, 1, 1), MonthlyRent = 1000m };
            lease.Payments.Add(new RentPayment { Date = new(2026, 2, 1), ForMonth = new(2026, 2, 1), Amount = 1000m });
            db.Leases.Add(lease);
            await db.SaveChangesAsync();
        }
        var reports = new ReportService(dbFactory);
        var packet = new AccountantPacket(dbFactory, new ProfitAndLossPdf(reports, dbFactory, TimeProvider.System), report, storage, TimeProvider.System);
        using var output = new MemoryStream();

        await packet.WriteAsync(2026, output);

        output.Position = 0;
        using var zip = new ZipArchive(output);
        Assert.Equal(
            ["1099 vendors 2026.csv", "Expenses 2026.csv", "Profit and loss 2026.pdf", "Read me first.txt", "Receipts/2026-03-10 Ace Plumbing 1500.00.pdf", "Rent received 2026.csv"],
            zip.Entries.Select(e => e.FullName).Order());
        var expenses = new StreamReader(zip.GetEntry("Expenses 2026.csv")!.Open()).ReadToEnd();
        Assert.Contains("2026-03-10,1354 Taylor St NW,,Repairs,14,Yes,Ace Plumbing 3,Ace Plumbing,Zelle,1500.00,,2026-03-10 Ace Plumbing 1500.00.pdf", expenses);
        Assert.DoesNotContain("2025-05-10", expenses);
        var rent = new StreamReader(zip.GetEntry("Rent received 2026.csv")!.Open()).ReadToEnd();
        Assert.Contains("Jane Sample", rent);
        var readme = new StreamReader(zip.GetEntry("Read me first.txt")!.Open()).ReadToEnd();
        Assert.Contains("Rent received: $1,000.00", readme);
        Assert.Contains("Not deductible (mortgage principal, escrow deposits): $6,000.00", readme);
        Assert.StartsWith("%PDF", new StreamReader(zip.GetEntry("Profit and loss 2026.pdf")!.Open()).ReadLine());
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
