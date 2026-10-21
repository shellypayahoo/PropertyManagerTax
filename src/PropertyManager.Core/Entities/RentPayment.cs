namespace PropertyManager.Core.Entities;

public enum PaymentMethod { Zelle, BankTransfer, Check, Cash, Card, Other }

/// <summary>Rent actually received from a tenant under a lease.</summary>
public class RentPayment
{
    public int Id { get; set; }
    public int LeaseId { get; set; }
    public Lease? Lease { get; set; }

    /// <summary>Date the money was received.</summary>
    public DateOnly Date { get; set; }

    /// <summary>First day of the month this payment is rent for, e.g. October rent paid on November 2 is for October 1.</summary>
    public DateOnly ForMonth { get; set; }

    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Zelle;
    public string? Notes { get; set; }

    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);
}
