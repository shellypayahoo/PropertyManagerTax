using System.Text;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class LocalDocumentStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-docs-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDocumentStorage storage;

    public LocalDocumentStorageTests() => storage = new LocalDocumentStorage(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task SaveOpenDelete_RoundTrips()
    {
        var key = await storage.SaveAsync(new MemoryStream("lease text"u8.ToArray()), "Smith Lease 2024.PDF");

        Assert.EndsWith(".pdf", key);
        Assert.DoesNotContain("Smith", key);

        await using (var stream = await storage.OpenReadAsync(key))
        {
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!, Encoding.UTF8);
            Assert.Equal("lease text", await reader.ReadToEndAsync());
        }

        await storage.DeleteAsync(key);
        Assert.Null(await storage.OpenReadAsync(key));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("sub/file.pdf")]
    [InlineData("")]
    public async Task OpenRead_RejectsKeysOutsideRoot(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(key));
    }

    [Fact]
    public async Task Save_DropsUnsafeExtension()
    {
        var key = await storage.SaveAsync(new MemoryStream([1, 2, 3]), "evil.p/../x");

        Assert.Equal(32, key.Length);
        Assert.True(File.Exists(Path.Combine(root, key)));
    }
}
