using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaidWith",
                table: "Expenses",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsRentalOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    DateColumn = table.Column<string>(type: "TEXT", nullable: true),
                    DescriptionColumn = table.Column<string>(type: "TEXT", nullable: true),
                    AmountColumn = table.Column<string>(type: "TEXT", nullable: true),
                    DebitColumn = table.Column<string>(type: "TEXT", nullable: true),
                    CreditColumn = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CategorizationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MatchText = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PropertyId = table.Column<int>(type: "INTEGER", nullable: true),
                    UnitId = table.Column<int>(type: "INTEGER", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    VendorId = table.Column<int>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategorizationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategorizationRules_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategorizationRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategorizationRules_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CategorizationRules_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RentPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LeaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentPayments_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportedTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ExpenseId = table.Column<int>(type: "INTEGER", nullable: true),
                    RentPaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedTransactions_BankAccounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "BankAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportedTransactions_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ImportedTransactions_RentPayments_RentPaymentId",
                        column: x => x.RentPaymentId,
                        principalTable: "RentPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_Name",
                table: "BankAccounts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategorizationRules_PropertyId",
                table: "CategorizationRules",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_CategorizationRules_TenantId",
                table: "CategorizationRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CategorizationRules_UnitId",
                table: "CategorizationRules",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_CategorizationRules_VendorId",
                table: "CategorizationRules",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedTransactions_BankAccountId_Fingerprint",
                table: "ImportedTransactions",
                columns: new[] { "BankAccountId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedTransactions_ExpenseId",
                table: "ImportedTransactions",
                column: "ExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedTransactions_RentPaymentId",
                table: "ImportedTransactions",
                column: "RentPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_RentPayments_Date",
                table: "RentPayments",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_RentPayments_LeaseId",
                table: "RentPayments",
                column: "LeaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategorizationRules");

            migrationBuilder.DropTable(
                name: "ImportedTransactions");

            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropTable(
                name: "RentPayments");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "PaidWith",
                table: "Expenses");
        }
    }
}
