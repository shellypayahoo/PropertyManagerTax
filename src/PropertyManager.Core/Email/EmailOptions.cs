namespace PropertyManager.Core.Email;

/// <summary>Settings for signing in to Outlook.com / Hotmail (section "Email"): the Azure app registration's client id and tenant.</summary>
public class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Application (client) id of the app registration in the Azure portal. Not a secret.</summary>
    public string? ClientId { get; set; }

    /// <summary>"consumers" for Hotmail/Outlook.com accounts; "common" also allows work accounts.</summary>
    public string Tenant { get; set; } = "consumers";

    /// <summary>Other addresses of yours (aliases), so emails from them count as sent by you.</summary>
    public List<string> MyAddresses { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !ClientId.Contains('<');
}
