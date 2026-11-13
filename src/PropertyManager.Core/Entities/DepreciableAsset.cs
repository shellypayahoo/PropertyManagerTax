namespace PropertyManager.Core.Entities;

/// <summary>How long something is depreciated over, under the IRS's general system (MACRS GDS).</summary>
public enum AssetLife
{
    /// <summary>Appliances, carpet, furniture in a rental: 5 years.</summary>
    FiveYear = 5,

    /// <summary>Land improvements such as fences, driveways, and landscaping: 15 years.</summary>
    FifteenYear = 15,

    /// <summary>The building, and improvements that are part of it (roof, HVAC system, addition, kitchen remodel): 27.5 years.</summary>
    ResidentialBuilding = 27,
}

public static class AssetLifeExtensions
{
    public static string DisplayName(this AssetLife life) => life switch
    {
        AssetLife.FiveYear => "5 years (appliances, carpet, furniture)",
        AssetLife.FifteenYear => "15 years (fence, driveway, landscaping)",
        _ => "27.5 years (part of the building: roof, HVAC, remodel)",
    };

    public static string ShortName(this AssetLife life) => life == AssetLife.ResidentialBuilding ? "27.5 years" : $"{(int)life} years";
}

/// <summary>
/// Something bought for a property that's deducted over several years rather than all at once: a new roof, a
/// replaced HVAC system, appliances.
/// </summary>
public class DepreciableAsset
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public Property? Property { get; set; }

    public string Description { get; set; } = "";

    /// <summary>When it was ready to use (placed in service).</summary>
    public DateOnly PlacedInService { get; set; }

    public decimal Cost { get; set; }
    public AssetLife Life { get; set; } = AssetLife.ResidentialBuilding;
}
