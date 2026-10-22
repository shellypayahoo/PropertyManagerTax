using System.IO.Compression;
using System.Text;
using System.Xml;
using PropertyManager.Core.Import.Pdf;
using UglyToad.PdfPig;

namespace PropertyManager.Core.Documents;

/// <summary>Reads the text out of uploaded PDF and Word (.docx) files. Other formats have no text to read.</summary>
public static class DocumentTextExtractor
{
    public static bool CanRead(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".pdf" or ".docx";

    /// <summary>
    /// The file's text, or null for formats that can't be read. A PDF's pages are marked "[Page N]" so answers can
    /// point to a page. A scanned PDF with no text layer returns an empty string.
    /// </summary>
    public static string? Extract(Stream content, string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => FromPdf(content),
            ".docx" => FromDocx(content),
            _ => null,
        };

    private static string FromPdf(Stream content)
    {
        using var document = PdfDocument.Open(content);
        var text = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            var lines = PdfTextLines.LinesOf(page.GetWords()).ToList();
            if (lines.Count > 0)
            {
                text.AppendLine($"[Page {page.Number}]");
                lines.ForEach(l => text.AppendLine(l));
            }
        }
        return text.ToString();
    }

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Reads word/document.xml from the .docx package: one line per paragraph, tabs and line breaks kept.</summary>
    private static string FromDocx(Stream content)
    {
        using var zip = new ZipArchive(content, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        var text = new StringBuilder();
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NamespaceURI == WordNamespace && reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                // Moves past </w:t> itself, so don't call Read() again.
                text.Append(reader.ReadElementContentAsString());
                continue;
            }
            if (reader.NamespaceURI == WordNamespace)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab")
                {
                    text.Append('\t');
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "br" or "cr")
                {
                    text.AppendLine();
                }
                else if (reader.LocalName == "p" && (reader.NodeType == XmlNodeType.EndElement || reader.IsEmptyElement))
                {
                    text.AppendLine();
                }
            }
            reader.Read();
        }
        return text.ToString();
    }
}
