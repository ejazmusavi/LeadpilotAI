using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7AiKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiInteractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TriggerMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Intent = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Decision = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    DraftText = table.Column<string>(type: "text", nullable: true),
                    CitationsJson = table.Column<string>(type: "text", nullable: false),
                    ValidationJson = table.Column<string>(type: "text", nullable: false),
                    EscalationReason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    NextAction = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    ExtractedJson = table.Column<string>(type: "text", nullable: false),
                    InjectionSuspected = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    RetrievalLogId = table.Column<Guid>(type: "char(36)", nullable: true),
                    HandledByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    HandledAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInteractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiInteractions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiInteractions_Messages_TriggerMessageId",
                        column: x => x.TriggerMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiInteractions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AiTenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    KeyHint = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    ChatModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    ClassifierModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    SummaryModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    ProtectedEmbeddingKey = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    EmbeddingKeyHint = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    EmbeddingModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    TopK = table.Column<int>(type: "int", nullable: false),
                    MinRetrievalScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    MinConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    HistoryMessages = table.Column<int>(type: "int", nullable: false),
                    MaxContextChars = table.Column<int>(type: "int", nullable: false),
                    MonthlyTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    MonthlyBudgetUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    UnsupportedReply = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    ToneInstructions = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    EscalationKeywords = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiTenantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiTenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AiUsageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Operation = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Model = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    CacheReadTokens = table.Column<int>(type: "int", nullable: false),
                    CacheWriteTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Success = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiUsageEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ConversationSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    CoveredUntilUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CoveredMessageCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationSummaries_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationSummaries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "KnowledgeSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Url = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    LastSyncAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastSyncError = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeSources_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RetrievalLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Source = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    QueryHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    QueryLength = table.Column<int>(type: "int", nullable: false),
                    QueryText = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    TopK = table.Column<int>(type: "int", nullable: false),
                    MinScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    TopScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    ResultsJson = table.Column<string>(type: "text", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    StoreName = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetrievalLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RetrievalLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AiToolExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    InteractionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ToolName = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    ArgumentsSummary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiToolExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiToolExecutions_AiInteractions_InteractionId",
                        column: x => x.InteractionId,
                        principalTable: "AiInteractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiToolExecutions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SourceId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    SourceUrl = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CurrentVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_KnowledgeSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "KnowledgeSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DocumentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DocumentId = table.Column<Guid>(type: "char(36)", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "varchar(260)", maxLength: 260, nullable: true),
                    MimeType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ChunkCount = table.Column<int>(type: "int", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    IndexedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    IndexingStartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentVersions_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DocumentId = table.Column<Guid>(type: "char(36)", nullable: false),
                    VersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Heading = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    CharCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentChunks_DocumentVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentChunks_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentChunks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "EmbeddingRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChunkId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DocumentId = table.Column<Guid>(type: "char(36)", nullable: false),
                    VersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Provider = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Model = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Vector = table.Column<byte[]>(type: "longblob", nullable: false),
                    StoreName = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmbeddingRecords_DocumentChunks_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "DocumentChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmbeddingRecords_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_ConversationId",
                table: "AiInteractions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_TenantId",
                table: "AiInteractions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc",
                table: "AiInteractions",
                columns: new[] { "TenantId", "ConversationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_TenantId_Status_CreatedAtUtc",
                table: "AiInteractions",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_TenantId_TriggerMessageId",
                table: "AiInteractions",
                columns: new[] { "TenantId", "TriggerMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_TriggerMessageId",
                table: "AiInteractions",
                column: "TriggerMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_AiTenantSettings_TenantId",
                table: "AiTenantSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_InteractionId",
                table: "AiToolExecutions",
                column: "InteractionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_TenantId",
                table: "AiToolExecutions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiToolExecutions_TenantId_InteractionId",
                table: "AiToolExecutions",
                columns: new[] { "TenantId", "InteractionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEntries_TenantId",
                table: "AiUsageEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEntries_TenantId_OccurredAtUtc",
                table: "AiUsageEntries",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEntries_TenantId_PeriodKey",
                table: "AiUsageEntries",
                columns: new[] { "TenantId", "PeriodKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSummaries_ConversationId",
                table: "ConversationSummaries",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSummaries_TenantId",
                table: "ConversationSummaries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSummaries_TenantId_ConversationId",
                table: "ConversationSummaries",
                columns: new[] { "TenantId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_DocumentId",
                table: "DocumentChunks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_TenantId",
                table: "DocumentChunks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_TenantId_DocumentId",
                table: "DocumentChunks",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_VersionId_Ordinal",
                table: "DocumentChunks",
                columns: new[] { "VersionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_DocumentId_VersionNumber",
                table: "DocumentVersions",
                columns: new[] { "DocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_TenantId",
                table: "DocumentVersions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_TenantId_Status",
                table: "DocumentVersions",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingRecords_ChunkId_Model",
                table: "EmbeddingRecords",
                columns: new[] { "ChunkId", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingRecords_TenantId",
                table: "EmbeddingRecords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingRecords_TenantId_DocumentId",
                table: "EmbeddingRecords",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingRecords_TenantId_Model",
                table: "EmbeddingRecords",
                columns: new[] { "TenantId", "Model" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_SourceId",
                table: "KnowledgeDocuments",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_TenantId",
                table: "KnowledgeDocuments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_TenantId_SourceId",
                table: "KnowledgeDocuments",
                columns: new[] { "TenantId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_TenantId_SourceId_SourceUrl",
                table: "KnowledgeDocuments",
                columns: new[] { "TenantId", "SourceId", "SourceUrl" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_TenantId_Status",
                table: "KnowledgeDocuments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSources_TenantId",
                table: "KnowledgeSources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSources_TenantId_Type",
                table: "KnowledgeSources",
                columns: new[] { "TenantId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalLogs_TenantId",
                table: "RetrievalLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalLogs_TenantId_ConversationId",
                table: "RetrievalLogs",
                columns: new[] { "TenantId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalLogs_TenantId_CreatedAtUtc",
                table: "RetrievalLogs",
                columns: new[] { "TenantId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiTenantSettings");

            migrationBuilder.DropTable(
                name: "AiToolExecutions");

            migrationBuilder.DropTable(
                name: "AiUsageEntries");

            migrationBuilder.DropTable(
                name: "ConversationSummaries");

            migrationBuilder.DropTable(
                name: "EmbeddingRecords");

            migrationBuilder.DropTable(
                name: "RetrievalLogs");

            migrationBuilder.DropTable(
                name: "AiInteractions");

            migrationBuilder.DropTable(
                name: "DocumentChunks");

            migrationBuilder.DropTable(
                name: "DocumentVersions");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.DropTable(
                name: "KnowledgeSources");
        }
    }
}
