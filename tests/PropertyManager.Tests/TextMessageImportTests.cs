using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PropertyManager.Core.Data;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Messaging;

namespace PropertyManager.Tests;

/// <summary>Uses a sample database with the same tables and columns as the Mac's Messages chat.db.</summary>
public sealed class TextMessageImportTests : IDisposable
{
    private const string TenantPhone = "+12025551234";
    private const string NeighborPhone = "+12025559999";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pm-msg-test-" + Guid.NewGuid().ToString("N"));
    private readonly string chatDb;
    private readonly string addressBookFolder;
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbContextFactory dbFactory;
    private readonly TextMessageImportService service;
    private readonly int tenantId;

    public TextMessageImportTests()
    {
        Directory.CreateDirectory(folder);
        chatDb = Path.Combine(folder, "chat.db");
        addressBookFolder = Path.Combine(folder, "AddressBook");
        CreateMessagesDatabase(chatDb);
        CreateAddressBook(Path.Combine(addressBookFolder, "Sources", "ABC", "AddressBook-v22.abcddb"));

        connection.Open();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<PropertyManagerDbContext>().UseSqlite(connection).Options);
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();
        var tenant = new Tenant { FirstName = "Jane", LastName = "Sample", Phone = "(202) 555-1234" };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        tenantId = tenant.Id;
        service = new TextMessageImportService(dbFactory);
    }

    public void Dispose()
    {
        connection.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    private static byte[] AttributedBody(string text)
    {
        // Shape of a real typedstream archive around the string.
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = bytes.Length < 0x80 ? new[] { (byte)bytes.Length } : new byte[] { 0x81, (byte)(bytes.Length & 0xFF), (byte)(bytes.Length >> 8) };
        return [.. "\u0004\u000bstreamtyped\u0081è\u0003\u0084\u0001@\u0084\u0084\u0084\u0012NSAttributedString\u0000\u0084\u0084\u0008NSObject\u0000\u0085\u0092\u0084\u0084\u0084\u0008"u8,
                .. "NSString"u8, 0x01, 0x94, 0x84, 0x01, 0x2B, .. length, .. bytes, 0x86, 0x84, 0x02, 0x69, 0x49];
    }

    private static long AppleNanos(DateTime utc) => (long)(utc - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds * 1_000_000_000;

    private static void CreateMessagesDatabase(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE handle (ROWID INTEGER PRIMARY KEY, id TEXT, service TEXT);
            CREATE TABLE chat (ROWID INTEGER PRIMARY KEY, guid TEXT, chat_identifier TEXT, style INTEGER);
            CREATE TABLE chat_handle_join (chat_id INTEGER, handle_id INTEGER);
            CREATE TABLE message (ROWID INTEGER PRIMARY KEY, guid TEXT, text TEXT, attributedBody BLOB, handle_id INTEGER,
                                  is_from_me INTEGER, date INTEGER, associated_message_type INTEGER, item_type INTEGER);
            CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER, message_date INTEGER);
            CREATE TABLE message_attachment_join (message_id INTEGER, attachment_id INTEGER);
            INSERT INTO handle VALUES (1, '+12025551234', 'iMessage'), (2, '+12025551234', 'SMS'), (3, '+12025559999', 'iMessage');
            INSERT INTO chat VALUES (1, 'iMessage;-;+12025551234', '+12025551234', 45),
                                    (2, 'iMessage;-;+12025559999', '+12025559999', 45),
                                    (3, 'iMessage;+;chat123', 'chat123', 43);
            INSERT INTO chat_handle_join VALUES (1, 1), (2, 3), (3, 1), (3, 3);
            """;
        cmd.ExecuteNonQuery();

        void Message(int id, int chat, string? text, byte[]? body, int handle, bool fromMe, DateTime when, int reaction = 0, int attachments = 0)
        {
            var m = c.CreateCommand();
            m.CommandText = "INSERT INTO message VALUES ($id, $guid, $text, $body, $handle, $me, $date, $reaction, 0); INSERT INTO chat_message_join VALUES ($chat, $id, $date);";
            m.Parameters.AddWithValue("$id", id);
            m.Parameters.AddWithValue("$guid", $"GUID-{id}");
            m.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
            m.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
            m.Parameters.AddWithValue("$handle", handle);
            m.Parameters.AddWithValue("$me", fromMe ? 1 : 0);
            m.Parameters.AddWithValue("$date", AppleNanos(when));
            m.Parameters.AddWithValue("$reaction", reaction);
            m.Parameters.AddWithValue("$chat", chat);
            m.ExecuteNonQuery();
            for (var a = 0; a < attachments; a++)
            {
                var j = c.CreateCommand();
                j.CommandText = $"INSERT INTO message_attachment_join VALUES ({id}, {id * 10 + a})";
                j.ExecuteNonQuery();
            }
        }

        Message(1, 1, "Hi, the kitchen faucet in the basement is leaking", null, 1, false, new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc));
        Message(2, 1, null, AttributedBody("Thanks for letting me know, I'll send a plumber tomorrow"), 0, true, new DateTime(2026, 5, 1, 14, 5, 0, DateTimeKind.Utc));
        Message(3, 1, "Loved “Thanks for letting me know”", null, 1, false, new DateTime(2026, 5, 1, 14, 6, 0, DateTimeKind.Utc), reaction: 2000);
        Message(4, 1, "￼", null, 2, false, new DateTime(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc), attachments: 1);
        Message(5, 2, "Can you move your trash cans?", null, 3, false, new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc));
        Message(6, 3, "Block party Saturday, street closed", null, 3, false, new DateTime(2026, 6, 2, 8, 0, 0, DateTimeKind.Utc));
        Message(7, 3, "Good to know, thanks", null, 1, false, new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc));
    }

    private static void CreateAddressBook(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ZABCDRECORD (Z_PK INTEGER PRIMARY KEY, ZFIRSTNAME TEXT, ZLASTNAME TEXT, ZORGANIZATION TEXT);
            CREATE TABLE ZABCDPHONENUMBER (Z_PK INTEGER PRIMARY KEY, ZOWNER INTEGER, ZFULLNUMBER TEXT);
            CREATE TABLE ZABCDEMAILADDRESS (Z_PK INTEGER PRIMARY KEY, ZOWNER INTEGER, ZADDRESS TEXT);
            INSERT INTO ZABCDRECORD VALUES (1, 'Jane', 'Sample', NULL), (2, 'Ned', 'Neighbor', NULL);
            INSERT INTO ZABCDPHONENUMBER VALUES (1, 1, '(202) 555-1234'), (2, 2, '202.555.9999');
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void DecodesAttributedBody_AndAppleDates()
    {
        Assert.Equal("Thanks for letting me know, I'll send a plumber tomorrow",
            AttributedBodyDecoder.Decode(AttributedBody("Thanks for letting me know, I'll send a plumber tomorrow")));
        var longText = new string('x', 300);
        Assert.Equal(longText, AttributedBodyDecoder.Decode(AttributedBody(longText)));
        Assert.Null(AttributedBodyDecoder.Decode([1, 2, 3]));
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero),
            MessagesDatabaseReader.ToDate(AppleNanos(new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public async Task Scan_ListsContactsWithNames_AndIncludesOnlyTenantsByDefault()
    {
        var summary = await service.ScanContactsAsync(chatDb, addressBookFolder);

        Assert.Equal(new ContactScanSummary(2, 2, 1), summary);
        await using var db = dbFactory.CreateDbContext();
        var contacts = await db.MessageContacts.OrderBy(c => c.Handle).ToListAsync();
        Assert.Equal(("2025551234", "Jane Sample", true, (int?)tenantId), (contacts[0].Handle, contacts[0].Name, contacts[0].Included, contacts[0].TenantId));
        Assert.Equal(("2025559999", "Ned Neighbor", false), (contacts[1].Handle, contacts[1].Name, contacts[1].Included));
        Assert.True(contacts[0].MessageCount >= 4);
    }

    [Fact]
    public async Task Import_OnlyIncludedConversations_SkipsReactions_DecodesText_AndIsRepeatable()
    {
        await service.ScanContactsAsync(chatDb, addressBookFolder);

        var first = await service.ImportAsync(chatDb);
        var again = await service.ImportAsync(chatDb);

        Assert.Equal((2, 5), (first.ChatsMatched, first.MessagesAdded)); // 1:1 with Jane (3 kept) + group chat (2)
        Assert.Equal(0, again.MessagesAdded);
        await using var db = dbFactory.CreateDbContext();
        var messages = await db.TextMessages.OrderBy(m => m.SentAt).ToListAsync();
        Assert.DoesNotContain(messages, m => m.Text.StartsWith("Loved") || m.Text.Contains("trash cans"));
        Assert.Contains(messages, m => m.IsFromMe && m.Text.StartsWith("Thanks for letting me know"));
        Assert.Contains(messages, m => m.Text == "" && m.AttachmentCount == 1);
        // The neighbor's message in the group chat is filed under Jane (the included contact in that chat).
        var party = messages.Single(m => m.Text.StartsWith("Block party"));
        Assert.Equal(("2025551234", (int?)tenantId, true, "+12025559999"), (party.ContactHandle, party.TenantId, party.IsGroupChat, party.SenderHandle));
    }

    [Fact]
    public async Task IncludingAndExcludingAContact_AddsAndRemovesTheirMessages()
    {
        await service.ScanContactsAsync(chatDb, addressBookFolder);
        await service.ImportAsync(chatDb);
        int neighborId;
        await using (var db = dbFactory.CreateDbContext())
        {
            neighborId = (await db.MessageContacts.SingleAsync(c => c.Handle == "2025559999")).Id;
        }

        await service.SetIncludedAsync(neighborId, true);
        Assert.Equal(1, (await service.ImportAsync(chatDb)).MessagesAdded);

        await service.SetIncludedAsync(neighborId, false);
        await using var check = dbFactory.CreateDbContext();
        Assert.Equal(0, await check.TextMessages.CountAsync(m => m.ContactHandle == "2025559999"));
        Assert.Equal(5, await check.TextMessages.CountAsync());
    }

    [Theory]
    [InlineData("+1 (202) 555-1234", "2025551234")]
    [InlineData("202.555.1234", "2025551234")]
    [InlineData("Jane@Example.com", "jane@example.com")]
    [InlineData("12", null)]
    public void NormalizesHandles(string input, string? expected) => Assert.Equal(expected, ContactHandles.Normalize(input));

    private sealed class TestDbContextFactory(DbContextOptions<PropertyManagerDbContext> options)
        : IDbContextFactory<PropertyManagerDbContext>
    {
        public PropertyManagerDbContext CreateDbContext() => new(options);
    }
}
