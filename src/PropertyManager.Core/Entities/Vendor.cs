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
    /// No 1099 is needed for this vendor whatever the amount: a corporation, a store, a utility, a lender, an insurer.
    /// </summary>
    public bool Exempt1099 { get; set; }

    /// <summary>The owner has this vendor's W-9 (name, address, and tax ID for the 1099).</summary>
    public bool HasW9 { get; set; }

    /// <summary>
    /// For someone you no longer rent to or work with: texts and emails with them after this date aren't kept in the
    /// app (they stay on your phone and in your mailbox). Null keeps everything.
    /// </summary>
    public DateOnly? KeepMessagesUntil { get; set; }
}
