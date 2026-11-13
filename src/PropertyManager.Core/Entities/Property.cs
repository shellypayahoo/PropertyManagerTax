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

    /// <summary>The part of the purchase price that was the land, which is never depreciated. Depreciation is only calculated once this is set.</summary>
    public decimal? LandValue { get; set; }

    /// <summary>Closing costs and other amounts added to what the building cost (title, recording, legal fees, transfer taxes).</summary>
    public decimal? BasisAdditions { get; set; }

    /// <summary>When it was first available to rent (placed in service); the purchase date when not set.</summary>
    public DateOnly? RentalStartDate { get; set; }

    /// <summary>Improvements and equipment depreciated on their own schedules.</summary>
    public List<DepreciableAsset> Assets { get; set; } = [];
    public string? Notes { get; set; }

    /// <summary>The house's photo in <see cref="Storage.IDocumentStorage"/> (a JPEG when it could be converted).</summary>
    public string? PhotoKey { get; set; }
    public string? PhotoContentType { get; set; }

    public List<Unit> Units { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];

    public string FullAddress =>
        $"{Street}, {City}, {State}{(string.IsNullOrWhiteSpace(PostalCode) ? "" : " " + PostalCode)}";
}
