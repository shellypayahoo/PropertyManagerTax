using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Reports;

/// <summary>One line of a depreciation schedule for a year.</summary>
public record DepreciationLine(
    int PropertyId, string PropertyName, string Item, DateOnly PlacedInService, decimal Basis, AssetLife Life,
    decimal PriorYears, decimal ThisYear)
{
    public decimal Accumulated => PriorYears + ThisYear;
    public decimal Remaining => Basis - Accumulated;
}

/// <summary>
/// Depreciation for residential rental property under the IRS's general system (MACRS GDS), as in Publication 946:
/// the building and its improvements straight-line over 27.5 years with the mid-month convention; 5- and 15-year
/// property by the half-year tables. Bonus depreciation, Section 179, and the de minimis safe harbor aren't modeled:
/// something written off all at once is simply entered as an expense instead.
/// </summary>
public static class Depreciation
{
    // Half-year convention percentages, Publication 946 Table A-1.
    private static readonly decimal[] FiveYear = [20.00m, 32.00m, 19.20m, 11.52m, 11.52m, 5.76m];
    private static readonly decimal[] FifteenYear =
        [5.00m, 9.50m, 8.55m, 7.70m, 6.93m, 6.23m, 5.90m, 5.90m, 5.91m, 5.90m, 5.91m, 5.90m, 5.91m, 5.90m, 5.91m, 2.95m];

    private const decimal BuildingMonths = 27.5m * 12;

    /// <summary>The deduction for one calendar year.</summary>
    public static decimal ForYear(decimal basis, DateOnly placedInService, AssetLife life, int year)
    {
        var index = year - placedInService.Year;
        if (basis <= 0 || index < 0)
        {
            return 0m;
        }
        if (life != AssetLife.ResidentialBuilding)
        {
            var table = life == AssetLife.FiveYear ? FiveYear : FifteenYear;
            return index < table.Length ? Round(basis * table[index] / 100m) : 0m;
        }
        // Mid-month: the month it's placed in service counts as half a month, and so does the last one.
        var firstYearMonths = 12.5m - placedInService.Month;
        var before = index == 0 ? 0m : firstYearMonths + 12m * (index - 1);
        var months = Math.Min(index == 0 ? firstYearMonths : 12m, BuildingMonths - before);
        return months <= 0 ? 0m : Round(basis * months / BuildingMonths);
    }

    /// <summary>Everything deducted in the years before <paramref name="year"/>.</summary>
    public static decimal Before(decimal basis, DateOnly placedInService, AssetLife life, int year)
    {
        var total = 0m;
        for (var y = placedInService.Year; y < year; y++)
        {
            total += ForYear(basis, placedInService, life, y);
        }
        return Math.Min(total, basis);
    }

    /// <summary>What the building cost for depreciation: price plus closing costs, less the land. Null until the land value is known.</summary>
    public static decimal? BuildingBasis(Property property) =>
        property is { PurchasePrice: { } price, LandValue: { } land } && (property.RentalStartDate ?? property.PurchaseDate) is not null
            ? Math.Max(0m, price + (property.BasisAdditions ?? 0m) - land)
            : null;

    /// <summary>The schedule for a property in a year: the building, then each improvement. Needs <see cref="Property.Assets"/> loaded.</summary>
    public static List<DepreciationLine> Schedule(Property property, int year)
    {
        var lines = new List<DepreciationLine>();
        void Add(string item, DateOnly placed, decimal basis, AssetLife life)
        {
            if (placed.Year <= year && basis > 0)
            {
                var prior = Before(basis, placed, life, year);
                lines.Add(new DepreciationLine(property.Id, property.Name, item, placed, basis, life, prior, Math.Min(ForYear(basis, placed, life, year), basis - prior)));
            }
        }
        if (BuildingBasis(property) is { } building)
        {
            Add("Building", (property.RentalStartDate ?? property.PurchaseDate)!.Value, building, AssetLife.ResidentialBuilding);
        }
        foreach (var asset in property.Assets.OrderBy(a => a.PlacedInService).ThenBy(a => a.Id))
        {
            Add(asset.Description, asset.PlacedInService, asset.Cost, asset.Life);
        }
        return lines;
    }

    /// <summary>
    /// The schedule's amounts for any period: each year's deduction is spread evenly over the days of that year the
    /// item was in service, so a quarter gets its share and a full year gets the whole amount.
    /// </summary>
    public static List<(string Item, decimal Amount)> ForPeriod(Property property, DateOnly from, DateOnly to)
    {
        var totals = new Dictionary<string, decimal>();
        var order = new List<string>();
        for (var year = from.Year; year <= to.Year; year++)
        {
            foreach (var line in Schedule(property, year).Where(l => l.ThisYear > 0))
            {
                var serviceStart = line.PlacedInService.Year == year ? line.PlacedInService : new DateOnly(year, 1, 1);
                var yearEnd = new DateOnly(year, 12, 31);
                var start = from > serviceStart ? from : serviceStart;
                var end = to < yearEnd ? to : yearEnd;
                if (start > end)
                {
                    continue;
                }
                var share = (end.DayNumber - start.DayNumber + 1m) / (yearEnd.DayNumber - serviceStart.DayNumber + 1m);
                if (!totals.ContainsKey(line.Item))
                {
                    order.Add(line.Item);
                }
                totals[line.Item] = totals.GetValueOrDefault(line.Item) + Round(line.ThisYear * share);
            }
        }
        return order.Select(item => (item, totals[item])).ToList();
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
