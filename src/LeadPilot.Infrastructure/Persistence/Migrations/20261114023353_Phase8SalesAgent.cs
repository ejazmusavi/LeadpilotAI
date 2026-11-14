using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8SalesAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiEpoch",
                table: "Messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AiInteractionId",
                table: "Messages",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAiGenerated",
                table: "Messages",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AiEpoch",
                table: "Conversations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AiModeChangedAtUtc",
                table: "Conversations",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAtUtc",
                table: "Conversations",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationReason",
                table: "Conversations",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "InteractionId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "char(36)");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovalRequestId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DedupeKey",
                table: "AiToolExecutions",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "AiToolExecutions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutedByUserId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputJson",
                table: "AiToolExecutions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeadId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputJson",
                table: "AiToolExecutions",
                type: "mediumtext",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TriggerMessageId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "AiInteractions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoSend",
                table: "AiInteractions",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AutoSendNote",
                table: "AiInteractions",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SentMessageId",
                table: "AiInteractions",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentTenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AiRepliesAllowed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MaxAutoRepliesPerConversationPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxTrialsPerLead = table.Column<int>(type: "int", nullable: false),
                    ApprovalLifetimeHours = table.Column<int>(type: "int", nullable: false),
                    MaxOpenFollowUpsPerLead = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTenantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AgentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ToolName = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Reason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    DedupeKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Execution = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ExecutionNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecisionNote = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CatalogProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Summary = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    Features = table.Column<string>(type: "text", nullable: true),
                    DemoProcess = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    TrialProcess = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsSample = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogProducts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ConversationNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Body = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Author = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationNotes_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationNotes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FollowUps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AssignedUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    DueAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedByKind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FollowUps_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FollowUps_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "HandoffEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Actor = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    FromState = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    ToState = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HandoffEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HandoffEvents_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HandoffEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeadQualifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AgentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AnswersJson = table.Column<string>(type: "text", nullable: false),
                    Fit = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadQualifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadQualifications_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadQualifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SalesAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsSample = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ChatModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    MinConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    EnabledToolsJson = table.Column<string>(type: "text", nullable: false),
                    AutoReplyEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OffTopicReply = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesAgents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesAgents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PricingPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ProductId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    BillingPeriod = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Details = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    MaxDiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsSample = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PricingPlans_CatalogProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "CatalogProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PricingPlans_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AgentQualificationQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AgentId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Key = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Question = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    AnswerType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ChoicesJson = table.Column<string>(type: "text", nullable: true),
                    Required = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentQualificationQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentQualificationQuestions_SalesAgents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "SalesAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentQualificationQuestions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_TenantId_DedupeKey",
                table: "AiToolExecutions",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_TenantId_LeadId_ToolName",
                table: "AiToolExecutions",
                columns: new[] { "TenantId", "LeadId", "ToolName" });

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_TenantId_OccurredAtUtc",
                table: "AiToolExecutions",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_TenantId_TriggerMessageId",
                table: "AiToolExecutions",
                columns: new[] { "TenantId", "TriggerMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentQualificationQuestions_AgentId",
                table: "AgentQualificationQuestions",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentQualificationQuestions_TenantId",
                table: "AgentQualificationQuestions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentQualificationQuestions_TenantId_AgentId_Key",
                table: "AgentQualificationQuestions",
                columns: new[] { "TenantId", "AgentId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTenantSettings_TenantId",
                table: "AgentTenantSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_ConversationId",
                table: "ApprovalRequests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId",
                table: "ApprovalRequests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_DedupeKey",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_Status_RequestedAtUtc",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "Status", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogProducts_TenantId",
                table: "CatalogProducts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogProducts_TenantId_IsActive",
                table: "CatalogProducts",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNotes_ConversationId",
                table: "ConversationNotes",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNotes_TenantId",
                table: "ConversationNotes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNotes_TenantId_ConversationId_CreatedAtUtc",
                table: "ConversationNotes",
                columns: new[] { "TenantId", "ConversationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUps_LeadId",
                table: "FollowUps",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowUps_TenantId",
                table: "FollowUps",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowUps_TenantId_LeadId_Status",
                table: "FollowUps",
                columns: new[] { "TenantId", "LeadId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUps_TenantId_Status_DueAtUtc",
                table: "FollowUps",
                columns: new[] { "TenantId", "Status", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HandoffEvents_ConversationId",
                table: "HandoffEvents",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_HandoffEvents_TenantId",
                table: "HandoffEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_HandoffEvents_TenantId_ConversationId_OccurredAtUtc",
                table: "HandoffEvents",
                columns: new[] { "TenantId", "ConversationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadQualifications_LeadId",
                table: "LeadQualifications",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadQualifications_TenantId",
                table: "LeadQualifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadQualifications_TenantId_LeadId",
                table: "LeadQualifications",
                columns: new[] { "TenantId", "LeadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PricingPlans_ProductId",
                table: "PricingPlans",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PricingPlans_TenantId",
                table: "PricingPlans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PricingPlans_TenantId_ProductId",
                table: "PricingPlans",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesAgents_TenantId",
                table: "SalesAgents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesAgents_TenantId_IsDefault",
                table: "SalesAgents",
                columns: new[] { "TenantId", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentQualificationQuestions");

            migrationBuilder.DropTable(
                name: "AgentTenantSettings");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "ConversationNotes");

            migrationBuilder.DropTable(
                name: "FollowUps");

            migrationBuilder.DropTable(
                name: "HandoffEvents");

            migrationBuilder.DropTable(
                name: "LeadQualifications");

            migrationBuilder.DropTable(
                name: "PricingPlans");

            migrationBuilder.DropTable(
                name: "SalesAgents");

            migrationBuilder.DropTable(
                name: "CatalogProducts");

            migrationBuilder.DropIndex(
                name: "IX_AiToolExecutions_TenantId_DedupeKey",
                table: "AiToolExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AiToolExecutions_TenantId_LeadId_ToolName",
                table: "AiToolExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AiToolExecutions_TenantId_OccurredAtUtc",
                table: "AiToolExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AiToolExecutions_TenantId_TriggerMessageId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "AiEpoch",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AiInteractionId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsAiGenerated",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AiEpoch",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AiModeChangedAtUtc",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "EscalatedAtUtc",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "EscalationReason",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "DedupeKey",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "ExecutedByUserId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "InputJson",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "LeadId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "OutputJson",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "TriggerMessageId",
                table: "AiToolExecutions");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "AiInteractions");

            migrationBuilder.DropColumn(
                name: "AutoSend",
                table: "AiInteractions");

            migrationBuilder.DropColumn(
                name: "AutoSendNote",
                table: "AiInteractions");

            migrationBuilder.DropColumn(
                name: "SentMessageId",
                table: "AiInteractions");

            migrationBuilder.AlterColumn<Guid>(
                name: "InteractionId",
                table: "AiToolExecutions",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true);
        }
    }
}
