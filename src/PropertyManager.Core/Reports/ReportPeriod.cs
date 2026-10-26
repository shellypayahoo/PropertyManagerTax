using System.Globalization;

namespace PropertyManager.Core.Reports;

/// <summary>A reporting period (both dates included), with a label such as "Q2 2026" or "Jan 1 – Sep 30, 2026".</summary>
public record ReportPeriod(DateOnly From, DateOnly To)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public bool IsCalendarYear => From.Month == 1 && From.Day == 1 && To.Month == 12 && To.Day == 31 && From.Year == To.Year;

    public string Label =>
        IsCalendarYear ? From.Year.ToString(Us)
        : From.Year == To.Year ? $"{From.ToString("MMM d", Us)} – {To.ToString("MMM d, yyyy", Us)}"
        : $"{From.ToString("MMM d, yyyy", Us)} – {To.ToString("MMM d, yyyy", Us)}";

    public static ReportPeriod Year(int year) => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    public static ReportPeriod YearToDate(DateOnly today) => new(new DateOnly(today.Year, 1, 1), today);

    /// <summary>Calendar quarter 1–4 of a year.</summary>
    public static ReportPeriod Quarter(int year, int quarter)
    {
        var start = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        return new(start, start.AddMonths(3).AddDays(-1));
    }

    /// <summary>The last <paramref name="count"/> completed calendar quarters before today's quarter.</summary>
    public static ReportPeriod LastCompletedQuarters(DateOnly today, int count)
    {
        var thisQuarterStart = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return new(thisQuarterStart.AddMonths(-3 * count), thisQuarterStart.AddDays(-1));
    }
}
