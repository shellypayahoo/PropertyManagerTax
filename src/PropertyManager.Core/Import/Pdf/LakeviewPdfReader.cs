using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PropertyManager.Core.Import.Pdf;

/// <summary>A lender PDF converted to the CSV layout the mortgage import reads.</summary>
public record MortgagePdfResult(string Csv, int Payments, int Disbursements, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads Lakeview Loan Servicing's "Payment History" PDF (saved from the lender's website). Payment rows look like
/// <c>10/01/2026 10/01/2026 Payment $4,479.51 $1,629.09 $1,180.78 $1,669.64 …balances</c>; escrow disbursements span
/// a few lines ("Separate County and City Tax Disbursements", "Hazard Insurance Disbursements") with the amount on a
/// line next to the dates. The date used is the Date Paid.
/// </summary>
public static partial class LakeviewPdfReader
{
    public const string CsvHeader = "Date,Description,Total,Principal,Interest,Escrow";

    [GeneratedRegex(@"^(?<due>\d\d/\d\d/\d{4})\s+(?<paid>\d\d/\d\d/\d{4})\s+(?<rest>.*)$")]
    private static partial Regex DatedLine();

    [GeneratedRegex(@"^Payment\s+(?<total>\$[\d,]+\.\d\d)\s+(?<principal>\$[\d,]+\.\d\d)\s+(?<interest>\$[\d,]+\.\d\d)\s+(?<escrow>\$[\d,]+\.\d\d)")]
    private static partial Regex PaymentColumns();

    [GeneratedRegex(@"\$([\d,]+\.\d\d)")]
    private static partial Regex Money();

    public static MortgagePdfResult Read(Stream pdf) => FromLines(PdfTextLines.Read(pdf));

    public static MortgagePdfResult FromLines(IReadOnlyList<string> lines)
    {
        var warnings = new List<string>();
        if (!lines.Any(l => l.Contains("Date Paid", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("This doesn't look like a Lakeview payment history (no \"Date Paid\" column). Check the rows carefully.");
        }

        var rows = new List<(DateOnly Date, string Description, decimal Total, decimal Principal, decimal Interest, decimal Escrow)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var dated = DatedLine().Match(lines[i].Trim());
            if (!dated.Success)
            {
                continue;
            }

            var paid = DateOnly.ParseExact(dated.Groups["paid"].Value, "MM/dd/yyyy", CultureInfo.InvariantCulture);
            var rest = dated.Groups["rest"].Value;
            var payment = PaymentColumns().Match(rest);
            if (payment.Success)
            {
                var total = Parse(payment.Groups["total"].Value);
                var principal = Parse(payment.Groups["principal"].Value);
                var interest = Parse(payment.Groups["interest"].Value);
                var escrow = Parse(payment.Groups["escrow"].Value);
                if (principal + interest + escrow != total)
                {
                    warnings.Add($"{paid:d}: principal, interest, and escrow add up to {principal + interest + escrow:C}, not the {total:C} payment. Check this row.");
                }
                rows.Add((paid, "Payment", total, principal, interest, escrow));
                continue;
            }

            // A disbursement block: description lines just before and after the dated line (which only holds
            // dashes and balances), with the disbursed amount on a following line.
            var before = BlockLines(lines, i, -1);
            var after = BlockLines(lines, i, +1);
            var words = string.Join(' ', before.Append(rest).Concat(after));
            var description =
                words.Contains("Tax", StringComparison.OrdinalIgnoreCase) ? "County and City Tax Disbursement"
                : words.Contains("Insurance", StringComparison.OrdinalIgnoreCase) || words.Contains("Hazard", StringComparison.OrdinalIgnoreCase) ? "Hazard Insurance Disbursement"
                : null;
            var amount = after.Concat(before).Select(l => Money().Match(l)).FirstOrDefault(m => m.Success);
            if (description is null || amount is null)
            {
                warnings.Add($"{paid:d}: couldn't read the row \"{Squash(words)}\"; it was left out.");
                continue;
            }

            var value = Parse(amount.Value);
            rows.Add((paid, description, value, 0m, 0m, value));
        }

        if (rows.Count == 0)
        {
            warnings.Add("No payments were found in this PDF.");
        }

        var csv = new StringBuilder(CsvHeader).Append('\n');
        foreach (var r in rows.OrderBy(r => r.Date))
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{r.Date:MM/dd/yyyy},{r.Description},{r.Total:0.00},{r.Principal:0.00},{r.Interest:0.00},{r.Escrow:0.00}\n");
        }
        return new MortgagePdfResult(csv.ToString(), rows.Count(r => r.Description == "Payment"), rows.Count(r => r.Description != "Payment"), warnings);
    }

    /// <summary>Undated lines next to a dated line, stopping at another dated line or the end of the previous block.</summary>
    private static List<string> BlockLines(IReadOnlyList<string> lines, int index, int direction)
    {
        var block = new List<string>();
        for (var j = index + direction; j >= 0 && j < lines.Count && Math.Abs(j - index) <= 3; j += direction)
        {
            var line = lines[j].Trim();
            if (DatedLine().IsMatch(line) || line.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            // "Disbursements" closes a block, so walking backwards it belongs to the previous one.
            if (direction < 0 && line.Equals("Disbursements", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            block.Add(line);
            if (direction > 0 && line.Equals("Disbursements", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        if (direction < 0)
        {
            block.Reverse();
        }
        return block;
    }

    private static decimal Parse(string money) =>
        decimal.Parse(money.Replace("$", "").Replace(",", ""), CultureInfo.InvariantCulture);

    private static string Squash(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
