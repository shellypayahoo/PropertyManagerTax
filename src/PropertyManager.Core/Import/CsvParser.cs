using System.Text;

namespace PropertyManager.Core.Import;

/// <summary>Minimal RFC 4180 CSV parser: quoted fields, escaped quotes, and newlines inside quotes.</summary>
public static class CsvParser
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                case '﻿' when i == 0:
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        EndRow();
        return rows;

        void EndRow()
        {
            fields.Add(field.ToString());
            field.Clear();
            if (fields.Any(f => f.Trim().Length > 0))
            {
                rows.Add(fields.Select(f => f.Trim()).ToArray());
            }
            fields.Clear();
        }
    }
}
