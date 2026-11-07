namespace PropertyManager.Core.Entities;

public enum SuggestionStatus
{
    Pending,
    Approved,
    Dismissed,
}

/// <summary>
/// An expense the expense checker found in a vendor's text or email, waiting for the owner to approve, change, or
/// dismiss it. Nothing is added to the books until it's approved.
/// </summary>
public class ExpenseSuggestion
{
    public int Id { get; set; }
    public SuggestionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The message the charge was found in (one of the two).</summary>
    public int? TextMessageId { get; set; }
    public TextMessage? TextMessage { get; set; }
    public int? EmailMessageId { get; set; }
    public EmailMessage? EmailMessage { get; set; }

    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Repairs;

    /// <summary>The property, or null with <see cref="IsShared"/> for all properties; null without it means not known yet.</summary>
    public int? PropertyId { get; set; }
    public Property? Property { get; set; }
    public bool IsShared { get; set; }
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public PaymentMethod? PaidWith { get; set; }

    /// <summary>The words in the message the charge was found in.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>An attached invoice or receipt, saved as the expense's receipt when approved.</summary>
    public int? TextAttachmentId { get; set; }
    public TextAttachment? TextAttachment { get; set; }
    public int? EmailAttachmentId { get; set; }
    public EmailAttachment? EmailAttachment { get; set; }

    /// <summary>An existing expense that looks like the same charge (same vendor or amount, close date).</summary>
    public int? DuplicateOfExpenseId { get; set; }
    public Expense? DuplicateOfExpense { get; set; }

    /// <summary>The expense created when it was approved.</summary>
    public int? ExpenseId { get; set; }

    public bool NeedsProperty => PropertyId is null && !IsShared;
}
