namespace PropertyManager.Core.Entities;

public class Property
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Street { get; set; } = "";
    public string City { get; set; } = "Washington";
    public string State { get; set; } = "DC";
    public string? PostalCode { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string? Notes { get; set; }

    /// <summary>The house's photo in <see cref="Storage.IDocumentStorage"/> (a JPEG when it could be converted).</summary>
    public string? PhotoKey { get; set; }
    public string? PhotoContentType { get; set; }

    public List<Unit> Units { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];

    public string FullAddress =>
        $"{Street}, {City}, {State}{(string.IsNullOrWhiteSpace(PostalCode) ? "" : " " + PostalCode)}";
}
