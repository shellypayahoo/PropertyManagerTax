using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class LeaseDocumentServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-docs-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LeaseDocumentService service;
    private readonly int leaseId;

    public LeaseDocumentServiceTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        DbSeeder.SeedAsync(db).GetAwaiter().GetResult();
        var lease = new Lease { UnitId = db.Units.First().Id, Tenants = [new Tenant { FirstName = "Jane", LastName = "Sample" }], StartDate = new(2025, 1, 1), MonthlyRent = 4400m };
        db.Leases.Add(lease);
        db.SaveChanges();
        leaseId = lease.Id;
        service = new LeaseDocumentService(dbFactory, new LocalDocumentStorage(root), TimeProvider.System);
    }

    public void Dispose()
    {
        connection.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private static (string, long, Func<Stream>) File(string name, byte[] content) =>
        (name, content.Length, () => new MemoryStream(content));

    [Fact]
    public async Task SavesAllowedFiles_AndReportsRejectedOnes()
    {
        var result = await service.AddAsync(leaseId,
        [
            File("Signed Lease.PDF", new byte[2048]),
            File("photo.heic", new byte[10]),
            File("virus.exe", new byte[10]),
            File("empty.pdf", []),
        ]);

        Assert.Equal(2, result.Saved);
        Assert.Equal(2, result.Problems.Count);
        await using var db = dbFactory.CreateDbContext();
        Assert.Equal(["Signed Lease.PDF", "photo.heic"], await db.LeaseDocuments.OrderBy(d => d.Id).Select(d => d.FileName).ToListAsync());
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Fact]
    public async Task InterruptedUpload_LeavesNoPartialFileOrRecord()
    {
        var result = await service.AddAsync(leaseId, [("lease.pdf", 4096, () => new FailingStream())]);

        Assert.Equal(0, result.Saved);
        Assert.Contains("upload failed", Assert.Single(result.Problems));
        Assert.Empty(Directory.GetFiles(root));
        await using var db = dbFactory.CreateDbContext();
        Assert.Empty(db.LeaseDocuments);
    }

    [Fact]
    public async Task MissingLease_IsReported()
    {
        var result = await service.AddAsync(9999, [File("lease.pdf", new byte[10])]);

        Assert.Equal("That lease no longer exists.", Assert.Single(result.Problems));
    }

    /// <summary>Returns some bytes, then fails like a dropped connection.</summary>
    private sealed class FailingStream : MemoryStream
    {
        private bool sentFirstChunk;

        public FailingStream() : base(new byte[4096]) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (sentFirstChunk)
            {
                throw new IOException("Unexpected end of request content.");
            }
            sentFirstChunk = true;
            buffer.Span[..100].Fill(1);
            return ValueTask.FromResult(100);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
