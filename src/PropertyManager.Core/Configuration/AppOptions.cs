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

    /// <summary>Read new vendor texts and emails with the Ask page's AI model and suggest expenses to approve.</summary>
    public bool ExpenseSuggestions { get; set; } = true;
}

/// <summary>Automatic daily backups of the database and documents.</summary>
public class BackupOptions
{
    public const string Section = "Backup";
    public static readonly int[] KeepChoices = [7, 14, 30, 90];

    /// <summary>Where backups go, e.g. a folder in iCloud Drive or OneDrive. Blank turns backups off.</summary>
    public string? Folder { get; set; }

    /// <summary>How many days of database copies (and of files since deleted in the app) to keep.</summary>
    public int KeepDays { get; set; } = 30;
}

/// <summary>How often new emails and calendar appointments are fetched in the background.</summary>
public class SyncOptions
{
    public const string Section = "Sync";
    public static readonly int[] Choices = [1, 2, 3, 6, 12, 24];

    /// <summary>Hours between background syncs (1–24).</summary>
    public int HoursBetweenSyncs { get; set; } = 1;
}

/// <summary>How often the expense checker reads new vendor messages (see Features:ExpenseSuggestions).</summary>
public class ExpenseCheckOptions
{
    public const string Section = "ExpenseCheck";
    public static readonly int[] Choices = [1, 2, 3, 6, 12, 24];

    /// <summary>Hours between background checks (1–24).</summary>
    public int HoursBetweenChecks { get; set; } = 3;
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

    /// <summary>
    /// The time zone the business runs in, e.g. America/New_York, when it isn't the computer's. Times in the app are shown
    /// in it, and new appointments start out in it. Blank uses the computer's time zone.
    /// </summary>
    public string? TimeZone { get; set; }
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
