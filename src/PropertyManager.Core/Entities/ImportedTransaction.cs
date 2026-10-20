namespace PropertyManager.Core.Entities;

public enum TransactionKind
{
    /// <summary>Money out for a rental expense.</summary>
    Expense,

    /// <summary>Rent received from a tenant.</summary>
    RentPayment,

    /// <summary>Money moved between your own accounts, e.g. Zelle rent swept into the business account. Not income or expense.</summary>
    Transfer,

    /// <summary>Not related to the rentals.</summary>
    Ignored,
}

/// <summary>A bank transaction that has been imported, kept so the same row is never imported twice.</summary>
public class ImportedTransaction
{
    public int Id { get; set; }
    public int BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }

    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Signed amount: positive is money in, negative is money out.</summary>
    public decimal Amount { get; set; }

    /// <summary>Identifies the row within its account so re-importing an overlapping file skips it.</summary>
    public string Fingerprint { get; set; } = "";

    public TransactionKind Kind { get; set; }
    public int? ExpenseId { get; set; }
    public Expense? Expense { get; set; }
    public int? RentPaymentId { get; set; }
    public RentPayment? RentPayment { get; set; }

    public DateTimeOffset ImportedAt { get; set; }
}
