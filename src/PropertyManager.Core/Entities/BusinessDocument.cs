namespace PropertyManager.Core.Entities;

/// <summary>What a business document is, for filing and filtering.</summary>
public enum BusinessDocumentKind
{
    LlcAndFormation,
    TaxReturn,
    TaxForm,
    Insurance,
    DeedAndClosing,
    MortgageAndLoan,
    BankStatement,
    Other,
}

public static class BusinessDocumentKindExtensions
{
    public static string DisplayName(this BusinessDocumentKind kind) => kind switch
    {
        BusinessDocumentKind.LlcAndFormation => "LLC and formation",
        BusinessDocumentKind.TaxReturn => "Tax return",
        BusinessDocumentKind.TaxForm => "1099s and W-9s",
        BusinessDocumentKind.Insurance => "Insurance",
        BusinessDocumentKind.DeedAndClosing => "Deed and closing",
        BusinessDocumentKind.MortgageAndLoan => "Mortgage and loan",
        BusinessDocumentKind.BankStatement => "Bank statement",
        _ => "Other",
    };

    /// <summary>Tax papers carry SSNs and EINs, so they're kept from the Ask page unless the owner says otherwise.</summary>
    public static bool PrivateByDefault(this BusinessDocumentKind kind) =>
        kind is BusinessDocumentKind.TaxReturn or BusinessDocumentKind.TaxForm;
}

/// <summary>
/// A file about the business rather than a tenant or expense: LLC papers, tax returns, insurance policies, deeds,
/// closing documents, loan papers.
/// </summary>
public class BusinessDocument
{
    public int Id { get; set; }
    public BusinessDocumentKind Kind { get; set; }

    /// <summary>The owner's description, e.g. "Closing" or "Tax 2026".</summary>
    public string Description { get; set; } = "";

    /// <summary>The property it's about, or null for the whole business.</summary>
    public int? PropertyId { get; set; }
    public Property? Property { get; set; }

    /// <summary>The year it's for (tax year, policy year), when that matters.</summary>
    public int? Year { get; set; }

    /// <summary>When set, the Ask page never sees this document, so its contents are never sent to an AI model.</summary>
    public bool KeepFromAssistant { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    /// <summary>The original file in <see cref="Storage.IDocumentStorage"/>.</summary>
    public string StorageKey { get; set; } = "";

    /// <summary>For photos: a JPEG every browser can show (iPhone photos are HEIC), and a thumbnail.</summary>
    public string? PreviewKey { get; set; }
    public string? ThumbnailKey { get; set; }

    /// <summary>Text read from PDF and Word files, for search and the Ask page.</summary>
    public string? Text { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
