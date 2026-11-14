using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Crm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    FileName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    FileType = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredFileKey = table.Column<string>(type: "varchar(400)", maxLength: 400, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    DuplicatePolicy = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    MergedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedDuplicateCount = table.Column<int>(type: "int", nullable: false),
                    PendingDuplicateCount = table.Column<int>(type: "int", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    Mapping = table.Column<string>(type: "longtext", nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportJobs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadCategories_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsClosed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadStatuses_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadTags_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pipelines_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ScoringRuleSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringRuleSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScoringRuleSets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SuppressionEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ValueNormalized = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false),
                    ValueHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuppressionEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SuppressionEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PipelineStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PipelineId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineStages_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PipelineStages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ScoringRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RuleSetId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Dimension = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Target = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScoringRules_ScoringRuleSets_RuleSetId",
                        column: x => x.RuleSetId,
                        principalTable: "ScoringRuleSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScoringRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    NormalizedName = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    DedupKey = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    CategoryId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Country = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Province = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    City = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    District = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Area = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Address = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Website = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    WebsiteDomain = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    PipelineId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PipelineStageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    StatusId = table.Column<Guid>(type: "char(36)", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Score = table.Column<int>(type: "int", nullable: false),
                    ScoreRuleVersion = table.Column<int>(type: "int", nullable: true),
                    ScoredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Confidence = table.Column<int>(type: "int", nullable: false),
                    PrimarySourceType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    IsArchived = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Leads_LeadCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "LeadCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_LeadStatuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "LeadStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    PhoneRaw = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    PhoneE164 = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    DataOrigin = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PhoneVerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    IsPrimary = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    PhoneUniqueKey = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    EmailUniqueKey = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contacts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ImportRowErrors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ImportJobId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Column = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    RowData = table.Column<string>(type: "varchar(8000)", maxLength: 8000, nullable: true),
                    DuplicateLeadId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Resolution = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRowErrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportRowErrors_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportRowErrors_Leads_DuplicateLeadId",
                        column: x => x.DuplicateLeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportRowErrors_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Summary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    Payload = table.Column<string>(type: "longtext", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadActivities_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadActivities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Body = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadNotes_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadNotes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    RuleVersion = table.Column<int>(type: "int", nullable: false),
                    Breakdown = table.Column<string>(type: "longtext", nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadScores_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadScores_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Attribution = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadSources_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadSources_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadTagMaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TagId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadTagMaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadTagMaps_LeadTags_TagId",
                        column: x => x.TagId,
                        principalTable: "LeadTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeadTagMaps_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeadTagMaps_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Opportunities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PipelineStageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    ExpectedCloseDateUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Opportunities_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ConsentRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    IsRevocation = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Source = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    EvidenceRef = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentRecords_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsentRecords_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OpportunityActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    Summary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityActivities_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunityActivities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_ContactId",
                table: "ConsentRecords",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_TenantId",
                table: "ConsentRecords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_TenantId_ContactId_Channel_Purpose_OccurredAt~",
                table: "ConsentRecords",
                columns: new[] { "TenantId", "ContactId", "Channel", "Purpose", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_TenantId_OccurredAtUtc",
                table: "ConsentRecords",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_LeadId",
                table: "Contacts",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId",
                table: "Contacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_Email",
                table: "Contacts",
                columns: new[] { "TenantId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_LeadId_EmailUniqueKey",
                table: "Contacts",
                columns: new[] { "TenantId", "LeadId", "EmailUniqueKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_LeadId_PhoneUniqueKey",
                table: "Contacts",
                columns: new[] { "TenantId", "LeadId", "PhoneUniqueKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_PhoneE164",
                table: "Contacts",
                columns: new[] { "TenantId", "PhoneE164" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_TenantId",
                table: "ImportJobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_TenantId_CreatedAtUtc",
                table: "ImportJobs",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowErrors_DuplicateLeadId",
                table: "ImportRowErrors",
                column: "DuplicateLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowErrors_ImportJobId",
                table: "ImportRowErrors",
                column: "ImportJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowErrors_TenantId",
                table: "ImportRowErrors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowErrors_TenantId_ImportJobId_Kind",
                table: "ImportRowErrors",
                columns: new[] { "TenantId", "ImportJobId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowErrors_TenantId_ImportJobId_RowNumber",
                table: "ImportRowErrors",
                columns: new[] { "TenantId", "ImportJobId", "RowNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_LeadId",
                table: "LeadActivities",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_TenantId",
                table: "LeadActivities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_TenantId_LeadId_OccurredAtUtc",
                table: "LeadActivities",
                columns: new[] { "TenantId", "LeadId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_TenantId_OccurredAtUtc",
                table: "LeadActivities",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadCategories_TenantId",
                table: "LeadCategories",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadCategories_TenantId_NormalizedName",
                table: "LeadCategories",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadNotes_LeadId",
                table: "LeadNotes",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadNotes_TenantId",
                table: "LeadNotes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadNotes_TenantId_LeadId_CreatedAtUtc",
                table: "LeadNotes",
                columns: new[] { "TenantId", "LeadId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_CategoryId",
                table: "Leads",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_PipelineId",
                table: "Leads",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_PipelineStageId",
                table: "Leads",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_StatusId",
                table: "Leads",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId",
                table: "Leads",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_CategoryId",
                table: "Leads",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_City",
                table: "Leads",
                columns: new[] { "TenantId", "City" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_DedupKey",
                table: "Leads",
                columns: new[] { "TenantId", "DedupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_IsArchived_CreatedAtUtc",
                table: "Leads",
                columns: new[] { "TenantId", "IsArchived", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_NormalizedName",
                table: "Leads",
                columns: new[] { "TenantId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_OwnerUserId",
                table: "Leads",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_PipelineStageId",
                table: "Leads",
                columns: new[] { "TenantId", "PipelineStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_Score",
                table: "Leads",
                columns: new[] { "TenantId", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_StatusId",
                table: "Leads",
                columns: new[] { "TenantId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_WebsiteDomain",
                table: "Leads",
                columns: new[] { "TenantId", "WebsiteDomain" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadScores_LeadId",
                table: "LeadScores",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadScores_TenantId",
                table: "LeadScores",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadScores_TenantId_LeadId_CalculatedAtUtc",
                table: "LeadScores",
                columns: new[] { "TenantId", "LeadId", "CalculatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadSources_LeadId",
                table: "LeadSources",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadSources_TenantId",
                table: "LeadSources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadSources_TenantId_LeadId",
                table: "LeadSources",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadSources_TenantId_Type_ExternalId",
                table: "LeadSources",
                columns: new[] { "TenantId", "Type", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadStatuses_TenantId",
                table: "LeadStatuses",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadStatuses_TenantId_Name",
                table: "LeadStatuses",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadTagMaps_LeadId_TagId",
                table: "LeadTagMaps",
                columns: new[] { "LeadId", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadTagMaps_TagId",
                table: "LeadTagMaps",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadTagMaps_TenantId",
                table: "LeadTagMaps",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadTagMaps_TenantId_TagId",
                table: "LeadTagMaps",
                columns: new[] { "TenantId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadTags_TenantId",
                table: "LeadTags",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadTags_TenantId_NormalizedName",
                table: "LeadTags",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_LeadId",
                table: "Opportunities",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_PipelineStageId",
                table: "Opportunities",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId",
                table: "Opportunities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_LeadId",
                table: "Opportunities",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_PipelineStageId",
                table: "Opportunities",
                columns: new[] { "TenantId", "PipelineStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityActivities_OpportunityId",
                table: "OpportunityActivities",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityActivities_TenantId",
                table: "OpportunityActivities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityActivities_TenantId_OpportunityId_OccurredAtUtc",
                table: "OpportunityActivities",
                columns: new[] { "TenantId", "OpportunityId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_TenantId",
                table: "Pipelines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_TenantId_Name",
                table: "Pipelines",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_PipelineId_Name",
                table: "PipelineStages",
                columns: new[] { "PipelineId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_TenantId",
                table: "PipelineStages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_TenantId_PipelineId_Position",
                table: "PipelineStages",
                columns: new[] { "TenantId", "PipelineId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_RuleSetId",
                table: "ScoringRules",
                column: "RuleSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_TenantId",
                table: "ScoringRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_TenantId_RuleSetId",
                table: "ScoringRules",
                columns: new[] { "TenantId", "RuleSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRuleSets_TenantId",
                table: "ScoringRuleSets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRuleSets_TenantId_IsActive",
                table: "ScoringRuleSets",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRuleSets_TenantId_Version",
                table: "ScoringRuleSets",
                columns: new[] { "TenantId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionEntries_TenantId",
                table: "SuppressionEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionEntries_TenantId_Channel_ValueNormalized",
                table: "SuppressionEntries",
                columns: new[] { "TenantId", "Channel", "ValueNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionEntries_TenantId_ValueHash",
                table: "SuppressionEntries",
                columns: new[] { "TenantId", "ValueHash" });

            migrationBuilder.CreateIndex(
                name: "IX_SuppressionEntries_TenantId_ValueNormalized",
                table: "SuppressionEntries",
                columns: new[] { "TenantId", "ValueNormalized" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsentRecords");

            migrationBuilder.DropTable(
                name: "ImportRowErrors");

            migrationBuilder.DropTable(
                name: "LeadActivities");

            migrationBuilder.DropTable(
                name: "LeadNotes");

            migrationBuilder.DropTable(
                name: "LeadScores");

            migrationBuilder.DropTable(
                name: "LeadSources");

            migrationBuilder.DropTable(
                name: "LeadTagMaps");

            migrationBuilder.DropTable(
                name: "OpportunityActivities");

            migrationBuilder.DropTable(
                name: "ScoringRules");

            migrationBuilder.DropTable(
                name: "SuppressionEntries");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "ImportJobs");

            migrationBuilder.DropTable(
                name: "LeadTags");

            migrationBuilder.DropTable(
                name: "Opportunities");

            migrationBuilder.DropTable(
                name: "ScoringRuleSets");

            migrationBuilder.DropTable(
                name: "Leads");

            migrationBuilder.DropTable(
                name: "LeadCategories");

            migrationBuilder.DropTable(
                name: "LeadStatuses");

            migrationBuilder.DropTable(
                name: "PipelineStages");

            migrationBuilder.DropTable(
                name: "Pipelines");
        }
    }
}
