using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MortgageAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EscrowColumn",
                table: "BankAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InterestColumn",
                table: "BankAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMortgage",
                table: "BankAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalColumn",
                table: "BankAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PropertyId",
                table: "BankAccounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_PropertyId",
                table: "BankAccounts",
                column: "PropertyId");

            migrationBuilder.AddForeignKey(
                name: "FK_BankAccounts_Properties_PropertyId",
                table: "BankAccounts",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankAccounts_Properties_PropertyId",
                table: "BankAccounts");

            migrationBuilder.DropIndex(
                name: "IX_BankAccounts_PropertyId",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "EscrowColumn",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "InterestColumn",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "IsMortgage",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "PrincipalColumn",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "BankAccounts");
        }
    }
}
