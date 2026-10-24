using System.Diagnostics;
using System.Globalization;

namespace PropertyManager.Core.Messaging;

/// <summary>Makes JPEG copies of pictures (iPhone photos are HEIC, which only Safari shows) and thumbnails.</summary>
public interface IImageConverter
{
    /// <summary>A JPEG whose longer side is at most <paramref name="maxPixels"/> (never enlarged), or null if the picture can't be converted.</summary>
    Task<byte[]?> ToJpegAsync(string sourcePath, int maxPixels, CancellationToken ct = default);
}

/// <summary>Uses macOS's built-in <c>sips</c> tool. Elsewhere (and if sips fails) returns null.</summary>
public class SipsImageConverter : IImageConverter
{
    public async Task<byte[]?> ToJpegAsync(string sourcePath, int maxPixels, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(sourcePath))
        {
            return null;
        }
        var output = Path.Combine(Path.GetTempPath(), $"pm-img-{Guid.NewGuid():N}.jpg");
        try
        {
            var (ok, info) = await RunAsync(["-g", "pixelWidth", "-g", "pixelHeight", sourcePath], ct);
            if (!ok)
            {
                return null;
            }
            var longest = info.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("pixelWidth:", StringComparison.Ordinal) || l.StartsWith("pixelHeight:", StringComparison.Ordinal))
                .Select(l => int.TryParse(l[(l.IndexOf(':') + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            List<string> args = ["-s", "format", "jpeg", "-s", "formatOptions", "80"];
            if (longest > maxPixels)
            {
                args.AddRange(["-Z", maxPixels.ToString(CultureInfo.InvariantCulture)]);
            }
            args.AddRange([sourcePath, "--out", output]);
            var (converted, _) = await RunAsync(args, ct);
            return converted && File.Exists(output) ? await File.ReadAllBytesAsync(output, ct) : null;
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static async Task<(bool Ok, string Output)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        try
        {
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode == 0, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "");
        }
    }
}
