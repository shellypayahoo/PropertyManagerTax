using PropertyManager.Core.Import;

namespace PropertyManager.Tests;

public class BankCsvReaderTests
{
    private static BankCsvResult ReadWithDetectedColumns(string csv)
    {
        var rows = CsvParser.Parse(csv);
        var (headers, start) = BankCsvReader.FindHeader(rows);
        var mapping = BankCsvReader.DetectColumns(headers, rows, start);
        Assert.True(BankCsvReader.IsComplete(mapping), "column detection failed");
        return BankCsvReader.Read(csv, mapping);
    }

    [Fact]
    public void Chase_SignedAmountColumn()
    {
        var result = ReadWithDetectedColumns("""
            Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #
            DEBIT,10/14/2026,"PEPCO        BILL PAY   ",-182.33,ACH_DEBIT,10512.10,,
            CREDIT,10/01/2026,"ONLINE TRANSFER FROM CHK ...1234",8900.00,ACCT_XFER,10694.43,,
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 14), result.Rows[0].Date);
        Assert.Equal("PEPCO BILL PAY", result.Rows[0].Description);
        Assert.Equal(-182.33m, result.Rows[0].Amount);
        Assert.Equal(8900m, result.Rows[1].Amount);
    }

    [Fact]
    public void BankOfAmerica_SkipsSummaryPreambleAndBalanceRows()
    {
        var result = ReadWithDetectedColumns("""
            Description,,Summary Amt.
            Beginning balance as of 01/01/2026,,"1,000.00"
            Ending balance as of 01/31/2026,,"2,000.00"

            Date,Description,Amount,Running Bal.
            01/01/2026,Beginning balance as of 01/01/2026,,"1,000.00"
            01/05/2026,"Zelle payment from JANE SAMPLE Conf# abc123","4,500.00","5,500.00"
            """);

        Assert.Empty(result.Errors);
        var row = Assert.Single(result.Rows);
        Assert.Equal(4500m, row.Amount);
    }

    [Fact]
    public void CapitalOne_SeparateDebitAndCreditColumns()
    {
        var result = ReadWithDetectedColumns("""
            Transaction Date,Posted Date,Card No.,Description,Category,Debit,Credit
            2026-03-02,2026-03-03,1234,HOME DEPOT #2501,Merchandise,125.40,
            2026-03-05,2026-03-06,1234,HOME DEPOT RETURN,Merchandise,,20.00
            """);

        Assert.Equal([-125.40m, 20.00m], result.Rows.Select(r => r.Amount));
        Assert.Equal(new DateOnly(2026, 3, 2), result.Rows[0].Date);
    }

    [Fact]
    public void WellsFargo_NoHeaderRow()
    {
        var result = ReadWithDetectedColumns("""
            "04/15/2026","-2500.00","*","","DC TREASURER RE TAX 1354 TAYLOR"
            "04/16/2026","-75.00","*","","ACE HARDWARE"
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("DC TREASURER RE TAX 1354 TAYLOR", result.Rows[0].Description);
        Assert.Equal(-2500m, result.Rows[0].Amount);
    }

    [Fact]
    public void IdenticalRowsGetDistinctFingerprints_AndRereadingIsStable()
    {
        const string csv = """
            Date,Description,Amount
            05/01/2026,WATER BILL,-60.00
            05/01/2026,WATER BILL,-60.00
            """;
        var first = ReadWithDetectedColumns(csv);
        var second = ReadWithDetectedColumns(csv);

        Assert.NotEqual(first.Rows[0].Fingerprint, first.Rows[1].Fingerprint);
        Assert.Equal(first.Rows.Select(r => r.Fingerprint), second.Rows.Select(r => r.Fingerprint));
    }

    [Theory]
    [InlineData("(45.00)", -45.00)]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData("-12", -12)]
    [InlineData("99.10 DR", -99.10)]
    public void ParsesAmountFormats(string text, decimal expected)
    {
        Assert.True(ValueParsers.TryParseAmount(text, out var amount));
        Assert.Equal(expected, amount);
    }
}
