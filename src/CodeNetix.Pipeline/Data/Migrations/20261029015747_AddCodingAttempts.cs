using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Pipeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCodingAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "coding_attempts",
                table: "run_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "failed_coding_attempts",
                table: "run_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "coding_attempts",
                table: "run_runs");

            migrationBuilder.DropColumn(
                name: "failed_coding_attempts",
                table: "run_runs");
        }
    }
}
