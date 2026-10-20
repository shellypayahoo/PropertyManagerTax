namespace PropertyManager.Core.Storage;

/// <summary>Stores documents as files in a folder on the local disk.</summary>
public class LocalDocumentStorage : IDocumentStorage
{
    private readonly string rootPath;

    public LocalDocumentStorage(string rootPath)
    {
        this.rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.rootPath);
    }

    public async Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        // Never use the uploaded name on disk; keep only a sanitized extension.
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension.Length > 10 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            extension = "";
        }

        var key = $"{Guid.NewGuid():N}{extension}";
        await using var file = new FileStream(GetPath(key), FileMode.CreateNew, FileAccess.Write);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = GetPath(key);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(GetPath(key));
        return Task.CompletedTask;
    }

    private string GetPath(string key)
    {
        if (string.IsNullOrEmpty(key) || key != Path.GetFileName(key) || key.StartsWith('.'))
        {
            throw new ArgumentException($"Invalid storage key '{key}'.", nameof(key));
        }
        return Path.Combine(rootPath, key);
    }
}
