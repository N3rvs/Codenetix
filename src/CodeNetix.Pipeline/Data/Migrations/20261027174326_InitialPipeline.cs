using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Pipeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    short_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    object_key = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", nullable: true),
                    failure_detail = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "run_events",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    seq = table.Column<long>(type: "INTEGER", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_events", x => new { x.run_id, x.seq });
                    table.ForeignKey(
                        name: "fk_run_events_run_runs",
                        column: x => x.run_id,
                        principalTable: "run_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_run_runs_active_object_key",
                table: "run_runs",
                column: "object_key",
                unique: true,
                filter: "state NOT IN ('PullRequestCreated', 'Failed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "ux_run_runs_event_id",
                table: "run_runs",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_run_runs_short_id",
                table: "run_runs",
                column: "short_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_events");

            migrationBuilder.DropTable(
                name: "run_runs");
        }
    }
}
