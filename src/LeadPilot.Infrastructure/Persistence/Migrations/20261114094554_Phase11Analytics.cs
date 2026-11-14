using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyticsRefreshStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CoveredFromDay = table.Column<DateTime>(type: "date", precision: 6, nullable: false),
                    CoveredToDay = table.Column<DateTime>(type: "date", precision: 6, nullable: false),
                    LastRefreshedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    LastFullRebuildAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastDurationMs = table.Column<int>(type: "int", nullable: false),
                    LastRowCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsRefreshStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalyticsRefreshStates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DailyMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Day = table.Column<DateTime>(type: "date", precision: 6, nullable: false),
                    MetricKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Dimension = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyMetrics_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_Tenant_Created",
                table: "Trials",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Tenant_Paid",
                table: "Payments",
                columns: new[] { "TenantId", "PaidAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Tenant_Status_Updated",
                table: "Opportunities",
                columns: new[] { "TenantId", "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Tenant_Dir_Occurred",
                table: "Messages",
                columns: new[] { "TenantId", "Direction", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Tenant_Dir_Sent",
                table: "Messages",
                columns: new[] { "TenantId", "Direction", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Tenant_Failed",
                table: "Messages",
                columns: new[] { "TenantId", "FailedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Demos_Tenant_Completed",
                table: "Demos",
                columns: new[] { "TenantId", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Demos_Tenant_Created",
                table: "Demos",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Tenant_Converted",
                table: "Customers",
                columns: new[] { "TenantId", "ConvertedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_Tenant_Sent",
                table: "CampaignRecipients",
                columns: new[] { "TenantId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsRefreshStates_TenantId",
                table: "AnalyticsRefreshStates",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyMetrics_TenantId",
                table: "DailyMetrics",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_DailyMetrics_Tenant_Metric_Day_Dim",
                table: "DailyMetrics",
                columns: new[] { "TenantId", "MetricKey", "Day", "Dimension" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsRefreshStates");

            migrationBuilder.DropTable(
                name: "DailyMetrics");

            migrationBuilder.DropIndex(
                name: "IX_Trials_Tenant_Created",
                table: "Trials");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Tenant_Paid",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Opportunities_Tenant_Status_Updated",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Messages_Tenant_Dir_Occurred",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_Tenant_Dir_Sent",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_Tenant_Failed",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Demos_Tenant_Completed",
                table: "Demos");

            migrationBuilder.DropIndex(
                name: "IX_Demos_Tenant_Created",
                table: "Demos");

            migrationBuilder.DropIndex(
                name: "IX_Customers_Tenant_Converted",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_CampaignRecipients_Tenant_Sent",
                table: "CampaignRecipients");
        }
    }
}
