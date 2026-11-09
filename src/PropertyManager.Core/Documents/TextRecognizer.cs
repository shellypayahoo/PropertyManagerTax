using System.Diagnostics;
using System.Globalization;

namespace PropertyManager.Core.Documents;

/// <summary>Reads the text in pictures and scanned PDFs (OCR). Runs on this computer; nothing is sent anywhere.</summary>
public interface ITextRecognizer
{
    /// <summary>The recognized text (PDF pages marked "[Page N]"), or null when there's none or it couldn't be read.</summary>
    Task<string?> ReadAsync(byte[] content, string fileName, CancellationToken ct = default);
}

/// <summary>
/// Uses the text recognition built into macOS (the Vision framework behind Live Text), through <c>osascript</c>, so
/// nothing needs installing. Elsewhere, or if it fails, returns null and the file simply has no text.
/// </summary>
public class MacTextRecognizer : ITextRecognizer
{
    public const int MaxPages = 50;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff",
    };

    public static bool CanRead(string fileName) => Supported.Contains(Path.GetExtension(fileName));

    public async Task<string?> ReadAsync(byte[] content, string fileName, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || !CanRead(fileName) || content.Length == 0)
        {
            return null;
        }
        var input = Path.Combine(Path.GetTempPath(), $"pm-ocr-{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}");
        var script = Path.Combine(Path.GetTempPath(), $"pm-ocr-{Guid.NewGuid():N}.js");
        try
        {
            await File.WriteAllBytesAsync(input, content, ct);
            await File.WriteAllTextAsync(script, Script, ct);
            var start = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var arg in new[] { "-l", "JavaScript", script, input, MaxPages.ToString(CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(arg);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var text = (await output).Trim();
                return process.ExitCode == 0 && text.Length > 0 ? text : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            File.Delete(input);
            File.Delete(script);
        }
    }

    // JavaScript for Automation: renders each PDF page (or opens the picture), runs Vision's accurate text
    // recognition, and prints the lines top to bottom, left to right.
    private const string Script = """
        ObjC.import('Foundation');
        ObjC.import('AppKit');
        ObjC.import('PDFKit');
        ObjC.import('Vision');

        function recognize(handler) {
            const request = $.VNRecognizeTextRequest.alloc.init;
            request.recognitionLevel = 0; // accurate
            request.usesLanguageCorrection = true;
            if (!handler.performRequestsError($([request]), null)) return '';
            const results = request.results;
            const lines = [];
            for (let i = 0; i < results.count; i++) {
                const observation = results.objectAtIndex(i);
                const best = observation.topCandidates(1);
                if (best.count === 0) continue;
                const box = observation.boundingBox;
                lines.push({ text: best.objectAtIndex(0).string.js, y: box.origin.y + box.size.height / 2, x: box.origin.x });
            }
            // Vision's coordinates start at the bottom left; lines within 1% of the page height count as one row.
            lines.sort((a, b) => Math.abs(a.y - b.y) < 0.01 ? a.x - b.x : b.y - a.y);
            return lines.map(l => l.text).join('\n');
        }

        function run(argv) {
            const path = argv[0];
            const url = $.NSURL.fileURLWithPath(path);
            if (!path.toLowerCase().endsWith('.pdf')) {
                return recognize($.VNImageRequestHandler.alloc.initWithURLOptions(url, $({})));
            }
            const pdf = $.PDFDocument.alloc.initWithURL(url);
            if (pdf.isNil()) return '';
            const pages = Math.min(pdf.pageCount, Number(argv[1]));
            const out = [];
            for (let i = 0; i < pages; i++) {
                const page = pdf.pageAtIndex(i);
                const bounds = page.boundsForBox(0); // media box
                // About 144 dpi: sharp enough for small print on tax forms.
                const image = page.thumbnailOfSizeForBox({ width: bounds.size.width * 2, height: bounds.size.height * 2 }, 0);
                const text = recognize($.VNImageRequestHandler.alloc.initWithDataOptions(image.TIFFRepresentation, $({})));
                if (text.length > 0) out.push('[Page ' + (i + 1) + ']\n' + text);
            }
            return out.join('\n');
        }
        """;
}

/// <summary>Reads a file's text: PDF and Word text directly, and pictures or scanned PDFs with text recognition.</summary>
public static class DocumentReader
{
    /// <summary>True when the text has words in it, not just page markers or blank space.</summary>
    public static bool HasWords(string? text) =>
        !string.IsNullOrWhiteSpace(text) && System.Text.RegularExpressions.Regex.Replace(text, @"\[Page \d+\]", "").Trim().Length > 0;

    /// <summary>The file's text, or null when it has none (or couldn't be read). Damaged files just have no text.</summary>
    public static async Task<string?> ReadAsync(byte[] content, string fileName, ITextRecognizer? recognizer, CancellationToken ct = default)
    {
        string? text = null;
        if (DocumentTextExtractor.CanRead(fileName))
        {
            try
            {
                text = DocumentTextExtractor.Extract(new MemoryStream(content), fileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Damaged or password-protected: try recognition below, else no text.
            }
        }
        if (!HasWords(text) && recognizer is not null && MacTextRecognizer.CanRead(fileName))
        {
            text = await recognizer.ReadAsync(content, fileName, ct);
        }
        return HasWords(text) ? text : null;
    }
}
