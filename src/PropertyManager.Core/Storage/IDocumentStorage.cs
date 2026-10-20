namespace PropertyManager.Core.Storage;

/// <summary>
/// Stores uploaded document contents. The database keeps only metadata and the storage key,
/// so this can be backed by the local disk now and by Azure Blob Storage later.
/// </summary>
public interface IDocumentStorage
{
    /// <summary>Saves the stream and returns the key to retrieve it with.</summary>
    Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct = default);

    /// <summary>Opens a stored document for reading, or returns null if it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
