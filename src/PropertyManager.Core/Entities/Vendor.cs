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
}
