using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PropertyManager.Core.Reports;

/// <summary>
/// The annual profit and loss as a PDF for a tax preparer: the Schedule E summary by property, notes on shared and
/// non-deductible amounts, and optionally every item behind each line.
/// </summary>
public class ProfitAndLossPdf(ReportService reports, IDbContextFactory<PropertyManagerDbContext> dbFactory, TimeProvider clock, IOptionsMonitor<BusinessOptions>? business = null)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    static ProfitAndLossPdf()
    {
        // Free Community license: individuals and businesses with under $1M annual revenue.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string FileName(int year) => $"Rental P&L {year}.pdf";

    public async Task<byte[]> CreateAsync(int year, bool includeDetail, CancellationToken ct = default)
    {
        var statements = await reports.GetAnnualProfitAndLossAsync(year, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var properties = await db.Properties.AsNoTracking().Include(p => p.Units).ToDictionaryAsync(p => p.Id, ct);

        var categories = statements.SelectMany(s => s.Expenses).Select(e => e.Category).Distinct().Order().ToList();
        var notDeductible = statements.SelectMany(s => s.NotDeductible).Select(e => e.Category).Distinct().Order().ToList();
        var details = new List<(string Title, ReportDetail Detail, string PartyHeader)>();
        if (includeDetail)
        {
            details.Add(("Line 3 – Rents received", await reports.GetDetailAsync(year, null, null, ct), "How paid"));
            foreach (var category in categories.Concat(notDeductible))
            {
                var title = category.ScheduleELine() is { } line ? $"Line {line} – {category.DisplayName()}" : category.DisplayName();
                details.Add((title, await reports.GetDetailAsync(year, category, null, ct), "Vendor"));
            }
        }

        var generated = clock.GetLocalNow();
        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(statements.Count > 3 ? PageSizes.Letter.Landscape() : PageSizes.Letter);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(9));

                page.Header().PaddingBottom(10).Column(header =>
                {
                    if (business?.CurrentValue.Name is { Length: > 0 } name)
                    {
                        header.Item().Text(name).FontSize(11).FontColor(Colors.Grey.Darken2);
                    }
                    header.Item().Text($"Rental Income and Expenses – Tax Year {year}").FontSize(16).SemiBold();
                    header.Item().Text($"Schedule E summary by property · Prepared {generated.ToString("MMMM d, yyyy", Us)}").FontColor(Colors.Grey.Darken1);
                });

                page.Content().Column(content =>
                {
                    content.Spacing(12);
                    content.Item().Element(c => PropertyList(c, statements, properties));
                    content.Item().Element(c => SummaryTable(c, statements, categories));
                    content.Item().Element(c => Notes(c, statements, notDeductible));

                    foreach (var (title, detail, partyHeader) in details)
                    {
                        content.Item().Element(c => DetailTable(c, title, detail, partyHeader));
                    }
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"Tax year {year}").FontColor(Colors.Grey.Darken1);
                    row.RelativeItem().AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontColor(Colors.Grey.Darken1));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" of ");
                        t.TotalPages();
                    });
                });
            });
        }).GeneratePdf();
    }

    private static void PropertyList(IContainer container, IReadOnlyList<PropertyProfitAndLoss> statements, Dictionary<int, Property> properties) =>
        container.Column(list =>
        {
            list.Item().Text("Properties").SemiBold().FontSize(11);
            foreach (var s in statements)
            {
                var p = properties.GetValueOrDefault(s.PropertyId);
                var units = p?.Units.Count ?? 0;
                list.Item().Text($"{p?.FullAddress ?? s.PropertyName} · {units} unit{(units == 1 ? "" : "s")}"
                    + (p?.PurchaseDate is { } bought ? $" · purchased {bought.ToString("MMM d, yyyy", Us)}" : ""));
            }
        });

    private static void SummaryTable(IContainer container, IReadOnlyList<PropertyProfitAndLoss> statements, IReadOnlyList<ExpenseCategory> categories) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(32);
                columns.RelativeColumn(3);
                foreach (var _ in statements)
                {
                    columns.RelativeColumn(1.4f);
                }
                columns.RelativeColumn(1.4f);
            });

            table.Header(h =>
            {
                h.Cell().Element(HeaderCell).Text("Line");
                h.Cell().Element(HeaderCell).Text("");
                foreach (var s in statements)
                {
                    h.Cell().Element(HeaderCell).AlignRight().Text(s.PropertyName);
                }
                h.Cell().Element(HeaderCell).AlignRight().Text("Total");
            });

            void Row(string line, string label, IEnumerable<decimal> amounts, bool bold = false, bool muted = false, bool topBorder = false)
            {
                var values = amounts.ToList();
                IContainer Cell(IContainer c) => (topBorder ? c.BorderTop(1).BorderColor(Colors.Grey.Darken1) : c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)).PaddingVertical(3);
                void Text(IContainer c, string text, bool right)
                {
                    var t = (right ? c.AlignRight() : c).Text(text);
                    if (bold) t.SemiBold();
                    if (muted) t.FontColor(Colors.Grey.Darken1).Italic();
                }
                table.Cell().Element(Cell).Element(c => Text(c, line, false));
                table.Cell().Element(Cell).Element(c => Text(c, label, false));
                foreach (var v in values)
                {
                    table.Cell().Element(Cell).Element(c => Text(c, Money(v), true));
                }
                table.Cell().Element(Cell).Element(c => Text(c, Money(values.Sum()), true));
            }

            Row("3", "Rents received", statements.Select(s => s.RentReceived), bold: true);
            Row("", "Scheduled rent under leases (for comparison)", statements.Select(s => s.ScheduledRent), muted: true);
            foreach (var category in categories)
            {
                Row(category.ScheduleELine()?.ToString() ?? "", category.DisplayName(), statements.Select(s => s.Expenses.Where(e => e.Category == category).Sum(e => e.Amount)));
            }
            Row("20", "Total expenses", statements.Select(s => s.TotalExpenses), bold: true, topBorder: true);
            Row("21", "Net income (loss)", statements.Select(s => s.NetIncome), bold: true);
        });

    private static void Notes(IContainer container, IReadOnlyList<PropertyProfitAndLoss> statements, IReadOnlyList<ExpenseCategory> notDeductible) =>
        container.Column(notes =>
        {
            notes.Spacing(4);
            notes.Item().Text("Notes").SemiBold().FontSize(11);
            notes.Item().Text("Rents received are payments by the date received. Expenses are by the date paid.");
            var shared = statements.Sum(s => s.SharedExpenses);
            if (shared != 0m)
            {
                notes.Item().Text($"Includes {Money(shared)} of expenses shared by all properties (e.g. software, accounting), split by number of units: "
                    + string.Join(", ", statements.Select(s => $"{s.PropertyName} {Money(s.SharedExpenses)}")) + ".");
            }
            foreach (var category in notDeductible)
            {
                var amounts = statements.Select(s => (s.PropertyName, Amount: s.NotDeductible.Where(n => n.Category == category).Sum(n => n.Amount))).Where(a => a.Amount != 0m).ToList();
                notes.Item().Text($"Not included above: {category.DisplayName().Replace(" (not deductible)", "").ToLowerInvariant()} {Money(amounts.Sum(a => a.Amount))} ("
                    + string.Join(", ", amounts.Select(a => $"{a.PropertyName} {Money(a.Amount)}")) + ").");
            }
            if (notDeductible.Contains(ExpenseCategory.EscrowDeposit))
            {
                notes.Item().Text("Escrow deposits are excluded; property taxes and insurance the lender paid from escrow are included under Taxes and Insurance on the dates paid.");
            }
            if (!statements.Any(s => s.Expenses.Any(e => e.Category == ExpenseCategory.Depreciation)))
            {
                notes.Item().Text("Depreciation (line 18) is not recorded here.");
            }
            notes.Item().Text("Prepared from the owner's bookkeeping records; not tax advice.").FontColor(Colors.Grey.Darken1);
        });

    private static void DetailTable(IContainer container, string title, ReportDetail detail, string partyHeader) =>
        container.Column(section =>
        {
            section.Item().PaddingTop(6).Text(title).SemiBold().FontSize(11);
            if (detail.Lines.Count == 0)
            {
                section.Item().Text("No items.").FontColor(Colors.Grey.Darken1);
                return;
            }
            section.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(62);
                    c.RelativeColumn(2);
                    c.RelativeColumn(4);
                    c.RelativeColumn(1.6f);
                    c.ConstantColumn(70);
                });
                table.Header(h =>
                {
                    h.Cell().Element(HeaderCell).Text("Date");
                    h.Cell().Element(HeaderCell).Text("Property");
                    h.Cell().Element(HeaderCell).Text("Description");
                    h.Cell().Element(HeaderCell).Text(partyHeader);
                    h.Cell().Element(HeaderCell).AlignRight().Text("Amount");
                });
                foreach (var line in detail.Lines)
                {
                    static IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3).PaddingVertical(2);
                    table.Cell().Element(Cell).Text(line.Date.ToString("MM/dd/yyyy", Us));
                    table.Cell().Element(Cell).Text(line.Where);
                    table.Cell().Element(Cell).Text(line.Description);
                    table.Cell().Element(Cell).Text(line.Party ?? "");
                    table.Cell().Element(Cell).AlignRight().Text(Money(line.Amount));
                }
                table.Cell().ColumnSpan(4).PaddingTop(3).AlignRight().Text("Total").SemiBold();
                table.Cell().PaddingTop(3).AlignRight().Text(Money(detail.Total)).SemiBold();
            });
        });

    private static IContainer HeaderCell(IContainer c) =>
        c.BorderBottom(1).BorderColor(Colors.Grey.Darken2).PaddingVertical(3).DefaultTextStyle(t => t.SemiBold());

    // Accounting style: losses and refunds in parentheses.
    private static string Money(decimal amount) => amount.ToString("$#,##0.00;($#,##0.00)", Us);
}
