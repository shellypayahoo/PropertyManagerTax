namespace PropertyManager.Core.Entities;

/// <summary>A mail folder (e.g. "Tenants") whose emails are copied into the app.</summary>
public class EmailFolder
{
    public int Id { get; set; }

    /// <summary>The folder's Microsoft Graph id.</summary>
    public string GraphId { get; set; } = "";

    /// <summary>Folder path as shown in Outlook, e.g. "Inbox / Tenants".</summary>
    public string Path { get; set; } = "";

    /// <summary>Microsoft Graph delta link; the next sync asks only for changes since it. Null means sync everything.</summary>
    public string? DeltaLink { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public List<EmailMessage> Messages { get; set; } = [];
}
