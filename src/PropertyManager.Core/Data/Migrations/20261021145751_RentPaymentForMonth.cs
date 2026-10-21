using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RentPaymentForMonth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RentPayments_LeaseId",
                table: "RentPayments");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ForMonth",
                table: "RentPayments",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            // Existing payments count toward the month they were received in.
            migrationBuilder.Sql("UPDATE RentPayments SET ForMonth = substr(Date, 1, 8) || '01';");

            migrationBuilder.CreateIndex(
                name: "IX_RentPayments_LeaseId_ForMonth",
                table: "RentPayments",
                columns: new[] { "LeaseId", "ForMonth" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RentPayments_LeaseId_ForMonth",
                table: "RentPayments");

            migrationBuilder.DropColumn(
                name: "ForMonth",
                table: "RentPayments");

            migrationBuilder.CreateIndex(
                name: "IX_RentPayments_LeaseId",
                table: "RentPayments",
                column: "LeaseId");
        }
    }
}
