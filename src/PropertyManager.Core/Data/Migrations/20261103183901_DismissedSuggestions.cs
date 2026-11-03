using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DismissedSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DismissedSuggestions",
                columns: table => new
                {
                    MaintenanceRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DismissedSuggestions", x => new { x.MaintenanceRequestId, x.CalendarEventId });
                    table.ForeignKey(
                        name: "FK_DismissedSuggestions_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DismissedSuggestions_MaintenanceRequests_MaintenanceRequestId",
                        column: x => x.MaintenanceRequestId,
                        principalTable: "MaintenanceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DismissedSuggestions_CalendarEventId",
                table: "DismissedSuggestions",
                column: "CalendarEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DismissedSuggestions");
        }
    }
}
