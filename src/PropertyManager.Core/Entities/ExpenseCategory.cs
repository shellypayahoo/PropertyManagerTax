namespace PropertyManager.Core.Entities;

/// <summary>
/// Expense categories matching IRS Schedule E (Form 1040), Part I, lines 5–19.
/// The numeric value is the Schedule E line number.
/// </summary>
public enum ExpenseCategory
{
    Advertising = 5,
    AutoAndTravel = 6,
    CleaningAndMaintenance = 7,
    Commissions = 8,
    Insurance = 9,
    LegalAndProfessionalFees = 10,
    ManagementFees = 11,
    MortgageInterest = 12,
    OtherInterest = 13,
    Repairs = 14,
    Supplies = 15,
    Taxes = 16,
    Utilities = 17,
    Depreciation = 18,
    Other = 19,
}

public static class ExpenseCategoryExtensions
{
    public static string DisplayName(this ExpenseCategory category) => category switch
    {
        ExpenseCategory.AutoAndTravel => "Auto and travel",
        ExpenseCategory.CleaningAndMaintenance => "Cleaning and maintenance",
        ExpenseCategory.LegalAndProfessionalFees => "Legal and other professional fees",
        ExpenseCategory.ManagementFees => "Management fees",
        ExpenseCategory.MortgageInterest => "Mortgage interest paid to banks, etc.",
        ExpenseCategory.OtherInterest => "Other interest",
        ExpenseCategory.Depreciation => "Depreciation expense or depletion",
        _ => category.ToString(),
    };

    public static int ScheduleELine(this ExpenseCategory category) => (int)category;
}
