using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Observability.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase8AlertEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RuleType = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    EventName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WindowMinutes = table.Column<int>(type: "int", nullable: false),
                    Threshold = table.Column<double>(type: "float", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastEvaluatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FiredAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleType = table.Column<int>(type: "int", nullable: false),
                    FiredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DedupKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ObservedValue = table.Column<double>(type: "float", nullable: false),
                    Threshold = table.Column<double>(type: "float", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiredAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_ApplicationId_IsEnabled",
                table: "AlertRules",
                columns: new[] { "ApplicationId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_FiredAlerts_AlertRuleId_DedupKey_FiredAt",
                table: "FiredAlerts",
                columns: new[] { "AlertRuleId", "DedupKey", "FiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FiredAlerts_ApplicationId_FiredAt",
                table: "FiredAlerts",
                columns: new[] { "ApplicationId", "FiredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "FiredAlerts");
        }
    }
}
