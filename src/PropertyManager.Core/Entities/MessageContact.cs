namespace PropertyManager.Core.Entities;

/// <summary>
/// Someone in the Mac's Messages history, identified by a normalized phone number or email. Only contacts marked
/// <see cref="Included"/> have their conversations imported.
/// </summary>
public class MessageContact
{
    public int Id { get; set; }

    /// <summary>Normalized phone number (digits) or lowercase email; see <see cref="Messaging.ContactHandles"/>.</summary>
    public string Handle { get; set; } = "";

    /// <summary>Name from the Mac's Contacts app, if found.</summary>
    public string? Name { get; set; }

    public bool Included { get; set; }

    /// <summary>The tenant or vendor this contact is, when known (matched by phone/email, or chosen by hand).</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>Size of the conversation history in Messages, from the last scan.</summary>
    public int MessageCount { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
}
