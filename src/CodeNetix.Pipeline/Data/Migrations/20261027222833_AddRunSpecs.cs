using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Pipeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_specs",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    spec_json = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_specs", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_run_specs_run_runs",
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
                name: "run_specs");
        }
    }
}
