using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Observability.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase11Annotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Annotations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReleaseSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Annotations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Annotations_ApplicationId_EnvironmentId_At",
                table: "Annotations",
                columns: new[] { "ApplicationId", "EnvironmentId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Annotations");
        }
    }
}
