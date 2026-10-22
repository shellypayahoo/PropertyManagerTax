namespace PropertyManager.Core.Entities;

/// <summary>
/// A text message imported from the Mac's Messages app, kept only when the conversation includes a tenant or vendor.
/// </summary>
public class TextMessage
{
    public int Id { get; set; }

    /// <summary>Messages' own unique id for the message, used to avoid importing it twice.</summary>
    public string Guid { get; set; } = "";

    /// <summary>The Messages conversation (chat) identifier; group chats share one.</summary>
    public string ChatIdentifier { get; set; } = "";

    public bool IsGroupChat { get; set; }

    /// <summary>The other person's phone number or email for incoming messages; null for messages you sent.</summary>
    public string? SenderHandle { get; set; }

    public bool IsFromMe { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string Text { get; set; } = "";
    public int AttachmentCount { get; set; }

    /// <summary>Normalized handle of the included contact this message is filed under (see <see cref="MessageContact"/>).</summary>
    public string ContactHandle { get; set; } = "";

    /// <summary>The tenant or vendor the conversation is with (for group chats, one of them).</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }
}
