using PropertyManager.Core.Import;

namespace PropertyManager.Tests;

public class TextExpenseParserTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void SplitsMultipleItems()
    {
        var result = TextExpenseParser.Parse("hey it's $180 for the lawn this week and $40 for the hedges", Today);

        Assert.Equal(
            [new TextExpenseLine(180m, "Lawn this week"), new TextExpenseLine(40m, "Hedges")],
            result.Lines);
        Assert.Null(result.Date);
    }

    [Fact]
    public void LeavesDateOutOfDescription()
    {
        var result = TextExpenseParser.Parse("hey it's $180 for the lawn on 2/4 and $40 for the hedges", Today);

        Assert.Equal(new DateOnly(2026, 2, 4), result.Date);
        Assert.Equal(["Lawn", "Hedges"], result.Lines.Select(l => l.Description));
    }

    [Fact]
    public void DropsTotalThatSumsTheItems()
    {
        var result = TextExpenseParser.Parse("Mowing $150, leaf cleanup $95.50. Total $245.50 thanks", Today);

        Assert.Equal([150m, 95.50m], result.Lines.Select(l => l.Amount));
        Assert.Equal("Mowing", result.Lines[0].Description);
        Assert.Equal("Leaf cleanup", result.Lines[1].Description);
    }

    [Fact]
    public void ReadsDollarsWordAndDate()
    {
        var result = TextExpenseParser.Parse("Cut both yards on 10/12, that's 200 dollars", Today);

        Assert.Equal(new DateOnly(2026, 10, 12), result.Date);
        Assert.Equal(new TextExpenseLine(200m, "Cut both yards"), Assert.Single(result.Lines));
    }

    [Fact]
    public void DateLaterThanTodayWithoutYearMeansLastYear()
    {
        var result = TextExpenseParser.Parse("12/28 snow removal $120", new DateOnly(2027, 1, 3));

        Assert.Equal(new DateOnly(2026, 12, 28), result.Date);
    }

    [Fact]
    public void NoAmounts_ReturnsNoLines()
    {
        Assert.Empty(TextExpenseParser.Parse("I'll be there Tuesday", Today).Lines);
    }
}

public class RuleSuggestionsTests
{
    [Theory]
    [InlineData("HOME DEPOT #2501 WASHINGTON DC", "HOME DEPOT")]
    [InlineData("PEPCO BILL PAY", "PEPCO BILL PAY")]
    [InlineData("Zelle payment from JANE SAMPLE Conf# abc123", "JANE SAMPLE")]
    [InlineData("POS PURCHASE ACE HARDWARE 0412", "ACE HARDWARE")]
    public void SuggestsMeaningfulMatchText(string description, string expected)
    {
        Assert.Equal(expected, PropertyManager.Core.Import.RuleSuggestions.MatchTextFor(description));
    }
}
