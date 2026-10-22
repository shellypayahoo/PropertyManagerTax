using Microsoft.Data.Sqlite;

namespace PropertyManager.Core.Messaging;

public record MessagesChat(long Id, string Identifier, bool IsGroup, IReadOnlyList<string> Participants);

/// <summary>How much someone appears in Messages: messages in conversations they're part of, and the latest one.</summary>
public record MessagesHandleStats(string Handle, int MessageCount, DateTimeOffset LastMessageAt);

public record MessagesRow(
    long RowId, string Guid, long ChatId, string? SenderHandle, bool IsFromMe, DateTimeOffset SentAt, string Text, int AttachmentCount);

/// <summary>
/// Reads conversations from a copy of the Messages database (<c>~/Library/Messages/chat.db</c>). Never opens the live
/// file: Messages keeps it open and recent messages may still be in the write-ahead log, so the database and its
/// -wal/-shm files are copied to a temporary folder first.
/// </summary>
public sealed class MessagesDatabaseReader : IDisposable
{
    // Messages stores dates from 2001-01-01, in nanoseconds on modern macOS (seconds on very old versions).
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string copyFolder;
    private readonly SqliteConnection connection;

    private MessagesDatabaseReader(string copyFolder, SqliteConnection connection)
    {
        this.copyFolder = copyFolder;
        this.connection = connection;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Messages", "chat.db");

    /// <exception cref="UnauthorizedAccessException">macOS denied access (Full Disk Access not granted).</exception>
    /// <exception cref="FileNotFoundException">No Messages database at the path.</exception>
    public static MessagesDatabaseReader Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No Messages database was found.", path);
        }

        var folder = Path.Combine(Path.GetTempPath(), "pm-messages-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, "chat.db");
        File.Copy(path, copy);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Copy(path + suffix, copy + suffix);
            }
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
        connection.Open();
        return new MessagesDatabaseReader(folder, connection);
    }

    public IReadOnlyList<MessagesChat> ReadChats()
    {
        var participants = new Dictionary<long, List<string>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT chj.chat_id, h.id FROM chat_handle_join chj JOIN handle h ON h.ROWID = chj.handle_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var chatId = reader.GetInt64(0);
                if (!participants.TryGetValue(chatId, out var list))
                {
                    participants[chatId] = list = [];
                }
                list.Add(reader.GetString(1));
            }
        }

        var chats = new List<MessagesChat>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ROWID, chat_identifier, style FROM chat";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var people = participants.GetValueOrDefault(id) ?? [];
                // style 43 is a group chat; 45 is one-to-one.
                var isGroup = (!reader.IsDBNull(2) && reader.GetInt32(2) == 43) || people.Count > 1;
                chats.Add(new MessagesChat(id, reader.IsDBNull(1) ? "" : reader.GetString(1), isGroup, people));
            }
        }
        return chats;
    }

    /// <summary>Message counts per handle (phone/email), without reading any message text.</summary>
    public IReadOnlyList<MessagesHandleStats> ReadHandleStats()
    {
        var stats = new List<MessagesHandleStats>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, COUNT(m.ROWID), MAX(m.date)
            FROM handle h
            JOIN chat_handle_join chj ON chj.handle_id = h.ROWID
            JOIN chat_message_join cmj ON cmj.chat_id = chj.chat_id
            JOIN message m ON m.ROWID = cmj.message_id
            WHERE COALESCE(m.associated_message_type, 0) = 0 AND COALESCE(m.item_type, 0) = 0
            GROUP BY h.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            stats.Add(new MessagesHandleStats(reader.GetString(0), reader.GetInt32(1), ToDate(reader.IsDBNull(2) ? 0 : reader.GetInt64(2))));
        }
        return stats;
    }

    /// <summary>Messages in the given chats, skipping tapback reactions and group events (renames, joins).</summary>
    public IReadOnlyList<MessagesRow> ReadMessages(IReadOnlyCollection<long> chatIds)
    {
        var rows = new List<MessagesRow>();
        if (chatIds.Count == 0)
        {
            return rows;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.ROWID, m.guid, m.text, m.attributedBody, h.id, m.is_from_me, m.date, cmj.chat_id,
                   (SELECT COUNT(*) FROM message_attachment_join maj WHERE maj.message_id = m.ROWID)
            FROM message m
            JOIN chat_message_join cmj ON cmj.message_id = m.ROWID
            LEFT JOIN handle h ON h.ROWID = m.handle_id
            WHERE cmj.chat_id IN ({string.Join(',', chatIds)})
              AND COALESCE(m.associated_message_type, 0) = 0
              AND COALESCE(m.item_type, 0) = 0
            ORDER BY m.date
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrEmpty(text) && !reader.IsDBNull(3))
            {
                text = AttributedBodyDecoder.Decode((byte[])reader.GetValue(3));
            }
            // U+FFFC is the placeholder Messages puts in the text where an attachment goes.
            text = (text ?? "").Replace("￼", "").Trim();

            var attachments = reader.GetInt32(8);
            if (text.Length == 0 && attachments == 0)
            {
                continue;
            }

            rows.Add(new MessagesRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(7),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5) == 1,
                ToDate(reader.IsDBNull(6) ? 0 : reader.GetInt64(6)),
                text,
                attachments));
        }
        return rows;
    }

    public static DateTimeOffset ToDate(long appleTime) =>
        appleTime > 1_000_000_000_000 ? AppleEpoch.AddTicks(appleTime / 100) : AppleEpoch.AddSeconds(appleTime);

    public void Dispose()
    {
        connection.Dispose();
        try
        {
            Directory.Delete(copyFolder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the temp folder is cleaned up by the OS eventually.
        }
    }
}
