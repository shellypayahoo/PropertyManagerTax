using PropertyManager.Core.Entities;
using PropertyManager.Core.Services;

namespace PropertyManager.Tests;

public class RentScheduleTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static Lease LeaseWith(params RentPayment[] payments) =>
        new() { Id = 1, StartDate = new(2026, 3, 16), MonthlyRent = 4500m, Payments = [.. payments] };

    private static RentPayment Paid(int forMonth, decimal amount, int month = 0, int day = 1) =>
        new() { ForMonth = new(2026, forMonth, 1), Date = new(2026, month == 0 ? forMonth : month, day), Amount = amount };

    [Fact]
    public void FirstPartialMonthIsDueInFull_NoProration()
    {
        var march = RentSchedule.ForMonth(LeaseWith(), new(2026, 3, 1), Today);

        Assert.Equal(4500m, march.Due);
        Assert.Equal(RentStatus.Unpaid, march.Status);
    }

    [Fact]
    public void LatePaymentCountsTowardTheMonthItIsFor()
    {
        // October rent paid on November 2.
        var lease = LeaseWith(Paid(10, 4500m, month: 11, day: 2));

        Assert.Equal(RentStatus.Paid, RentSchedule.ForMonth(lease, new(2026, 10, 1), new(2026, 11, 5)).Status);
        Assert.Equal(RentStatus.Unpaid, RentSchedule.ForMonth(lease, new(2026, 11, 1), new(2026, 11, 5)).Status);
    }

    [Fact]
    public void CoTenantHalvesAddUp()
    {
        var lease = LeaseWith(Paid(9, 2250m), Paid(9, 2250m, day: 3), Paid(10, 2250m));

        Assert.Equal(RentStatus.Paid, RentSchedule.ForMonth(lease, new(2026, 9, 1), Today).Status);
        var october = RentSchedule.ForMonth(lease, new(2026, 10, 1), Today);
        Assert.Equal((RentStatus.Partial, 2250m), (october.Status, october.Balance));
    }

    [Fact]
    public void ForYear_ListsLeaseMonthsThroughCurrentMonth()
    {
        var months = RentSchedule.ForYear(LeaseWith(), 2026, Today);

        Assert.Equal(Enumerable.Range(3, 8), months.Select(m => m.Month.Month));
    }

    [Fact]
    public void FutureMonth_PaidEarlyIsPaid_OtherwiseUpcoming()
    {
        var lease = LeaseWith(Paid(11, 4500m, month: 10, day: 30));

        Assert.Equal(RentStatus.Paid, RentSchedule.ForMonth(lease, new(2026, 11, 1), Today).Status);
        Assert.Equal(RentStatus.Upcoming, RentSchedule.ForMonth(lease, new(2026, 12, 1), Today).Status);
    }

    [Fact]
    public void MonthAfterLeaseEnds_IsNotDue()
    {
        var lease = LeaseWith();
        lease.EndDate = new(2026, 6, 30);

        Assert.Equal(RentStatus.NotDue, RentSchedule.ForMonth(lease, new(2026, 7, 1), Today).Status);
    }
}
