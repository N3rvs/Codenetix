using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Ingestion.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "dispatched_at",
                table: "ing_events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                table: "ing_events",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dispatched_at",
                table: "ing_events");

            migrationBuilder.DropColumn(
                name: "run_id",
                table: "ing_events");
        }
    }
}
