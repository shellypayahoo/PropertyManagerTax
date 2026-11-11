namespace PropertyManager.Core.Storage;

/// <summary>Which stored files may be shown in the browser rather than downloaded.</summary>
public static class SafeFiles
{
    /// <summary>
    /// True for pictures, video, audio, and PDFs. Everything else, including HTML and SVG (which can carry scripts that
    /// would run as part of the app), is only ever downloaded.
    /// </summary>
    public static bool OpensInBrowser(string? contentType)
    {
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return type == "application/pdf"
            || (type.StartsWith("image/", StringComparison.Ordinal) && type != "image/svg+xml")
            || type.StartsWith("video/", StringComparison.Ordinal)
            || type.StartsWith("audio/", StringComparison.Ordinal);
    }
}
