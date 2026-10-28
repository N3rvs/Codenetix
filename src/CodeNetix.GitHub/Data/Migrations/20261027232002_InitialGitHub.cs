using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.GitHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialGitHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gh_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    comment_id = table.Column<long>(type: "INTEGER", nullable: true),
                    comment_url = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gh_notifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_gh_notifications_run_id_kind",
                table: "gh_notifications",
                columns: new[] { "run_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gh_notifications");
        }
    }
}
