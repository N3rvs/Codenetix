using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeNetix.Pipeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "base_sha",
                table: "run_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "effective_config_json",
                table: "run_runs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "base_sha",
                table: "run_runs");

            migrationBuilder.DropColumn(
                name: "effective_config_json",
                table: "run_runs");
        }
    }
}
