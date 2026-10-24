namespace PropertyManager.Core.Configuration;

/// <summary>Turns whole parts of the app on or off. Everything is on unless the "Features" section says otherwise.</summary>
public class FeatureOptions
{
    public const string Section = "Features";

    /// <summary>Import texts from the Mac's Messages app (macOS only).</summary>
    public bool TextMessages { get; set; } = true;

    /// <summary>Sync tenant emails from Outlook.com / Hotmail.</summary>
    public bool Email { get; set; } = true;

    /// <summary>Sync Outlook.com calendars (Dashboard appointments, maintenance scheduling).</summary>
    public bool Calendar { get; set; } = true;

    /// <summary>The Ask page (AI assistant).</summary>
    public bool Assistant { get; set; } = true;

    /// <summary>Attach receipts to expenses.</summary>
    public bool Receipts { get; set; } = true;

    /// <summary>A photo for each property.</summary>
    public bool PropertyPhotos { get; set; } = true;
}

/// <summary>Details and rules of the rental business.</summary>
public class BusinessOptions
{
    public const string Section = "Business";

    /// <summary>Shown in the menu and on the P&amp;L PDF when set.</summary>
    public string? Name { get; set; }

    /// <summary>Day of the month rent is due (1–28).</summary>
    public int RentDueDay { get; set; } = 1;

    /// <summary>Grace period before rent counts as late, the Rent collection report's starting choice.</summary>
    public int LateAfterDays { get; set; } = 5;
}

/// <summary>Properties and units created when the app starts with an empty database.</summary>
public class SetupOptions
{
    public const string Section = "Setup";

    public List<PropertySetup> Properties { get; set; } = [];
}

public class PropertySetup
{
    public string Name { get; set; } = "";

    /// <summary>Defaults to the name.</summary>
    public string? Street { get; set; }

    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public List<UnitSetup> Units { get; set; } = [];
}

public class UnitSetup
{
    public string Label { get; set; } = "";
    public decimal MarketRent { get; set; }
}
