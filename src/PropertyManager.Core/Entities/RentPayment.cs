namespace PropertyManager.Core.Entities;

public enum PaymentMethod { Zelle, BankTransfer, Check, Cash, Card, Other }

/// <summary>Rent actually received from a tenant under a lease.</summary>
public class RentPayment
{
    public int Id { get; set; }
    public int LeaseId { get; set; }
    public Lease? Lease { get; set; }

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Zelle;
    public string? Notes { get; set; }
}
