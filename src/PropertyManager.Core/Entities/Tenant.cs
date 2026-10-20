namespace PropertyManager.Core.Entities;

public class Tenant
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    public List<Lease> Leases { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}
