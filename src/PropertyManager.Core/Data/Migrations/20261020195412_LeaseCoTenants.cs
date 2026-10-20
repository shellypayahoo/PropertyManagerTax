using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LeaseCoTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeaseTenants",
                columns: table => new
                {
                    LeasesId = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseTenants", x => new { x.LeasesId, x.TenantsId });
                    table.ForeignKey(
                        name: "FK_LeaseTenants_Leases_LeasesId",
                        column: x => x.LeasesId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaseTenants_Tenants_TenantsId",
                        column: x => x.TenantsId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseTenants_TenantsId",
                table: "LeaseTenants",
                column: "TenantsId");

            // Carry each existing lease's tenant over before the old column is dropped.
            migrationBuilder.Sql(
                "INSERT INTO LeaseTenants (LeasesId, TenantsId) SELECT Id, TenantId FROM Leases WHERE TenantId IN (SELECT Id FROM Tenants);");

            migrationBuilder.DropForeignKey(
                name: "FK_Leases_Tenants_TenantId",
                table: "Leases");

            migrationBuilder.DropIndex(
                name: "IX_Leases_TenantId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Leases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Leases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Keep one tenant per lease (the first added) when going back to a single tenant.
            migrationBuilder.Sql(
                "UPDATE Leases SET TenantId = (SELECT MIN(TenantsId) FROM LeaseTenants WHERE LeasesId = Leases.Id) WHERE Id IN (SELECT LeasesId FROM LeaseTenants);");

            migrationBuilder.DropTable(
                name: "LeaseTenants");

            migrationBuilder.CreateIndex(
                name: "IX_Leases_TenantId",
                table: "Leases",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Leases_Tenants_TenantId",
                table: "Leases",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
