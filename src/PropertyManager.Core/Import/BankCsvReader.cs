using System.Security.Cryptography;
using System.Text;
using PropertyManager.Core.Entities;

namespace PropertyManager.Core.Import;

public record BankRow(int LineNumber, DateOnly Date, string Description, decimal Amount, string Fingerprint);

public record BankCsvResult(IReadOnlyList<string> Headers, IReadOnlyList<BankRow> Rows, IReadOnlyList<string> Errors);

/// <summary>Reads a bank's CSV export using an account's column mapping.</summary>
public static class BankCsvReader
{
    /// <summary>
    /// Finds the header row and returns its column names. Some banks put summary lines above the header;
    /// others (e.g. Wells Fargo) have no header at all, in which case columns are named "Column 1", "Column 2", ….
    /// </summary>
    public static (IReadOnlyList<string> Headers, int DataStart) FindHeader(List<string[]> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Length >= 3 && row.Any(c => c.Contains("date", StringComparison.OrdinalIgnoreCase)))
            {
                return (row, i + 1);
            }
            if (row.Length >= 3 && row.Any(c => ValueParsers.TryParseDate(c, out _)))
            {
                var width = rows.Skip(i).Max(r => r.Length);
                return (Enumerable.Range(1, width).Select(n => $"Column {n}").ToList(), i);
            }
        }
        return ([], rows.Count);
    }

    /// <summary>Guesses the column mapping from header names used by common US banks.</summary>
    public static BankAccount DetectColumns(IReadOnlyList<string> headers, List<string[]> rows, int dataStart)
    {
        string? Find(params string[] candidates) =>
            candidates.Select(c => headers.FirstOrDefault(h => h.Equals(c, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(h => h is not null)
            ?? candidates.Select(c => headers.FirstOrDefault(h => h.Contains(c, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(h => h is not null);

        var mapping = new BankAccount
        {
            DateColumn = Find("Posting Date", "Transaction Date", "Date", "Posted Date"),
            DescriptionColumn = Find("Description", "Payee", "Name", "Memo", "Details"),
            AmountColumn = Find("Amount"),
            DebitColumn = Find("Debit", "Withdrawal", "Withdrawals"),
            CreditColumn = Find("Credit", "Deposit", "Deposits"),
        };
        if (mapping.DebitColumn is not null && mapping.CreditColumn is not null && mapping.AmountColumn is null)
        {
            return mapping;
        }
        mapping.DebitColumn = mapping.CreditColumn = null;

        if (headers.Count > 0 && headers[0] == "Column 1")
        {
            // Headerless: pick the first column holding dates, the first holding amounts, and the longest text column.
            var sample = rows.Skip(dataStart).Take(20).ToList();
            int? ColumnWhere(Func<string, bool> test) =>
                Enumerable.Range(0, headers.Count).Cast<int?>().FirstOrDefault(i => sample.All(r => i < r.Length && test(r[i!.Value])));

            var dateIndex = ColumnWhere(v => ValueParsers.TryParseDate(v, out _));
            var amountIndex = ColumnWhere(v => v.Length > 0 && ValueParsers.TryParseAmount(v, out var a) && a is not null && !ValueParsers.TryParseDate(v, out _));
            var textIndex = Enumerable.Range(0, headers.Count)
                .Where(i => i != dateIndex && i != amountIndex)
                .OrderByDescending(i => sample.Sum(r => i < r.Length ? r[i].Length : 0))
                .Cast<int?>().FirstOrDefault();

            mapping.DateColumn = dateIndex is { } d ? headers[d] : null;
            mapping.AmountColumn = amountIndex is { } a ? headers[a] : null;
            mapping.DescriptionColumn = textIndex is { } t ? headers[t] : null;
        }
        return mapping;
    }

    public static bool IsComplete(BankAccount m) =>
        m.DateColumn is not null && m.DescriptionColumn is not null
        && (m.AmountColumn is not null || (m.DebitColumn is not null && m.CreditColumn is not null));

    public static BankCsvResult Read(string csvText, BankAccount mapping)
    {
        var rows = CsvParser.Parse(csvText);
        var (headers, dataStart) = FindHeader(rows);
        var errors = new List<string>();
        var result = new List<BankRow>();
        if (!IsComplete(mapping))
        {
            return new BankCsvResult(headers, result, ["Choose the date, description, and amount columns."]);
        }

        int Index(string? column) => column is null ? -1 : headers.ToList().FindIndex(h => h.Equals(column, StringComparison.OrdinalIgnoreCase));
        var dateIndex = Index(mapping.DateColumn);
        var descriptionIndex = Index(mapping.DescriptionColumn);
        var amountIndex = Index(mapping.AmountColumn);
        var debitIndex = Index(mapping.DebitColumn);
        var creditIndex = Index(mapping.CreditColumn);
        if (dateIndex < 0 || descriptionIndex < 0 || (amountIndex < 0 && (debitIndex < 0 || creditIndex < 0)))
        {
            return new BankCsvResult(headers, result, ["This file's columns don't match the saved layout for this account. Choose the columns again."]);
        }

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

            decimal? amount;
            if (amountIndex >= 0)
            {
                if (!ValueParsers.TryParseAmount(Cell(amountIndex), out amount))
                {
                    errors.Add($"Line {line}: couldn't read amount \"{Cell(amountIndex)}\"; skipped.");
                    continue;
                }
                if (amount is null)
                {
                    // Balance lines such as Bank of America's "Beginning balance" have no amount.
                    continue;
                }
            }
            else
            {
                if (!ValueParsers.TryParseAmount(Cell(debitIndex), out var debit) || !ValueParsers.TryParseAmount(Cell(creditIndex), out var credit))
                {
                    errors.Add($"Line {line}: couldn't read debit/credit; skipped.");
                    continue;
                }
                if (debit is null && credit is null)
                {
                    continue;
                }
                amount = (credit ?? 0m) - Math.Abs(debit ?? 0m);
            }

            var description = string.Join(' ', Cell(descriptionIndex).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var key = $"{date:yyyy-MM-dd}|{amount.Value:0.00}|{description.ToUpperInvariant()}";
            var occurrence = occurrences[key] = occurrences.GetValueOrDefault(key) + 1;

            result.Add(new BankRow(line, date, description, amount.Value, Fingerprint($"{key}|{occurrence}")));
        }

        return new BankCsvResult(headers, result, errors);
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
}
