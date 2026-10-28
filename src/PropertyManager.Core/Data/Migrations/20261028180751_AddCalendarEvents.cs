using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EndReminderEventId",
                table: "Leases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastAddedToAt",
                table: "CalendarSources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leases_EndReminderEventId",
                table: "Leases",
                column: "EndReminderEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_Leases_CalendarEvents_EndReminderEventId",
                table: "Leases",
                column: "EndReminderEventId",
                principalTable: "CalendarEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Leases_CalendarEvents_EndReminderEventId",
                table: "Leases");

            migrationBuilder.DropIndex(
                name: "IX_Leases_EndReminderEventId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "EndReminderEventId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "LastAddedToAt",
                table: "CalendarSources");
        }
    }
}
