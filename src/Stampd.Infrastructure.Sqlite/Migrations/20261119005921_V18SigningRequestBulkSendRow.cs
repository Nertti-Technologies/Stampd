using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class V18SigningRequestBulkSendRow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BulkSendJobId",
                table: "SigningRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BulkSendRowIndex",
                table: "SigningRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_BulkSendJobId_BulkSendRowIndex",
                table: "SigningRequests",
                columns: new[] { "BulkSendJobId", "BulkSendRowIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SigningRequests_BulkSendJobId_BulkSendRowIndex",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "BulkSendJobId",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "BulkSendRowIndex",
                table: "SigningRequests");
        }
    }
}
