using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Amicus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecialistCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "specialists",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Social");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category",
                table: "specialists");
        }
    }
}
