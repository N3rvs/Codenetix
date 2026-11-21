using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Pipeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewStagesAndVerdicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "stage_id",
                table: "run_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    round = table.Column<int>(type: "INTEGER", nullable: false),
                    reviewer = table.Column<string>(type: "TEXT", nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_stages", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_stages_run_runs",
                        column: x => x.run_id,
                        principalTable: "run_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_verdicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    stage_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    round = table.Column<int>(type: "INTEGER", nullable: false),
                    reviewer = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false),
                    summary = table.Column<string>(type: "TEXT", nullable: false),
                    findings_json = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_verdicts", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_verdicts_run_runs",
                        column: x => x.run_id,
                        principalTable: "run_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_run_verdicts_run_stages",
                        column: x => x.stage_id,
                        principalTable: "run_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_run_stages_run_kind_round",
                table: "run_stages",
                columns: new[] { "run_id", "kind", "round" });

            migrationBuilder.CreateIndex(
                name: "IX_run_verdicts_stage_id",
                table: "run_verdicts",
                column: "stage_id");

            migrationBuilder.CreateIndex(
                name: "ux_run_verdicts_run_round_reviewer",
                table: "run_verdicts",
                columns: new[] { "run_id", "round", "reviewer" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_verdicts");

            migrationBuilder.DropTable(
                name: "run_stages");

            migrationBuilder.DropColumn(
                name: "stage_id",
                table: "run_events");
        }
    }
}
