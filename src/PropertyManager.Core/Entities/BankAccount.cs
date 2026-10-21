namespace PropertyManager.Core.Entities;

/// <summary>
/// An account that transactions are imported from, with the CSV column layout its bank uses.
/// Column names are matched against the CSV header row, ignoring case.
/// </summary>
public class BankAccount
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>
    /// True when the account is used only for the rentals. Unrecognized money out is then suggested as an
    /// expense; for a personal account it is suggested as ignored.
    /// </summary>
    public bool IsRentalOnly { get; set; } = true;

    public string? DateColumn { get; set; }
    public string? DescriptionColumn { get; set; }

    /// <summary>Single signed amount column (negative = money out). Leave null when the bank uses separate debit and credit columns.</summary>
    public string? AmountColumn { get; set; }

    public string? DebitColumn { get; set; }
    public string? CreditColumn { get; set; }

    /// <summary>
    /// True for a mortgage loan whose payment history is imported from the lender. For a loan, <see cref="AmountColumn"/>
    /// is the payment total (optional) and the payment is broken down by the principal, interest, and escrow columns.
    /// </summary>
    public bool IsMortgage { get; set; }

    /// <summary>For a mortgage: the property the loan is on.</summary>
    public int? PropertyId { get; set; }
    public Property? Property { get; set; }

    public string? PrincipalColumn { get; set; }
    public string? InterestColumn { get; set; }
    public string? EscrowColumn { get; set; }
}
