using System.Globalization;
using PropertyManager.Core.Entities;
using PropertyManager.Core.Import;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public class DuplicatePaymentTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateOnly October = new(2026, 10, 1);

    // The app formats all amounts as US dollars (see Program.cs).
    public DuplicatePaymentTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-US");

    private static Lease LeaseWith(params RentPayment[] payments) =>
        new() { Id = 1, StartDate = new(2025, 1, 1), MonthlyRent = 4500m, Payments = [.. payments] };

    private static RentPayment Payment(int id, decimal amount, int day, int forMonth = 10) =>
        new() { Id = id, LeaseId = 1, Amount = amount, Date = new(2026, 10, day), ForMonth = new(2026, forMonth, 1) };

    [Fact]
    public void SameRentEnteredTwice_IsFlagged()
    {
        var lease = LeaseWith(Payment(1, 4500m, 1), Payment(2, 4500m, 3));

        var october = RentSchedule.ForMonth(lease, October, Today);
        Assert.Equal("Paid twice?", october.Warning);
        var problem = Assert.Single(RentSchedule.FindProblems([lease], Today));
        Assert.Equal((lease, October), (problem.Lease, problem.Month));
    }

    [Fact]
    public void RoommatesPayingHalfEach_IsNotFlagged()
    {
        var lease = LeaseWith(Payment(1, 2250m, 1), Payment(2, 2250m, 2));

        Assert.Null(RentSchedule.ForMonth(lease, October, Today).Warning);
        Assert.Empty(RentSchedule.FindProblems([lease], Today));
    }

    [Fact]
    public void CheckNewPayment_WarnsWhenMonthAlreadyPaid()
    {
        var lease = LeaseWith(Payment(1, 4500m, 1));

        var warning = RentSchedule.CheckNewPayment(lease, October, new(2026, 10, 21), 4500m, Today);

        Assert.Equal("October 2026 already has $4,500 of $4,500 paid ($4,500 on Oct 1). Saving this makes it $9,000.", warning);
    }

    [Fact]
    public void CheckNewPayment_WarnsOnSameAmountAndDateInAnotherMonth()
    {
        var lease = LeaseWith(Payment(1, 4500m, 1, forMonth: 9));

        var warning = RentSchedule.CheckNewPayment(lease, October, new(2026, 10, 1), 4500m, Today);

        Assert.Equal("A payment of $4,500.00 on Oct 1, 2026 is already recorded (for September 2026).", warning);
    }

    [Fact]
    public void CheckNewPayment_AllowsSecondHalfAndEditingSamePayment()
    {
        var half = LeaseWith(Payment(1, 2250m, 1));
        Assert.Null(RentSchedule.CheckNewPayment(half, October, new(2026, 10, 5), 2250m, Today));

        var paid = LeaseWith(Payment(1, 4500m, 1));
        Assert.Null(RentSchedule.CheckNewPayment(paid, October, new(2026, 10, 2), 4500m, Today, editing: paid.Payments[0]));
    }

    [Fact]
    public void MovingADuplicateToTheRightMonth_DoesNotWarn()
    {
        // Rent entered twice on Oct 21; the second one was really September's.
        var lease = LeaseWith(Payment(1, 4500m, 21), Payment(2, 4500m, 21));

        Assert.Null(RentSchedule.CheckNewPayment(lease, new(2026, 9, 1), new(2026, 10, 21), 4500m, Today, editing: lease.Payments[1]));
    }

    [Fact]
    public void Import_SkipsZelleRentForMonthAlreadyMarkedPaid()
    {
        var tenant = new Tenant { Id = 1, FirstName = "Jane", LastName = "Sample" };
        var lease = new Lease { Id = 1, StartDate = new(2025, 1, 1), MonthlyRent = 4500m, Tenants = [tenant] };
        // Marked paid by hand with the default date of the 1st; Zelle shows it arrived on the 9th.
        var context = new ClassifierContext([], [lease], [], new Dictionary<int, Expense>(), [], [Payment(1, 4500m, 1)]);
        var row = new ImportRow(new BankRow(2, new(2026, 10, 9), "Zelle payment from JANE SAMPLE", 4500m, "f"));

        TransactionClassifier.Classify(row, new BankAccount { Name = "Zelle", IsRentalOnly = false }, context);

        Assert.Equal(TransactionKind.Ignored, row.Kind);
        Assert.Equal("October rent is already recorded ($4,500); this would pay it twice", row.Reason);
    }
}
