using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PropertyManager.Core.Import.Pdf;

/// <summary>Rebuilds a PDF's text as lines, top to bottom, by grouping words that share a baseline.</summary>
public static class PdfTextLines
{
    private const double SameLineTolerance = 2.5;

    public static IReadOnlyList<string> Read(Stream pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var lines = new List<string>();
        foreach (var page in document.GetPages())
        {
            lines.AddRange(LinesOf(page.GetWords()));
        }
        return lines;
    }

    public static IEnumerable<string> LinesOf(IEnumerable<Word> words)
    {
        var groups = new List<(double Baseline, List<Word> Words)>();
        foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var group = groups.FirstOrDefault(g => Math.Abs(g.Baseline - word.BoundingBox.Bottom) <= SameLineTolerance);
            if (group.Words is null)
            {
                groups.Add((word.BoundingBox.Bottom, [word]));
            }
            else
            {
                group.Words.Add(word);
            }
        }
        return groups.Select(g => string.Join(' ', g.Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
    }
}
