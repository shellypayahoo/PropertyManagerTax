using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Depreciation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BasisAdditions",
                table: "Properties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LandValue",
                table: "Properties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RentalStartDate",
                table: "Properties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepreciableAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PropertyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlacedInService = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Cost = table.Column<decimal>(type: "TEXT", nullable: false),
                    Life = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepreciableAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepreciableAssets_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepreciableAssets_PropertyId",
                table: "DepreciableAssets",
                column: "PropertyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepreciableAssets");

            migrationBuilder.DropColumn(
                name: "BasisAdditions",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "LandValue",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "RentalStartDate",
                table: "Properties");
        }
    }
}
