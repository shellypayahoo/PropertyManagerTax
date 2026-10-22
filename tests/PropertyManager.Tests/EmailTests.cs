using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PropertyManager.Core.Assistant;
using PropertyManager.Core.Data;
using PropertyManager.Core.Email;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;
using PropertyManager.Core.Services;
using PropertyManager.Core.Storage;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PropertyManager.Tests;

public sealed class EmailTests : IDisposable
{
    private const string Me = "owner@hotmail.com";
    private const string Tenant = "jane.sample@example.com";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pm-email-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory dbFactory;
    private readonly LocalDocumentStorage storage;
    private readonly FakeMailSource mail = new();
    private readonly EmailSyncService sync;
    private readonly int tenantId;

    public EmailTests()
    {
        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        var tenant = new Tenant { FirstName = "Jane", LastName = "Sample", Email = "Jane.Sample@Example.com" };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        tenantId = tenant.Id;
        storage = new LocalDocumentStorage(root);
        sync = new EmailSyncService(dbFactory, mail, storage, null, Options.Create(new EmailOptions { MyAddresses = [Me] }));
    }

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MailItem Item(string id, string subject, string from, string to, string body, DateTimeOffset sent, bool attachments = false) =>
        new(id, "conv1", subject, new MailAddress(from, from == Me ? "Owner" : "Jane Sample"), [new MailAddress(to, null)], [], sent, body, attachments, false);

    private static byte[] Pdf(string line)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.Letter).AddText(line, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }

    private static readonly DateTimeOffset March2 = new(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Thanks, see you Thursday.\n\nOn Mon, Mar 2, 2026 at 3:00 PM Jane Sample <jane@example.com> wrote:\n> The sink leaks.", "Thanks, see you Thursday.")]
    [InlineData("Sounds good\r\n\r\nFrom: Jane Sample <jane@example.com>\r\nSent: Monday, March 2, 2026 3:00 PM\r\nTo: Owner\r\nSubject: Sink", "Sounds good")]
    [InlineData("Approved.\n-----Original Message-----\nFrom: Jane", "Approved.")]
    [InlineData("Ok\n________________________________\nFrom: Jane Sample", "Ok")]
    [InlineData("On Monday I'll be home.\nThe sink is fixed.", "On Monday I'll be home.\nThe sink is fixed.")]
    [InlineData("> only quoted text", "> only quoted text")]
    public void QuotedReplies_AreTrimmed(string body, string expected) =>
        Assert.Equal(expected, QuotedReplyTrimmer.Trim(body));

    [Fact]
    public async Task Sync_LinksTenants_MarksMyEmails_AndSavesAttachments()
    {
        await sync.SetFoldersAsync([new MailFolderInfo("folder1", "Inbox / Tenants", 2)]);
        mail.Changes["folder1"] = [new MailChanges(
        [
            Item("m1", "Kitchen sink", Tenant, Me, "The kitchen sink is leaking. Lease addendum attached.", March2, attachments: true),
            Item("m2", "RE: Kitchen sink", Me, Tenant, "I'll send the plumber Thursday.\n\nOn Mon, Mar 2 Jane wrote:\n> The kitchen sink is leaking.", March2.AddHours(1)),
        ], [], "delta1")];
        mail.Attachments["m1"] =
        [
            (new MailAttachmentInfo("a1", "Addendum.pdf", "application/pdf", 900, false), Pdf("Tenant may keep one cat.")),
            (new MailAttachmentInfo("a2", "leak.jpg", "image/jpeg", 3, false), [1, 2, 3]),
            (new MailAttachmentInfo("a3", "logo.png", "image/png", 3, true), [1, 2, 3]),
            (new MailAttachmentInfo("a4", "setup.exe", "application/octet-stream", 3, false), [1, 2, 3]),
        ];

        var summary = await sync.SyncAsync();

        Assert.Equal(new EmailSyncSummary(2, 0, 0, 2, []), summary with { Problems = [] });
        await using var db = dbFactory.CreateDbContext();
        var emails = await db.EmailMessages.Include(m => m.Attachments).OrderBy(m => m.GraphId).ToListAsync();
        Assert.All(emails, m => Assert.Equal(tenantId, m.TenantId));
        Assert.False(emails[0].IsFromMe);
        Assert.True(emails[1].IsFromMe);
        Assert.Equal("I'll send the plumber Thursday.", emails[1].Text);
        Assert.Contains("> The kitchen sink is leaking.", emails[1].FullText);
        Assert.Equal(["Addendum.pdf", "leak.jpg"], emails[0].Attachments.Select(a => a.FileName).Order());
        Assert.Contains("one cat", emails[0].Attachments.Single(a => a.FileName == "Addendum.pdf").Text);
        Assert.Null(emails[0].Attachments.Single(a => a.FileName == "leak.jpg").Text);
        Assert.Equal("delta1", (await db.EmailFolders.SingleAsync()).DeltaLink);
    }

    [Fact]
    public async Task LaterSync_AppliesChangesAndRemovals_AndDroppingAFolderDeletesItsFiles()
    {
        await sync.SetFoldersAsync([new MailFolderInfo("folder1", "Tenants", 2)]);
        mail.Changes["folder1"] =
        [
            new MailChanges([Item("m1", "Sink", Tenant, Me, "Leak", March2, attachments: true), Item("m2", "Rent", Tenant, Me, "Paid", March2)], [], "delta1"),
            new MailChanges([Item("m1", "Sink (edited)", Tenant, Me, "Leak", March2, attachments: true)], ["m2", "unknown"], "delta2"),
        ];
        mail.Attachments["m1"] = [(new MailAttachmentInfo("a1", "a.pdf", "application/pdf", 900, false), Pdf("text"))];
        await sync.SyncAsync();

        var second = await sync.SyncAsync();

        Assert.Equal(new EmailSyncSummary(0, 1, 1, 0, []), second with { Problems = [] });
        Assert.Equal(["folder1:null", "folder1:delta1"], mail.Requests);
        await using (var db = dbFactory.CreateDbContext())
        {
            Assert.Equal("Sink (edited)", (await db.EmailMessages.SingleAsync()).Subject);
        }
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));

        await sync.SetFoldersAsync([]);

        await using var check = dbFactory.CreateDbContext();
        Assert.Empty(await check.EmailMessages.ToListAsync());
        Assert.Empty(await check.EmailAttachments.ToListAsync());
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExpiredDeltaLink_ResyncsWithoutDuplicates()
    {
        await sync.SetFoldersAsync([new MailFolderInfo("folder1", "Tenants", 1)]);
        mail.Changes["folder1"] = [new MailChanges([Item("m1", "Sink", Tenant, Me, "Leak", March2)], [], "delta1")];
        await sync.SyncAsync();
        mail.ExpireDeltaLinks = true;
        mail.Changes["folder1"] = [new MailChanges([Item("m1", "Sink", Tenant, Me, "Leak", March2)], [], "delta9")];

        var summary = await sync.SyncAsync();

        Assert.Equal(0, summary.Added);
        await using var db = dbFactory.CreateDbContext();
        Assert.Single(await db.EmailMessages.ToListAsync());
        Assert.Equal("delta9", (await db.EmailFolders.SingleAsync()).DeltaLink);
    }

    [Fact]
    public async Task MessageMovedBetweenSyncedFolders_IsKept()
    {
        await sync.SetFoldersAsync([new MailFolderInfo("a", "A", 1), new MailFolderInfo("b", "B", 0)]);
        mail.Changes["a"] = [new MailChanges([Item("m1", "Sink", Tenant, Me, "Leak", March2)], [], "a1"), new MailChanges([], ["m1"], "a2")];
        mail.Changes["b"] = [new MailChanges([], [], "b1"), new MailChanges([Item("m1", "Sink", Tenant, Me, "Leak", March2)], [], "b2")];
        await sync.SyncAsync();

        await sync.SyncAsync();

        await using var db = dbFactory.CreateDbContext();
        var email = await db.EmailMessages.Include(m => m.EmailFolder).SingleAsync();
        Assert.Equal("B", email.EmailFolder!.Path);
    }

    [Fact]
    public async Task AssistantTools_SearchAndReadEmails_AndTheirAttachments()
    {
        await sync.SetFoldersAsync([new MailFolderInfo("folder1", "Tenants", 2)]);
        mail.Changes["folder1"] = [new MailChanges(
        [
            Item("m1", "Pet request", Tenant, Me, "Can I get a cat? Signed addendum attached.", March2, attachments: true),
            Item("m2", "RE: Pet request", Me, Tenant, "Yes, one cat is fine.\n\nOn Mon Jane wrote:\n> Can I get a cat?", March2.AddDays(1)),
        ], [], "d")];
        mail.Attachments["m1"] = [(new MailAttachmentInfo("a1", "Pet addendum.pdf", "application/pdf", 900, false), Pdf("Pet deposit is $300."))];
        await sync.SyncAsync();
        var tools = new AssistantTools(dbFactory, new TextMessageSearch(dbFactory), new ReportService(dbFactory));

        var byName = await tools.SearchEmailsAsync("cat", contact: "jane");
        var quotedOnly = await tools.SearchEmailsAsync("get");
        var emailId = (await dbFactory.CreateDbContext().EmailMessages.SingleAsync(m => m.GraphId == "m2")).Id;
        var full = await tools.ReadEmailAsync(emailId);
        var documents = await tools.SearchDocumentsAsync("deposit", lease: "jane");

        Assert.Contains("2 email(s)", byName);
        Assert.Contains("From Jane Sample <jane.sample@example.com>", byName);
        Assert.Contains("From Me to jane.sample@example.com", byName);
        Assert.Contains("Subject: Pet request", byName);
        Assert.Contains("Pet addendum.pdf (document E", byName);
        Assert.Contains("1 email(s)", quotedOnly);
        Assert.Contains("> Can I get a cat?", full);
        Assert.Contains("Document E", documents);
        Assert.Contains("Pet deposit is $300.", documents);
        var attachmentId = (await dbFactory.CreateDbContext().EmailAttachments.SingleAsync()).Id;
        Assert.Contains("Pet deposit is $300.", await tools.ReadDocumentAsync($"E{attachmentId}"));
        Assert.Equal("No emails found.", await tools.SearchEmailsAsync("roof"));
    }

    [Fact]
    public async Task Graph_ReadsFoldersRecursively()
    {
        var http = new FakeGraph();
        http.Responses["mailFolders?"] = """{"value":[{"id":"inbox","displayName":"Inbox","childFolderCount":1,"totalItemCount":10}]}""";
        http.Responses["mailFolders/inbox/childFolders"] = """{"value":[{"id":"t","displayName":"Tenants","childFolderCount":0,"totalItemCount":3}]}""";

        var folders = await new GraphMailSource(new HttpClient(http), new FakeToken()).GetFoldersAsync();

        Assert.Equal([new MailFolderInfo("inbox", "Inbox", 10), new MailFolderInfo("t", "Inbox / Tenants", 3)], folders);
        Assert.All(http.Seen, r => Assert.Equal("Bearer token123", r.Headers.Authorization?.ToString()));
    }

    [Fact]
    public async Task Graph_FollowsDeltaPages_AndReadsRemovals()
    {
        var http = new FakeGraph();
        http.Responses["messages/delta?$select"] = """
            {"value":[{"id":"m1","subject":"Sink","from":{"emailAddress":{"name":"Jane","address":"jane@example.com"}},
              "toRecipients":[{"emailAddress":{"name":"Owner","address":"owner@hotmail.com"}}],"ccRecipients":[],
              "sentDateTime":"2026-03-02T14:00:00Z","body":{"contentType":"text","content":"It leaks"},"hasAttachments":true,"isDraft":false}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders/f/messages/delta?$skiptoken=2"}
            """;
        http.Responses["skiptoken=2"] = """{"value":[{"id":"m0","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/f/messages/delta?$deltatoken=3"}""";

        var changes = await new GraphMailSource(new HttpClient(http), new FakeToken()).GetChangesAsync("f", null);

        var m = Assert.Single(changes.Changed);
        Assert.Equal(("m1", "Sink", "jane@example.com", "Jane", "owner@hotmail.com", "It leaks", true), (m.Id, m.Subject, m.From!.Address, m.From.Name, m.To.Single().Address, m.Body, m.HasAttachments));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 14, 0, 0, TimeSpan.Zero), m.SentAt);
        Assert.Equal(["m0"], changes.Removed);
        Assert.EndsWith("deltatoken=3", changes.DeltaLink);
        Assert.Contains("outlook.body-content-type=\"text\"", string.Join(", ", http.Seen[0].Headers.GetValues("Prefer")));
    }

    [Fact]
    public async Task Graph_ExpiredDelta_AndForeignLinks()
    {
        var http = new FakeGraph();
        http.Status["deltatoken=old"] = HttpStatusCode.Gone;
        http.Responses["mailFolders/x/messages/delta"] = """{"value":[],"@odata.nextLink":"https://evil.example.com/steal"}""";
        var graph = new GraphMailSource(new HttpClient(http), new FakeToken());

        await Assert.ThrowsAsync<MailDeltaExpiredException>(() => graph.GetChangesAsync("f", "https://graph.microsoft.com/v1.0/me/mailFolders/f/messages/delta?$deltatoken=old"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.GetChangesAsync("x", null));
        Assert.DoesNotContain(http.Seen, r => r.RequestUri!.Host != "graph.microsoft.com");
    }

    [Fact]
    public async Task Graph_ListsOnlyFileAttachments()
    {
        var http = new FakeGraph();
        http.Responses["/attachments?"] = """
            {"value":[{"@odata.type":"#microsoft.graph.fileAttachment","id":"a1","name":"lease.pdf","contentType":"application/pdf","size":100,"isInline":false},
                      {"@odata.type":"#microsoft.graph.itemAttachment","id":"a2","name":"Fwd","size":5,"isInline":false}]}
            """;
        http.Responses["/attachments/a1/$value"] = "PDFBYTES";
        var graph = new GraphMailSource(new HttpClient(http), new FakeToken());

        var attachments = await graph.GetAttachmentsAsync("m1");
        var bytes = await graph.DownloadAttachmentAsync("m1", "a1");

        Assert.Equal([new MailAttachmentInfo("a1", "lease.pdf", "application/pdf", 100, false)], attachments);
        Assert.Equal("PDFBYTES", Encoding.UTF8.GetString(bytes));
    }

    private sealed class FakeToken : IMailTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("token123");
    }

    /// <summary>Answers each request with the response whose key appears in its URL.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];
        public Dictionary<string, HttpStatusCode> Status { get; } = [];
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (Status.FirstOrDefault(s => url.Contains(s.Key)) is { Key: not null } status)
            {
                return Task.FromResult(new HttpResponseMessage(status.Value) { Content = new StringContent("""{"error":{"message":"gone"}}""") });
            }
            var body = Responses.Where(r => url.Contains(r.Key)).OrderByDescending(r => r.Key.Length).Select(r => r.Value).FirstOrDefault();
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"message":"Resource not found"}}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>Returns queued changes per folder, one batch per sync.</summary>
    private sealed class FakeMailSource : IMailSource
    {
        public Dictionary<string, List<MailChanges>> Changes { get; } = [];
        public Dictionary<string, List<(MailAttachmentInfo Info, byte[] Bytes)>> Attachments { get; } = [];
        public List<string> Requests { get; } = [];
        public bool ExpireDeltaLinks { get; set; }

        public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MailFolderInfo>>([]);

        public Task<MailChanges> GetChangesAsync(string folderId, string? deltaLink, CancellationToken ct = default)
        {
            Requests.Add($"{folderId}:{deltaLink ?? "null"}");
            if (ExpireDeltaLinks && deltaLink is not null)
            {
                throw new MailDeltaExpiredException("expired");
            }
            var queue = Changes[folderId];
            var next = queue[0];
            if (queue.Count > 1)
            {
                queue.RemoveAt(0);
            }
            return Task.FromResult(next);
        }

        public Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(string messageId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MailAttachmentInfo>>(Attachments.GetValueOrDefault(messageId)?.Select(a => a.Info).ToList() ?? []);

        public Task<byte[]> DownloadAttachmentAsync(string messageId, string attachmentId, CancellationToken ct = default) =>
            Task.FromResult(Attachments[messageId].Single(a => a.Info.Id == attachmentId).Bytes);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
