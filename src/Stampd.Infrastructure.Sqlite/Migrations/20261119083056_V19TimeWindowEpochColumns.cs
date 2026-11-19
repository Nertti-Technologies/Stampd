using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class V19TimeWindowEpochColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "WebhookDeliveries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "OtpChallenges",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OccurredAtUtcEpochMs",
                table: "AuditEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill existing rows; new rows are stamped by StampdDbContext on insert.
            migrationBuilder.Sql(@"
                UPDATE AuditEvents
                SET OccurredAtUtcEpochMs = CAST(ROUND((julianday(OccurredAtUtc) - 2440587.5) * 86400000.0) AS INTEGER)
                WHERE OccurredAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE OtpChallenges
                SET CreatedAtUtcEpochMs = CAST(ROUND((julianday(CreatedAtUtc) - 2440587.5) * 86400000.0) AS INTEGER)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE WebhookDeliveries
                SET CreatedAtUtcEpochMs = CAST(ROUND((julianday(CreatedAtUtc) - 2440587.5) * 86400000.0) AS INTEGER)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_CreatedAtUtcEpochMs",
                table: "WebhookDeliveries",
                column: "CreatedAtUtcEpochMs");

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_CreatedAtUtcEpochMs",
                table: "OtpChallenges",
                column: "CreatedAtUtcEpochMs");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SigningRequestId_OccurredAtUtcEpochMs",
                table: "AuditEvents",
                columns: new[] { "SigningRequestId", "OccurredAtUtcEpochMs" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_OccurredAtUtcEpochMs",
                table: "AuditEvents",
                columns: new[] { "TenantId", "OccurredAtUtcEpochMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveries_CreatedAtUtcEpochMs",
                table: "WebhookDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_OtpChallenges_CreatedAtUtcEpochMs",
                table: "OtpChallenges");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SigningRequestId_OccurredAtUtcEpochMs",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TenantId_OccurredAtUtcEpochMs",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcEpochMs",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcEpochMs",
                table: "OtpChallenges");

            migrationBuilder.DropColumn(
                name: "OccurredAtUtcEpochMs",
                table: "AuditEvents");
        }
    }
}
