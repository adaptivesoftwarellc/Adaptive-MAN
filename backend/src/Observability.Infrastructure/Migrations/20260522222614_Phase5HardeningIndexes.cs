using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Observability.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase5HardeningIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Events_ApplicationId_EnvironmentId_SessionId_OccurredAt",
                table: "Events",
                columns: new[] { "ApplicationId", "EnvironmentId", "SessionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Errors_ApplicationId_EnvironmentId_LastCorrelationId",
                table: "Errors",
                columns: new[] { "ApplicationId", "EnvironmentId", "LastCorrelationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_ApplicationId_EnvironmentId_SessionId_OccurredAt",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Errors_ApplicationId_EnvironmentId_LastCorrelationId",
                table: "Errors");
        }
    }
}
