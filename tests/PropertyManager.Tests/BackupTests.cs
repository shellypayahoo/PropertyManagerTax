using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Configuration;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;

namespace PropertyManager.Tests;

public sealed class BackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-backup-test-" + Guid.NewGuid().ToString("N"));
    private readonly string backupFolder;
    private readonly FileDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly BackupOptions settings;
    private readonly BackupService service;

    public BackupTests()
    {
        Directory.CreateDirectory(root);
        backupFolder = Path.Combine(root, "cloud", "Property Manager Backups");
        dbFactory = new FileDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "app.db"), Pooling = false }.ToString()).Options);
        using (var db = dbFactory.CreateDbContext())
        {
            db.Database.Migrate();
            db.Vendors.Add(new Vendor { Name = "Ace Plumbing" });
            db.SaveChanges();
        }
        storage = new LocalDocumentStorage(Path.Combine(root, "documents"));
        settings = new BackupOptions { Folder = backupFolder, KeepDays = 7 };
        service = new BackupService(dbFactory, storage, new StaticOptions(settings), new FixedTime(Now));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private Task<string> SaveAsync(string text, string name) => storage.SaveAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), name);

    [Fact]
    public async Task Backs_up_the_database_and_every_document()
    {
        var receipt = await SaveAsync("receipt", "receipt.pdf");
        await SaveAsync("lease", "lease.pdf");
        Assert.False(service.HasBackupForToday);

        var first = await service.RunAsync();

        Assert.Equal((2, 2), (first.DocumentsCopied, first.DocumentsTotal));
        Assert.True(service.HasBackupForToday);
        Assert.Equal("propertymanager-2026-11-11.zip", Path.GetFileName(first.DatabaseCopy));
        Assert.Equal("receipt", File.ReadAllText(Path.Combine(backupFolder, "documents", receipt)));

        // The copy is a real database with the data in it.
        var restored = Path.Combine(root, "restored");
        ZipFile.ExtractToDirectory(first.DatabaseCopy, restored);
        await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(restored, "propertymanager.db"), Pooling = false }.ToString()))
        {
            await copy.OpenAsync();
            var command = copy.CreateCommand();
            command.CommandText = "select Name from Vendors";
            Assert.Equal("Ace Plumbing", await command.ExecuteScalarAsync());
        }

        // Next time only new files are copied.
        await SaveAsync("tax return", "tax.pdf");
        var second = await service.RunAsync();
        Assert.Equal((1, 3), (second.DocumentsCopied, second.DocumentsTotal));

        var status = service.Status();
        Assert.Equal((backupFolder, 1, 3), (status.Folder, status.DatabaseCopies, status.Documents));
        Assert.NotNull(status.LastBackupAt);
    }

    [Fact]
    public async Task A_file_deleted_in_the_app_is_kept_aside_for_a_while()
    {
        var key = await SaveAsync("receipt", "receipt.pdf");
        await service.RunAsync();

        await storage.DeleteAsync(key);
        await service.RunAsync();

        Assert.False(File.Exists(Path.Combine(backupFolder, "documents", key)));
        Assert.True(File.Exists(Path.Combine(backupFolder, "documents-removed", "2026-11-11", key)));
    }

    [Fact]
    public async Task Old_copies_are_removed_but_never_the_newest_three()
    {
        var database = Path.Combine(backupFolder, "database");
        Directory.CreateDirectory(database);
        foreach (var day in new[] { "2026-11-09", "2026-11-01", "2026-10-01", "2026-09-01" })
        {
            File.WriteAllText(Path.Combine(database, $"propertymanager-{day}.zip"), "old");
        }
        Directory.CreateDirectory(Path.Combine(backupFolder, "documents-removed", "2026-10-01"));
        Directory.CreateDirectory(Path.Combine(backupFolder, "documents-removed", "2026-11-09"));

        await service.RunAsync();

        // Keep 7 days: Nov 11 (new) and Nov 9 are recent; Nov 1 stays as one of the newest three.
        Assert.Equal(["propertymanager-2026-11-01.zip", "propertymanager-2026-11-09.zip", "propertymanager-2026-11-11.zip"],
            Directory.EnumerateFiles(database).Select(Path.GetFileName).Order());
        Assert.Equal(["2026-11-09"], Directory.EnumerateDirectories(Path.Combine(backupFolder, "documents-removed")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Bad_or_missing_folders_are_explained()
    {
        Assert.Contains("outside", service.Problem(Path.Combine(storage.RootPath, "backups")));
        Assert.Null(service.Problem(backupFolder));
        Assert.Null(service.Problem("  "));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Backups"), BackupService.Expand("~/Backups"));

        settings.Folder = "";
        Assert.False(service.Status().IsOn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync());
    }

    private sealed class FileDbContextFactory(DbContextOptions<PropertyManagerDbContext> options) : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }

    private sealed class StaticOptions(BackupOptions value) : IOptionsMonitor<BackupOptions>
    {
        public BackupOptions CurrentValue => value;
        public BackupOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BackupOptions, string?> listener) => null;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
