using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TextMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Handle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Included = table.Column<bool>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    VendorId = table.Column<int>(type: "INTEGER", nullable: true),
                    MessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastMessageAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageContacts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MessageContacts_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TextMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Guid = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ChatIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsGroupChat = table.Column<bool>(type: "INTEGER", nullable: false),
                    SenderHandle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsFromMe = table.Column<bool>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    AttachmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContactHandle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    VendorId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TextMessages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TextMessages_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageContacts_Handle",
                table: "MessageContacts",
                column: "Handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageContacts_TenantId",
                table: "MessageContacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageContacts_VendorId",
                table: "MessageContacts",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_TextMessages_ContactHandle",
                table: "TextMessages",
                column: "ContactHandle");

            migrationBuilder.CreateIndex(
                name: "IX_TextMessages_Guid",
                table: "TextMessages",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TextMessages_SentAt",
                table: "TextMessages",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_TextMessages_TenantId",
                table: "TextMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TextMessages_VendorId",
                table: "TextMessages",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageContacts");

            migrationBuilder.DropTable(
                name: "TextMessages");
        }
    }
}
