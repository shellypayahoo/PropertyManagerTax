using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class PropertyPhotoServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-house-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly FakeSite site = new();
    private readonly PropertyPhotoService service;
    private readonly int propertyId;

    public PropertyPhotoServiceTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        propertyId = db.Properties.First().Id;
        service = new PropertyPhotoService(new HttpClient(site), dbFactory, new LocalDocumentStorage(root));
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private int FileCount => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public async Task FindImages_ReadsImgSrcsetLazyAndPreviewImages_SkippingIconsAndSvg()
    {
        site.Pages["https://parkerflatsllc.com/"] = ("text/html", """
            <html><head><meta property="og:image" content="https://cdn.example.com/hero-taylor.jpg"></head><body>
            <img src="/static/logo.png"><img src="/images/icon-phone.svg">
            <img src="small-1st.jpg" srcset="/img/1st-480.jpg 480w, /img/1st-1600.jpg 1600w">
            <img data-src="https://cdn.example.com/taylor-front.webp" src="data:image/gif;base64,R0lGOD">
            <div style="background-image: url('/img/street.jpg')"></div>
            </body></html>
            """u8.ToArray());

        var images = await service.FindImagesAsync("parkerflatsllc.com");

        Assert.Equal(
            ["https://cdn.example.com/hero-taylor.jpg", "https://parkerflatsllc.com/img/1st-1600.jpg", "https://cdn.example.com/taylor-front.webp", "https://parkerflatsllc.com/img/street.jpg"],
            images);
    }

    [Fact]
    public async Task UsingAWebPhoto_SavesIt_AndRejectsPagesThatArentPictures()
    {
        site.Pages["https://cdn.example.com/taylor-front.jpg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0]);
        site.Pages["https://parkerflatsllc.com/about"] = ("text/html", "<html></html>"u8.ToArray());

        await service.SetFromUrlAsync(propertyId, "https://cdn.example.com/taylor-front.jpg");

        await using (var db = dbFactory.CreateDbContext())
        {
            var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
            Assert.Equal("image/jpeg", property.PhotoContentType);
            Assert.NotNull(property.PhotoKey);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetFromUrlAsync(propertyId, "https://parkerflatsllc.com/about"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetFromUrlAsync(propertyId, "ftp://example.com/x.jpg"));
    }

    [Fact]
    public async Task Uploading_ReplacesTheOldPhoto_AndRemovingDeletesIt()
    {
        await service.SetFromUploadAsync(propertyId, "front.jpg", new MemoryStream([1, 2]));
        await service.SetFromUploadAsync(propertyId, "IMG_7000.HEIC", new MemoryStream([3, 4]));
        Assert.Equal(1, FileCount);
        await using (var db = dbFactory.CreateDbContext())
        {
            Assert.Equal("image/heic", (await db.Properties.SingleAsync(p => p.Id == propertyId)).PhotoContentType);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetFromUploadAsync(propertyId, "lease.pdf", new MemoryStream([1])));

        await service.RemoveAsync(propertyId);

        Assert.Equal(0, FileCount);
        await using var check = dbFactory.CreateDbContext();
        Assert.Null((await check.Properties.SingleAsync(p => p.Id == propertyId)).PhotoKey);
    }

    private sealed class FakeSite : HttpMessageHandler
    {
        public Dictionary<string, (string Type, byte[] Body)> Pages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var page))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }
            var content = new ByteArrayContent(page.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(page.Type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
