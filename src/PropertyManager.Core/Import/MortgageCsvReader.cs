using System.Security.Cryptography;
using System.Text;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Import;

public enum MortgageRowKind
{
    /// <summary>A monthly payment, broken down into principal, interest, and escrow.</summary>
    Payment,

    /// <summary>The lender paid a property tax bill out of escrow (deductible as Taxes).</summary>
    TaxPayout,

    /// <summary>The lender paid an insurance premium out of escrow (deductible as Insurance).</summary>
    InsurancePayout,

    /// <summary>Anything else, such as a fee, refund, or escrow adjustment.</summary>
    Other,
}

public record MortgageRow(
    int LineNumber, DateOnly Date, string Description,
    decimal Total, decimal Principal, decimal Interest, decimal Escrow,
    MortgageRowKind Kind, string Fingerprint);

public record MortgageCsvResult(IReadOnlyList<string> Headers, IReadOnlyList<MortgageRow> Rows, IReadOnlyList<string> Errors);

/// <summary>Reads a lender's payment-history CSV using a loan account's column mapping.</summary>
public static class MortgageCsvReader
{
    /// <summary>Guesses the column mapping from header names, skipping balance columns such as "Principal Balance".</summary>
    public static BankAccount DetectColumns(IReadOnlyList<string> headers)
    {
        var candidates = headers.Where(h => !h.Contains("balance", StringComparison.OrdinalIgnoreCase)).ToList();
        string? Find(params string[] names) =>
            names.Select(n => candidates.FirstOrDefault(h => h.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(h => h is not null)
            ?? names.Select(n => candidates.FirstOrDefault(h => h.Contains(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(h => h is not null);

        return new BankAccount
        {
            DateColumn = Find("Effective Date", "Transaction Date", "Payment Date", "Posted Date", "Date"),
            DescriptionColumn = Find("Description", "Transaction Type", "Transaction", "Type", "Details"),
            AmountColumn = Find("Total Amount", "Payment Amount", "Total", "Amount"),
            PrincipalColumn = Find("Principal"),
            InterestColumn = Find("Interest"),
            EscrowColumn = Find("Escrow"),
        };
    }

    public static bool IsComplete(BankAccount m) =>
        m.DateColumn is not null && m.PrincipalColumn is not null && m.InterestColumn is not null;

    public static MortgageCsvResult Read(string csvText, BankAccount mapping)
    {
        var rows = CsvParser.Parse(csvText);
        var (headers, dataStart) = BankCsvReader.FindHeader(rows);
        if (!IsComplete(mapping))
        {
            return new MortgageCsvResult(headers, [], ["Choose at least the date, principal, and interest columns."]);
        }

        int Index(string? column) => column is null ? -1 : headers.ToList().FindIndex(h => h.Equals(column, StringComparison.OrdinalIgnoreCase));
        var dateIndex = Index(mapping.DateColumn);
        var descriptionIndex = Index(mapping.DescriptionColumn);
        var totalIndex = Index(mapping.AmountColumn);
        var principalIndex = Index(mapping.PrincipalColumn);
        var interestIndex = Index(mapping.InterestColumn);
        var escrowIndex = Index(mapping.EscrowColumn);
        if (dateIndex < 0 || principalIndex < 0 || interestIndex < 0)
        {
            return new MortgageCsvResult(headers, [], ["This file's columns don't match the saved layout for this loan. Choose the columns again."]);
        }

        var errors = new List<string>();
        var result = new List<MortgageRow>();
        var occurrences = new Dictionary<string, int>();
        for (var i = dataStart; i < rows.Count; i++)
        {
            var row = rows[i];
            string Cell(int index) => index >= 0 && index < row.Length ? row[index] : "";
            var line = i + 1;

            if (!ValueParsers.TryParseDate(Cell(dateIndex), out var date))
            {
                errors.Add($"Line {line}: couldn't read date \"{Cell(dateIndex)}\"; skipped.");
                continue;
            }

            // Lenders differ on signs (payments positive or negative, payouts negative), so use absolute amounts.
            decimal Amount(int index, string name)
            {
                if (index < 0)
                {
                    return 0m;
                }
                if (!ValueParsers.TryParseAmount(Cell(index), out var value))
                {
                    errors.Add($"Line {line}: couldn't read {name} \"{Cell(index)}\"; treated as 0.");
                    return 0m;
                }
                return Math.Abs(value ?? 0m);
            }

            var principal = Amount(principalIndex, "principal");
            var interest = Amount(interestIndex, "interest");
            var escrow = Amount(escrowIndex, "escrow");
            var total = Amount(totalIndex, "total");
            if (total == 0m)
            {
                total = principal + interest + escrow;
            }
            if (total == 0m)
            {
                continue;
            }

            var description = string.Join(' ', Cell(descriptionIndex).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var kind = Classify(description, principal, interest);
            var key = $"{date:yyyy-MM-dd}|{total:0.00}|{principal:0.00}|{interest:0.00}|{escrow:0.00}|{description.ToUpperInvariant()}";
            var occurrence = occurrences[key] = occurrences.GetValueOrDefault(key) + 1;

            result.Add(new MortgageRow(line, date, description, total, principal, interest, escrow, kind, Fingerprint($"{key}|{occurrence}")));
        }

        return new MortgageCsvResult(headers, result, errors);
    }

    private static MortgageRowKind Classify(string description, decimal principal, decimal interest)
    {
        if (principal > 0m || interest > 0m)
        {
            return MortgageRowKind.Payment;
        }

        var text = description.ToLowerInvariant();
        if (text.Contains("tax"))
        {
            return MortgageRowKind.TaxPayout;
        }
        if (text.Contains("insur") || text.Contains("hazard") || text.Contains("homeowner") || text.Contains("flood") || text.Contains("premium"))
        {
            return MortgageRowKind.InsurancePayout;
        }
        return MortgageRowKind.Other;
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
}
