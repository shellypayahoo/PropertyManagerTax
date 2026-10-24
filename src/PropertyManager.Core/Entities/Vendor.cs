namespace PropertyManager.Core.Entities;

public class Vendor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>e.g. Plumber, Electrician, HVAC, General contractor.</summary>
    public string? Trade { get; set; }

    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// For someone you no longer rent to or work with: texts and emails with them after this date aren't kept in the
    /// app (they stay on your phone and in your mailbox). Null keeps everything.
    /// </summary>
    public DateOnly? KeepMessagesUntil { get; set; }
}
