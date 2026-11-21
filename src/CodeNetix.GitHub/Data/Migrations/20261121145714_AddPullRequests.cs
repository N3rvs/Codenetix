using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.GitHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPullRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gh_pull_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<long>(type: "INTEGER", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    branch_name = table.Column<string>(type: "TEXT", nullable: false),
                    commit_sha = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gh_pull_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_gh_pull_requests_run_id",
                table: "gh_pull_requests",
                column: "run_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gh_pull_requests");
        }
    }
}
