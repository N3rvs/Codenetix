using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Ingestion.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ing_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    object_key = table.Column<string>(type: "TEXT", nullable: false),
                    dedup_key = table.Column<string>(type: "TEXT", nullable: false),
                    actor = table.Column<string>(type: "TEXT", nullable: true),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    ignored_reason = table.Column<string>(type: "TEXT", nullable: true),
                    received_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ing_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ing_events_dedup_key",
                table: "ing_events",
                column: "dedup_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ing_events");
        }
    }
}
