using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Amicus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecialistStoryProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "profile",
                table: "specialists",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "profile",
                table: "specialists");
        }
    }
}
