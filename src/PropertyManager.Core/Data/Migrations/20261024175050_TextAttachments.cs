using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TextAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TextAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TextMessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Guid = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PreviewKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ThumbnailKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TextAttachments_TextMessages_TextMessageId",
                        column: x => x.TextMessageId,
                        principalTable: "TextMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TextAttachments_Guid",
                table: "TextAttachments",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TextAttachments_TextMessageId",
                table: "TextAttachments",
                column: "TextMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TextAttachments");
        }
    }
}
