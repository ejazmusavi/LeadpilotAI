using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Discovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoveryCategoryMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CategoryId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SearchTerms = table.Column<string>(type: "varchar(600)", maxLength: 600, nullable: false),
                    PlaceType = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryCategoryMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryCategoryMappings_LeadCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "LeadCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiscoveryCategoryMappings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiscoveryConnectorConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConnectorType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    KeyHint = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    MonthlyCallLimit = table.Column<int>(type: "int", nullable: true),
                    MonthlyBudgetUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CostPerCallUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryConnectorConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryConnectorConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiscoveryJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConnectorType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Country = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Province = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    City = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    District = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Area = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    CategoryId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Keywords = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    SeedUrls = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    MaxResults = table.Column<int>(type: "int", nullable: false),
                    EnrichWebsites = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    HangfireJobId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    PagesFetched = table.Column<int>(type: "int", nullable: false),
                    ResultsFound = table.Column<int>(type: "int", nullable: false),
                    ResultsNew = table.Column<int>(type: "int", nullable: false),
                    ResultsAlreadyKnown = table.Column<int>(type: "int", nullable: false),
                    DuplicatesFlagged = table.Column<int>(type: "int", nullable: false),
                    ApiCalls = table.Column<int>(type: "int", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Checkpoint = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    CancelRequested = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Warnings = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    ErrorMessage = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ErrorRetryable = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryJobs_LeadCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "LeadCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiscoveryJobs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiscoveryLocationPresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Country = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Province = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    City = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    District = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Area = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Label = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryLocationPresets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryLocationPresets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiscoveryUsageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    JobId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ConnectorType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    Operation = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Calls = table.Column<int>(type: "int", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryUsageEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryUsageEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiscoveryResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    JobId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConnectorType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CategoryId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CategoryLabel = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    Country = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Province = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    City = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    District = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Area = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Address = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Website = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    PublicPhone = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    PublicEmail = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    SocialLinks = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    SourceName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    SourceUrl = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Attribution = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ContentPurged = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Confidence = table.Column<int>(type: "int", nullable: false),
                    ConfidenceLevel = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    ResearchNotes = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    Provenance = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    Enrichment = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ReviewStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ReviewNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    DuplicateLeadId = table.Column<Guid>(type: "char(36)", nullable: true),
                    DuplicateReasons = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Approval = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryResults_DiscoveryJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "DiscoveryJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiscoveryResults_LeadCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "LeadCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiscoveryResults_Leads_DuplicateLeadId",
                        column: x => x.DuplicateLeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiscoveryResults_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiscoveryResults_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryCategoryMappings_CategoryId",
                table: "DiscoveryCategoryMappings",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryCategoryMappings_TenantId",
                table: "DiscoveryCategoryMappings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryCategoryMappings_TenantId_CategoryId",
                table: "DiscoveryCategoryMappings",
                columns: new[] { "TenantId", "CategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryConnectorConfigs_TenantId",
                table: "DiscoveryConnectorConfigs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryConnectorConfigs_TenantId_ConnectorType",
                table: "DiscoveryConnectorConfigs",
                columns: new[] { "TenantId", "ConnectorType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_CategoryId",
                table: "DiscoveryJobs",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_TenantId",
                table: "DiscoveryJobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_TenantId_CreatedAtUtc",
                table: "DiscoveryJobs",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_TenantId_Status",
                table: "DiscoveryJobs",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryLocationPresets_TenantId",
                table: "DiscoveryLocationPresets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryLocationPresets_TenantId_Label",
                table: "DiscoveryLocationPresets",
                columns: new[] { "TenantId", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_CategoryId",
                table: "DiscoveryResults",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_DuplicateLeadId",
                table: "DiscoveryResults",
                column: "DuplicateLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_ExpiresAtUtc_ContentPurged",
                table: "DiscoveryResults",
                columns: new[] { "ExpiresAtUtc", "ContentPurged" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_JobId",
                table: "DiscoveryResults",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_LeadId",
                table: "DiscoveryResults",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_TenantId",
                table: "DiscoveryResults",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_TenantId_ConnectorType_ExternalId",
                table: "DiscoveryResults",
                columns: new[] { "TenantId", "ConnectorType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_TenantId_JobId_ReviewStatus",
                table: "DiscoveryResults",
                columns: new[] { "TenantId", "JobId", "ReviewStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryResults_TenantId_ReviewStatus_Confidence",
                table: "DiscoveryResults",
                columns: new[] { "TenantId", "ReviewStatus", "Confidence" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryUsageEntries_TenantId",
                table: "DiscoveryUsageEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryUsageEntries_TenantId_ConnectorType_PeriodKey",
                table: "DiscoveryUsageEntries",
                columns: new[] { "TenantId", "ConnectorType", "PeriodKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryUsageEntries_TenantId_OccurredAtUtc",
                table: "DiscoveryUsageEntries",
                columns: new[] { "TenantId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryCategoryMappings");

            migrationBuilder.DropTable(
                name: "DiscoveryConnectorConfigs");

            migrationBuilder.DropTable(
                name: "DiscoveryLocationPresets");

            migrationBuilder.DropTable(
                name: "DiscoveryResults");

            migrationBuilder.DropTable(
                name: "DiscoveryUsageEntries");

            migrationBuilder.DropTable(
                name: "DiscoveryJobs");
        }
    }
}
