namespace PropertyManager.Core.Entities;

/// <summary>
/// Expense categories matching IRS Schedule E (Form 1040), Part I, lines 5–19; the numeric value is the
/// Schedule E line number. Values of 100 and up are tracked but not deductible, so they stay off the tax report.
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

    /// <summary>The part of a mortgage payment that repays the loan. Not a deductible expense.</summary>
    MortgagePrincipal = 100,

    /// <summary>
    /// The part of a mortgage payment set aside in escrow. Not deductible when paid in; the property taxes and
    /// insurance the lender later pays out of escrow are deducted as Taxes and Insurance on the dates paid.
    /// </summary>
    EscrowDeposit = 101,
}

public static class ExpenseCategoryExtensions
{
    public static string DisplayName(this ExpenseCategory category) => category switch
    {
        ExpenseCategory.AutoAndTravel => "Auto and travel",
        ExpenseCategory.CleaningAndMaintenance => "Cleaning and maintenance",
        ExpenseCategory.LegalAndProfessionalFees => "Legal and other professional fees",
        ExpenseCategory.ManagementFees => "Management fees",
        ExpenseCategory.MortgageInterest => "Mortgage interest",
        ExpenseCategory.MortgagePrincipal => "Mortgage principal (not deductible)",
        ExpenseCategory.EscrowDeposit => "Escrow deposit (not deductible)",
        ExpenseCategory.OtherInterest => "Other interest",
        ExpenseCategory.Depreciation => "Depreciation expense or depletion",
        _ => category.ToString(),
    };

    /// <summary>The Schedule E line number, or null for a category that isn't deductible.</summary>
    public static int? ScheduleELine(this ExpenseCategory category) => category.IsDeductible() ? (int)category : null;

    public static bool IsDeductible(this ExpenseCategory category) => (int)category is >= 5 and <= 19;
}
