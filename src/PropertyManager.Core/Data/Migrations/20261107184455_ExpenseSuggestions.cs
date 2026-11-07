using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExpenseChecked",
                table: "TextMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExpenseChecked",
                table: "EmailMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ExpenseSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TextMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    EmailMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    VendorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PropertyId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsShared = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnitId = table.Column<int>(type: "INTEGER", nullable: true),
                    PaidWith = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    TextAttachmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    EmailAttachmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    DuplicateOfExpenseId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpenseId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_EmailAttachments_EmailAttachmentId",
                        column: x => x.EmailAttachmentId,
                        principalTable: "EmailAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_EmailMessages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "EmailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_Expenses_DuplicateOfExpenseId",
                        column: x => x.DuplicateOfExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_TextAttachments_TextAttachmentId",
                        column: x => x.TextAttachmentId,
                        principalTable: "TextAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_TextMessages_TextMessageId",
                        column: x => x.TextMessageId,
                        principalTable: "TextMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExpenseSuggestions_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_DuplicateOfExpenseId",
                table: "ExpenseSuggestions",
                column: "DuplicateOfExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_EmailAttachmentId",
                table: "ExpenseSuggestions",
                column: "EmailAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_EmailMessageId",
                table: "ExpenseSuggestions",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_PropertyId",
                table: "ExpenseSuggestions",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_Status",
                table: "ExpenseSuggestions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_TextAttachmentId",
                table: "ExpenseSuggestions",
                column: "TextAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_TextMessageId",
                table: "ExpenseSuggestions",
                column: "TextMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_UnitId",
                table: "ExpenseSuggestions",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSuggestions_VendorId",
                table: "ExpenseSuggestions",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseSuggestions");

            migrationBuilder.DropColumn(
                name: "ExpenseChecked",
                table: "TextMessages");

            migrationBuilder.DropColumn(
                name: "ExpenseChecked",
                table: "EmailMessages");
        }
    }
}
