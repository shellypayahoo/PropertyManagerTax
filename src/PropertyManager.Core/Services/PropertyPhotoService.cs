using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Storage;

namespace PropertyManager.Core.Services;

/// <summary>
/// A photo for each property: uploaded, or downloaded from a web page such as the LLC's website. Pictures are saved
/// as JPEGs up to 1600 pixels when they can be converted (iPhone photos are HEIC).
/// </summary>
public partial class PropertyPhotoService(HttpClient http, IDbContextFactory<PropertyManagerDbContext> dbFactory, IDocumentStorage storage, IImageConverter? images = null)
{
    public const long MaxBytes = 15 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".heif"] = "image/heif",
    };

    /// <summary>Saves an uploaded picture as the property's photo, replacing any earlier one.</summary>
    public async Task SetFromUploadAsync(int propertyId, string fileName, Stream content, CancellationToken ct = default)
    {
        if (!Types.TryGetValue(Path.GetExtension(fileName), out var type))
        {
            throw new InvalidDataException("Choose a photo (JPG, PNG, HEIC, GIF, or WebP).");
        }
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        if (copy.Length == 0 || copy.Length > MaxBytes)
        {
            throw new InvalidDataException(copy.Length == 0 ? "The file is empty." : $"The photo is larger than {MaxBytes / 1024 / 1024} MB.");
        }
        await SaveAsync(propertyId, copy.ToArray(), Path.GetFileName(fileName), type, ct);
    }

    /// <summary>Downloads a picture from the web and saves it as the property's photo.</summary>
    public async Task SetFromUrlAsync(int propertyId, string imageUrl, CancellationToken ct = default)
    {
        var uri = WebAddress(imageUrl);
        using var response = await http.SendAsync(Request(uri), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The website answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || type.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("That address isn't a photo. Use Find photos on the page, or copy the picture's own address.");
        }
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new InvalidDataException($"The photo is larger than {MaxBytes / 1024 / 1024} MB.");
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException($"The photo is larger than {MaxBytes / 1024 / 1024} MB.");
        }
        var name = Path.GetFileName(uri.AbsolutePath) is { Length: > 0 } n ? n : "photo";
        await SaveAsync(propertyId, bytes, name, type.ToLowerInvariant(), ct);
    }

    /// <summary>
    /// The pictures on a web page, largest-looking first: &lt;img&gt; sources (including lazy-loaded and srcset ones),
    /// the page's preview image, and CSS background images. Icons and SVG logos are skipped.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindImagesAsync(string pageUrl, CancellationToken ct = default)
    {
        var page = WebAddress(pageUrl);
        using var response = await http.SendAsync(Request(page), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The website answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return [page.AbsoluteUri];
        }
        var html = await response.Content.ReadAsStringAsync(ct);
        var baseUri = response.RequestMessage?.RequestUri ?? page;

        var found = new List<string>();
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(candidate.Trim()), out var uri) && uri.Scheme is "http" or "https"
                && !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                && !IconName().IsMatch(uri.AbsolutePath) && !found.Contains(uri.AbsoluteUri))
            {
                found.Add(uri.AbsoluteUri);
            }
        }

        foreach (Match m in MetaImage().Matches(html))
        {
            Add(m.Groups["url"].Value);
        }
        foreach (Match tag in ImgTag().Matches(html))
        {
            // Prefer the largest srcset entry, then lazy-load attributes, then src.
            var srcset = Attribute(tag.Value, "srcset") ?? Attribute(tag.Value, "data-srcset");
            var largest = srcset?.Split(',').Select(e => e.Trim().Split(' ')).Where(p => p.Length > 0 && p[0].Length > 0)
                .OrderByDescending(p => p.Length > 1 && int.TryParse(p[1].TrimEnd('w', 'x'), out var w) ? w : 0)
                .Select(p => p[0]).FirstOrDefault();
            Add(largest ?? Attribute(tag.Value, "data-src") ?? Attribute(tag.Value, "data-image") ?? Attribute(tag.Value, "src"));
        }
        foreach (Match m in BackgroundImage().Matches(html))
        {
            Add(m.Groups["url"].Value);
        }
        return found.Take(60).ToList();
    }

    public async Task RemoveAsync(int propertyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var property = await db.Properties.SingleAsync(p => p.Id == propertyId, ct);
        if (property.PhotoKey is { } old)
        {
            await storage.DeleteAsync(old, ct);
        }
        property.PhotoKey = property.PhotoContentType = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveAsync(int propertyId, byte[] bytes, string fileName, string type, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var property = await db.Properties.SingleAsync(p => p.Id == propertyId, ct);

        var jpeg = await ToJpegAsync(bytes, fileName, ct);
        var key = await storage.SaveAsync(new MemoryStream(jpeg ?? bytes), jpeg is null ? fileName : Path.ChangeExtension(fileName, ".jpg"), ct);
        var old = property.PhotoKey;
        property.PhotoKey = key;
        property.PhotoContentType = jpeg is null ? type : "image/jpeg";
        await db.SaveChangesAsync(ct);
        if (old is not null)
        {
            await storage.DeleteAsync(old, ct);
        }
    }

    private async Task<byte[]?> ToJpegAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        if (images is null)
        {
            return null;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"pm-house-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            return await images.ToJpegAsync(temp, TextMessageImportService.PreviewPixels, ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static Uri WebAddress(string address)
    {
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidDataException("Enter a web address, like parkerflatsllc.com.");
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Some sites refuse requests that don't look like a browser.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(PropertyManager)"));
        request.Headers.Accept.ParseAdd("text/html,image/*;q=0.9,*/*;q=0.8");
        return request;
    }

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\s{Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase);
        return m.Success && m.Groups["v"].Value.Length > 0 ? m.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](?:og:image|twitter:image)[""'][^>]*content\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaImage();

    [GeneratedRegex(@"background(?:-image)?\s*:[^;""]*url\(\s*['""]?(?<url>[^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundImage();

    [GeneratedRegex(@"(favicon|sprite|icon|logo|pixel|spacer)[^/]*$", RegexOptions.IgnoreCase)]
    private static partial Regex IconName();
}
