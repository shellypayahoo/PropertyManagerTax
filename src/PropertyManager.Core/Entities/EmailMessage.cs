namespace PropertyManager.Core.Entities;

/// <summary>An email copied from a synced mail folder.</summary>
public class EmailMessage
{
    public int Id { get; set; }
    public int EmailFolderId { get; set; }
    public EmailFolder? EmailFolder { get; set; }

    /// <summary>The message's Microsoft Graph id (unique within the mailbox).</summary>
    public string GraphId { get; set; } = "";

    public string? ConversationId { get; set; }
    public string Subject { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }

    /// <summary>Recipients as "Name &lt;address&gt;", separated by "; ".</summary>
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";

    public DateTimeOffset SentAt { get; set; }
    public bool IsFromMe { get; set; }

    /// <summary>The new part of the message, with the quoted earlier conversation removed.</summary>
    public string Text { get; set; } = "";

    /// <summary>The whole plain-text body, including quoted replies.</summary>
    public string FullText { get; set; } = "";

    /// <summary>The tenant or vendor the email is with, matched by email address.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public List<EmailAttachment> Attachments { get; set; } = [];

    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : $"{FromName} <{FromAddress}>";
}
