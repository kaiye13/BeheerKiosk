using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boekhouding.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductRequiresShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresShipping",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                // Bestaande producten worden verzonden.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresShipping",
                table: "Products");
        }
    }
}
