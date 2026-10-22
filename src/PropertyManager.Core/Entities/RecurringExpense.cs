namespace PropertyManager.Core.Entities;

/// <summary>
/// An expense that repeats every month, such as an HVAC service plan, recorded once per month for each target
/// (a unit, a whole property, or all properties).
/// </summary>
public class RecurringExpense
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public ExpenseCategory Category { get; set; } = ExpenseCategory.CleaningAndMaintenance;
    public PaymentMethod? PaidWith { get; set; }

    /// <summary>Amount recorded for each target every month.</summary>
    public decimal Amount { get; set; }

    /// <summary>Day of the month the expense is dated; months shorter than this use their last day.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>First month the expense occurs (the day is ignored).</summary>
    public DateOnly StartMonth { get; set; }

    /// <summary>Last date an occurrence may fall on, or null to repeat indefinitely.</summary>
    public DateOnly? EndDate { get; set; }

    public bool IsPaused { get; set; }

    public List<RecurringExpenseTarget> Targets { get; set; } = [];

    public DateOnly OccurrenceIn(DateOnly month) =>
        new(month.Year, month.Month, Math.Min(DayOfMonth, DateTime.DaysInMonth(month.Year, month.Month)));
}

/// <summary>Where a recurring expense is recorded each month. A null property means shared by all properties.</summary>
public class RecurringExpenseTarget
{
    public int Id { get; set; }
    public int RecurringExpenseId { get; set; }
    public RecurringExpense? RecurringExpense { get; set; }

    public int? PropertyId { get; set; }
    public Property? Property { get; set; }
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }

    /// <summary>First month this target is included; targets added later don't back-fill earlier months.</summary>
    public DateOnly StartMonth { get; set; }
}

/// <summary>
/// Records that a recurring expense was generated for a target and month, so it is never generated twice, even
/// after the generated expense is edited or deleted.
/// </summary>
public class RecurringExpenseRun
{
    public int Id { get; set; }
    public int TargetId { get; set; }
    public RecurringExpenseTarget? Target { get; set; }
    public DateOnly Month { get; set; }
    public int? ExpenseId { get; set; }
    public Expense? Expense { get; set; }
}
