using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class MessageRetentionTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-retention-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly MessageRetentionService service;
    private readonly int tenantId;

    public MessageRetentionTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        storage = new LocalDocumentStorage(root);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        var tenant = new Tenant { FirstName = "Jane", LastName = "Sample" };
        db.Tenants.Add(tenant);
        var folder = new EmailFolder { GraphId = "f", Path = "Tenants", DeltaLink = "delta" };
        db.EmailFolders.Add(folder);
        db.SaveChanges();
        tenantId = tenant.Id;

        TextMessage Text(string guid, DateTimeOffset at) => new() { Guid = guid, ChatIdentifier = "c", ContactHandle = "2025551234", TenantId = tenantId, SentAt = at, Text = guid };
        var later = Text("later", Local(2026, 5, 2, 9));
        later.Attachments.Add(new TextAttachment { Guid = "a", FileName = "IMG.HEIC", Status = TextAttachmentStatus.Saved, StorageKey = storage.SaveAsync(new MemoryStream([1]), "IMG.HEIC").Result, ThumbnailKey = storage.SaveAsync(new MemoryStream([2]), "t.jpg").Result });
        db.TextMessages.AddRange(Text("moveout", Local(2026, 4, 30, 23)), Text("lastday", Local(2026, 5, 1, 22)), later);
        var email = new EmailMessage { GraphId = "e2", EmailFolder = folder, Subject = "Later", FromAddress = "jane@example.com", TenantId = tenantId, SentAt = Local(2026, 6, 1, 9) };
        email.Attachments.Add(new EmailAttachment { FileName = "x.pdf", StorageKey = storage.SaveAsync(new MemoryStream([3]), "x.pdf").Result });
        db.EmailMessages.AddRange(email, new EmailMessage { GraphId = "e1", EmailFolder = folder, Subject = "Deposit", FromAddress = "jane@example.com", TenantId = tenantId, SentAt = Local(2026, 4, 15, 9) });
        db.SaveChanges();
        service = new MessageRetentionService(dbFactory, storage);
    }

    private static DateTimeOffset Local(int y, int m, int d, int h)
    {
        var time = new DateTime(y, m, d, h, 0, 0);
        return new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time));
    }

    public void Dispose()
    {
        connection.Dispose();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Cutoff_IsTheEndOfTheDay()
    {
        var cutoff = MessageRetentionService.Cutoff(new DateOnly(2026, 5, 1));

        Assert.True(Local(2026, 5, 1, 23).AddMinutes(59) < cutoff);
        Assert.True(Local(2026, 5, 2, 0) > cutoff);
    }

    [Fact]
    public void Suggestion_IsLastLeaseEndPlusAMonth_OnlyForFormerTenants()
    {
        var today = new DateOnly(2026, 10, 1);
        var former = new Tenant { Leases = [new Lease { StartDate = new(2024, 1, 1), EndDate = new(2025, 12, 31) }, new Lease { StartDate = new(2026, 1, 1), EndDate = new(2026, 4, 30) }] };
        var current = new Tenant { Leases = [new Lease { StartDate = new(2026, 1, 1), EndDate = null }] };

        Assert.Equal(new DateOnly(2026, 5, 30), MessageRetentionService.Suggest(former, today));
        Assert.Null(MessageRetentionService.Suggest(current, today));
        Assert.Null(MessageRetentionService.Suggest(new Tenant(), today));
    }

    [Fact]
    public async Task Setting_RemovesLaterTextsEmailsAndFiles_ClearingRestoresEmailSync()
    {
        var result = await service.SetKeepUntilAsync(tenantId, null, new DateOnly(2026, 5, 1));

        Assert.Equal(new RetentionResult(1, 1), result);
        await using (var db = dbFactory.CreateDbContext())
        {
            Assert.Equal(["lastday", "moveout"], await db.TextMessages.Select(m => m.Guid).OrderBy(g => g).ToListAsync());
            Assert.Equal(["Deposit"], await db.EmailMessages.Select(m => m.Subject).ToListAsync());
            Assert.Equal(0, await db.TextAttachments.CountAsync());
            Assert.Equal(new DateOnly(2026, 5, 1), (await db.Tenants.SingleAsync()).KeepMessagesUntil);
            Assert.Equal("delta", (await db.EmailFolders.SingleAsync()).DeltaLink);
        }
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));

        Assert.Equal(new RetentionResult(0, 0), await service.SetKeepUntilAsync(tenantId, null, null));

        await using var check = dbFactory.CreateDbContext();
        Assert.Null((await check.Tenants.SingleAsync()).KeepMessagesUntil);
        // Emails skipped while the date was set are fetched again on the next sync.
        Assert.Null((await check.EmailFolders.SingleAsync()).DeltaLink);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
