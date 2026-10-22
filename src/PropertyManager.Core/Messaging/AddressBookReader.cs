using Microsoft.Data.Sqlite;

namespace PropertyManager.Core.Messaging;

/// <summary>
/// Reads names from the Mac's Contacts app, keyed by normalized phone number or email, so the contact list can show
/// "Jane Sample" instead of "+12025551234". Contacts keeps one database per account (iCloud, On My Mac, …) under
/// <c>~/Library/Application Support/AddressBook</c>. Best effort: an unreadable database is skipped.
/// </summary>
public static class AddressBookReader
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AddressBook");

    public static IReadOnlyDictionary<string, string> ReadNames(string folder)
    {
        var names = new Dictionary<string, string>();
        if (!Directory.Exists(folder))
        {
            return names;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "AddressBook-v22.abcddb", SearchOption.AllDirectories))
        {
            try
            {
                ReadFile(file, names);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Skip this account's database.
            }
        }
        return names;
    }

    private static void ReadFile(string file, Dictionary<string, string> names)
    {
        // Copy first: Contacts keeps the database open.
        var copy = Path.Combine(Path.GetTempPath(), "pm-contacts-" + Guid.NewGuid().ToString("N") + ".abcddb");
        File.Copy(file, copy);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(file + suffix))
            {
                File.Copy(file + suffix, copy + suffix);
            }
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            foreach (var (table, column) in new[] { ("ZABCDPHONENUMBER", "ZFULLNUMBER"), ("ZABCDEMAILADDRESS", "ZADDRESS") })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT x.{column}, r.ZFIRSTNAME, r.ZLASTNAME, r.ZORGANIZATION
                    FROM {table} x JOIN ZABCDRECORD r ON r.Z_PK = x.ZOWNER
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var handle = ContactHandles.Normalize(reader.IsDBNull(0) ? null : reader.GetString(0));
                    var person = string.Join(' ', new[] { 1, 2 }.Where(i => !reader.IsDBNull(i)).Select(reader.GetString)).Trim();
                    var name = person.Length > 0 ? person : reader.IsDBNull(3) ? "" : reader.GetString(3);
                    if (handle is not null && name.Length > 0)
                    {
                        names.TryAdd(handle, name);
                    }
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" })
            {
                File.Delete(f);
            }
        }
    }
}
