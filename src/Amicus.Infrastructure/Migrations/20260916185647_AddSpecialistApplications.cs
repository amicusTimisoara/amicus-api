using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Amicus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecialistApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "specialist_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    specialty = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    profile = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    story = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    speaks_english = table.Column<bool>(type: "boolean", nullable: false),
                    accepts_small_groups = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_specialist_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_specialist_applications_specialists_specialist_id",
                        column: x => x.specialist_id,
                        principalTable: "specialists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_specialist_applications_specialist_id",
                table: "specialist_applications",
                column: "specialist_id");

            migrationBuilder.CreateIndex(
                name: "ix_specialist_applications_status_created_at",
                table: "specialist_applications",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_specialist_applications_user_id",
                table: "specialist_applications",
                column: "user_id",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "specialist_applications");
        }
    }
}
