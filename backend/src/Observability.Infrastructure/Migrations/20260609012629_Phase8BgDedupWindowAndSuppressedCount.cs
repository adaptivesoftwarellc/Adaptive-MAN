using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Observability.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase8BgDedupWindowAndSuppressedCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SuppressedCount",
                table: "BackgroundJobFailures",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "BackgroundJobDedupWindowMinutes",
                table: "AppEnvironments",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuppressedCount",
                table: "BackgroundJobFailures");

            migrationBuilder.DropColumn(
                name: "BackgroundJobDedupWindowMinutes",
                table: "AppEnvironments");
        }
    }
}
