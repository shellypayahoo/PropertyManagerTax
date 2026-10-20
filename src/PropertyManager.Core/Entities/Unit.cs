namespace PropertyManager.Core.Entities;

public class Unit
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public Property? Property { get; set; }

    /// <summary>Unit label within the property, e.g. "A" or "B".</summary>
    public string Label { get; set; } = "";

    /// <summary>Asking rent for the unit when it is not under lease.</summary>
    public decimal MarketRent { get; set; }

    public int? Bedrooms { get; set; }
    public decimal? Bathrooms { get; set; }
    public int? SquareFeet { get; set; }

    public List<Lease> Leases { get; set; } = [];
    public List<MaintenanceRequest> MaintenanceRequests { get; set; } = [];

    public string DisplayName => Property is null ? $"Unit {Label}" : $"{Property.Name} – Unit {Label}";

    public Lease? ActiveLease(DateOnly asOf) => Leases.FirstOrDefault(l => l.IsActiveOn(asOf));
}
