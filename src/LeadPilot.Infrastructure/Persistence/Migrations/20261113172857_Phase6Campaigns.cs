using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6Campaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CampaignId",
                table: "Messages",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CampaignRecipientId",
                table: "Messages",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    TemplateId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: true),
                    MessageBodySnapshot = table.Column<string>(type: "text", nullable: true),
                    IncludeOptOutFooter = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OptOutFooterSnapshot = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    AudienceJson = table.Column<string>(type: "text", nullable: false),
                    AudienceVersion = table.Column<int>(type: "int", nullable: false),
                    ParentCampaignId = table.Column<Guid>(type: "char(36)", nullable: true),
                    FollowUpDelayDays = table.Column<int>(type: "int", nullable: true),
                    ScheduledAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ScheduleTimeZone = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    ScheduleJobId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    MinIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxRecipients = table.Column<int>(type: "int", nullable: false),
                    DailyLimit = table.Column<int>(type: "int", nullable: true),
                    FailureThresholdPercent = table.Column<int>(type: "int", nullable: true),
                    CooldownDays = table.Column<int>(type: "int", nullable: true),
                    AttributionWindowDays = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RejectedReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    PausedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    PauseReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    PausedAutomatically = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    NextRunAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastTickAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    SnapshotId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Campaigns_Campaigns_ParentCampaignId",
                        column: x => x.ParentCampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Campaigns_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Campaigns_MessageTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "MessageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Campaigns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignTenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CooldownDays = table.Column<int>(type: "int", nullable: false),
                    ApprovalRequired = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatorCannotApprove = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AttributionWindowDays = table.Column<int>(type: "int", nullable: false),
                    FailureThresholdPercent = table.Column<int>(type: "int", nullable: false),
                    FailureMinSample = table.Column<int>(type: "int", nullable: false),
                    OptOutFooter = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    MonthlyMessageLimit = table.Column<int>(type: "int", nullable: true),
                    TestNumbersJson = table.Column<string>(type: "text", nullable: false),
                    KillSwitch = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    KillSwitchAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    KillSwitchByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignTenantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignTenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignAudienceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CampaignId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AudienceVersion = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    TakenAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Candidates = table.Column<int>(type: "int", nullable: false),
                    Recipients = table.Column<int>(type: "int", nullable: false),
                    Excluded = table.Column<int>(type: "int", nullable: false),
                    ExclusionBreakdownJson = table.Column<string>(type: "text", nullable: false),
                    Truncated = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TakenByUserId = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignAudienceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignAudienceSnapshots_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignAudienceSnapshots_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CampaignId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Level = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Message = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignExecutionLogs_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignExecutionLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignStatsRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CampaignId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Total = table.Column<int>(type: "int", nullable: false),
                    Pending = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    Queued = table.Column<int>(type: "int", nullable: false),
                    Sent = table.Column<int>(type: "int", nullable: false),
                    Delivered = table.Column<int>(type: "int", nullable: false),
                    Read = table.Column<int>(type: "int", nullable: false),
                    Replied = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Cancelled = table.Column<int>(type: "int", nullable: false),
                    Conversions = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignStatsRollups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignStatsRollups_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignStatsRollups_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CampaignId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Destination = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true),
                    StatusDetail = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    EligibilityJson = table.Column<string>(type: "text", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    QueuedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RepliedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_CampaignAudienceSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "CampaignAudienceSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignConversions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CampaignId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RecipientId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Detail = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignConversions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignConversions_CampaignRecipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "CampaignRecipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignConversions_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignConversions_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignConversions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CampaignUsageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CampaignId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RecipientId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignUsageEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignUsageEntries_CampaignRecipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "CampaignRecipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignUsageEntries_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignUsageEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CampaignId",
                table: "Messages",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_CampaignId_Status",
                table: "Messages",
                columns: new[] { "TenantId", "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_CampaignRecipientId",
                table: "Messages",
                columns: new[] { "TenantId", "CampaignRecipientId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignAudienceSnapshots_CampaignId",
                table: "CampaignAudienceSnapshots",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignAudienceSnapshots_TenantId",
                table: "CampaignAudienceSnapshots",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignAudienceSnapshots_TenantId_CampaignId",
                table: "CampaignAudienceSnapshots",
                columns: new[] { "TenantId", "CampaignId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignConversions_CampaignId",
                table: "CampaignConversions",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignConversions_LeadId",
                table: "CampaignConversions",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignConversions_RecipientId",
                table: "CampaignConversions",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignConversions_TenantId",
                table: "CampaignConversions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignConversions_TenantId_CampaignId_OccurredAtUtc",
                table: "CampaignConversions",
                columns: new[] { "TenantId", "CampaignId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignConversions_TenantId_RecipientId_Kind_ReferenceId",
                table: "CampaignConversions",
                columns: new[] { "TenantId", "RecipientId", "Kind", "ReferenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignExecutionLogs_CampaignId",
                table: "CampaignExecutionLogs",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignExecutionLogs_TenantId",
                table: "CampaignExecutionLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignExecutionLogs_TenantId_CampaignId_OccurredAtUtc",
                table: "CampaignExecutionLogs",
                columns: new[] { "TenantId", "CampaignId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_CampaignId_ContactId",
                table: "CampaignRecipients",
                columns: new[] { "CampaignId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_ContactId",
                table: "CampaignRecipients",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_ConversationId",
                table: "CampaignRecipients",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_LeadId",
                table: "CampaignRecipients",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_MessageId",
                table: "CampaignRecipients",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_SnapshotId",
                table: "CampaignRecipients",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_TenantId",
                table: "CampaignRecipients",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_TenantId_CampaignId_Status_Sequence",
                table: "CampaignRecipients",
                columns: new[] { "TenantId", "CampaignId", "Status", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_TenantId_ContactId_QueuedAtUtc",
                table: "CampaignRecipients",
                columns: new[] { "TenantId", "ContactId", "QueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_TenantId_LeadId",
                table: "CampaignRecipients",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_TenantId_MessageId",
                table: "CampaignRecipients",
                columns: new[] { "TenantId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_ChannelAccountId",
                table: "Campaigns",
                column: "ChannelAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_ParentCampaignId",
                table: "Campaigns",
                column: "ParentCampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TemplateId",
                table: "Campaigns",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId",
                table: "Campaigns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId_CreatedAtUtc",
                table: "Campaigns",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId_ParentCampaignId",
                table: "Campaigns",
                columns: new[] { "TenantId", "ParentCampaignId" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId_Status_ScheduledAtUtc",
                table: "Campaigns",
                columns: new[] { "TenantId", "Status", "ScheduledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignStatsRollups_CampaignId",
                table: "CampaignStatsRollups",
                column: "CampaignId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignStatsRollups_TenantId",
                table: "CampaignStatsRollups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignTenantSettings_TenantId",
                table: "CampaignTenantSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageEntries_CampaignId",
                table: "CampaignUsageEntries",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageEntries_RecipientId",
                table: "CampaignUsageEntries",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageEntries_TenantId",
                table: "CampaignUsageEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageEntries_TenantId_PeriodKey",
                table: "CampaignUsageEntries",
                columns: new[] { "TenantId", "PeriodKey" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignUsageEntries_TenantId_RecipientId_Round",
                table: "CampaignUsageEntries",
                columns: new[] { "TenantId", "RecipientId", "Round" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Campaigns_CampaignId",
                table: "Messages",
                column: "CampaignId",
                principalTable: "Campaigns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Campaigns_CampaignId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "CampaignConversions");

            migrationBuilder.DropTable(
                name: "CampaignExecutionLogs");

            migrationBuilder.DropTable(
                name: "CampaignStatsRollups");

            migrationBuilder.DropTable(
                name: "CampaignTenantSettings");

            migrationBuilder.DropTable(
                name: "CampaignUsageEntries");

            migrationBuilder.DropTable(
                name: "CampaignRecipients");

            migrationBuilder.DropTable(
                name: "CampaignAudienceSnapshots");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropIndex(
                name: "IX_Messages_CampaignId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_CampaignId_Status",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_CampaignRecipientId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CampaignRecipientId",
                table: "Messages");
        }
    }
}
