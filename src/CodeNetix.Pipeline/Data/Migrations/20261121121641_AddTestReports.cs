using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Pipeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTestReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_test_reports",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    report_json = table.Column<string>(type: "TEXT", nullable: false),
                    passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    total_tests = table.Column<int>(type: "INTEGER", nullable: false),
                    failed_tests = table.Column<int>(type: "INTEGER", nullable: false),
                    new_tests_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_test_reports", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_run_test_reports_run_runs",
                        column: x => x.run_id,
                        principalTable: "run_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_test_reports");
        }
    }
}
