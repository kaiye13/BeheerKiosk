using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boekhouding.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductTrackStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TrackStock",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                // Bestaande producten houden hun voorraad bij.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrackStock",
                table: "Products");
        }
    }
}
