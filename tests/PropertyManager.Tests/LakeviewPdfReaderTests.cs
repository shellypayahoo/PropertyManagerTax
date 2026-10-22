using System.Globalization;
using PropertyManager.Core.Import;
using PropertyManager.Core.Import.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PropertyManager.Tests;

public class LakeviewPdfReaderTests
{
    public LakeviewPdfReaderTests() => CultureInfo.CurrentCulture = new CultureInfo("en-US");

    /// <summary>A payment history laid out like Lakeview's, with made-up numbers.</summary>
    private static byte[] SamplePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.Letter);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 740.0;
        void Line(params (double X, string Text)[] cells)
        {
            foreach (var (x, text) in cells)
            {
                page.AddText(text, 8, new PdfPoint(x, y), font);
            }
            y -= 14;
        }

        Line((40, "Lakeview - Payment History"));
        Line((40, "Transactional Details"), (500, "2026"));
        Line((40, "Due Date"), (100, "Date Paid"), (160, "Description"), (240, "Amount"), (300, "Principal"), (360, "Interest"), (420, "Escrow Paid"));
        Line((40, "03/01/2026"), (100, "03/02/2026"), (160, "Payment"), (240, "$3,000.00"), (300, "$1,000.00"), (360, "$1,250.00"), (420, "$750.00"), (480, "$9,000.00"), (540, "$400,000.00"));
        Line((160, "Separate"));
        Line((160, "County and"), (240, "-"), (420, "-"));
        Line((40, "03/01/2026"), (100, "03/13/2026"), (300, "—"), (360, "—"), (480, "$3,000.00"), (540, "$400,000.00"));
        Line((160, "City Tax"), (240, "$6,000.00"), (420, "$6,000.00"));
        Line((160, "Disbursements"));
        Line((40, "04/01/2026"), (100, "04/20/2026"), (160, "Hazard"), (240, "-"), (300, "—"), (360, "—"), (420, "-"), (480, "$2,500.00"), (540, "$399,000.00"));
        Line((160, "Insurance"), (240, "$1,250.00"), (420, "$1,250.00"));
        Line((160, "Disbursements"));
        Line((40, "04/01/2026"), (100, "04/01/2026"), (160, "Payment"), (240, "$3,000.00"), (300, "$1,002.00"), (360, "$1,248.00"), (420, "$750.00"), (480, "$3,250.00"), (540, "$399,000.00"));
        return builder.Build();
    }

    [Fact]
    public void ReadsPaymentsAndEscrowDisbursementsFromPdf()
    {
        var result = LakeviewPdfReader.Read(new MemoryStream(SamplePdf()));

        Assert.Empty(result.Warnings);
        Assert.Equal((2, 2), (result.Payments, result.Disbursements));
        Assert.Equal(
            """
            Date,Description,Total,Principal,Interest,Escrow
            03/02/2026,Payment,3000.00,1000.00,1250.00,750.00
            03/13/2026,County and City Tax Disbursement,6000.00,0.00,0.00,6000.00
            04/01/2026,Payment,3000.00,1002.00,1248.00,750.00
            04/20/2026,Hazard Insurance Disbursement,1250.00,0.00,0.00,1250.00

            """.ReplaceLineEndings("\n"),
            result.Csv);
    }

    [Fact]
    public void ConvertedCsvFlowsThroughTheMortgageReader()
    {
        var csv = LakeviewPdfReader.Read(new MemoryStream(SamplePdf())).Csv;
        var (headers, _) = BankCsvReader.FindHeader(CsvParser.Parse(csv));

        var rows = MortgageCsvReader.Read(csv, MortgageCsvReader.DetectColumns(headers)).Rows;

        Assert.Equal([MortgageRowKind.Payment, MortgageRowKind.TaxPayout, MortgageRowKind.Payment, MortgageRowKind.InsurancePayout], rows.Select(r => r.Kind));
        Assert.Equal(6000m, rows[1].Escrow);
    }

    [Fact]
    public void WarnsWhenPaymentPartsDontAddUp_AndWhenFormatIsUnknown()
    {
        var result = LakeviewPdfReader.FromLines(["01/01/2026 01/02/2026 Payment $3,000.00 $1,000.00 $1,250.00 $700.00 $1.00 $2.00"]);

        Assert.Equal(1, result.Payments);
        Assert.Contains(result.Warnings, w => w.Contains("Date Paid"));
        Assert.Contains(result.Warnings, w => w.Contains("add up to $2,950.00"));
    }
}
