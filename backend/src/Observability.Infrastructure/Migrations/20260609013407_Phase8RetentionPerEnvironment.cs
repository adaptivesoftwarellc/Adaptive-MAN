using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Observability.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase8RetentionPerEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ErrorRetentionDays",
                table: "AppEnvironments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventRetentionDays",
                table: "AppEnvironments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplayRetentionDays",
                table: "AppEnvironments",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorRetentionDays",
                table: "AppEnvironments");

            migrationBuilder.DropColumn(
                name: "EventRetentionDays",
                table: "AppEnvironments");

            migrationBuilder.DropColumn(
                name: "ReplayRetentionDays",
                table: "AppEnvironments");
        }
    }
}
