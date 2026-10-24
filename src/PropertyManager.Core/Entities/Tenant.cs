namespace PropertyManager.Core.Entities;

public class Tenant
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// For someone you no longer rent to or work with: texts and emails with them after this date aren't kept in the
    /// app (they stay on your phone and in your mailbox). Null keeps everything.
    /// </summary>
    public DateOnly? KeepMessagesUntil { get; set; }

    public List<Lease> Leases { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}
