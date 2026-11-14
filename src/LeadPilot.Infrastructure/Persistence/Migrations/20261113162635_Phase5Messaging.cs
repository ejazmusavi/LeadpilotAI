using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Provider = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CausationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastError = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConsumerName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessedMessages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ChannelAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChannelId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DisplayName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    GatewayAccountId = table.Column<string>(type: "varchar(63)", maxLength: 63, nullable: false),
                    WebhookKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ConnectionStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ConnectionStatusAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    PhoneE164 = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    PushName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Engine = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    IsPaused = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PausedAutomatically = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PauseReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    PausedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    NewContactBlockedUntilUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    HourlyCap = table.Column<int>(type: "int", nullable: false),
                    DailyCap = table.Column<int>(type: "int", nullable: false),
                    MinDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    WarmupStartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastSendAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelAccounts_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelAccounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ChannelAccountNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Message = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelAccountNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelAccountNotifications_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelAccountNotifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ChannelCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ProtectedBaseUrl = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    ProtectedWebhookSecret = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    KeyHint = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelCredentials_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelCredentials_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ExternalChatId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    PeerPhoneE164 = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    PeerName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    AiMode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    LastMessageAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastInboundAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastOutboundAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastMessagePreview = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    OptedOutAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ExternalEventId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "longtext", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SourceIp = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookEvents_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebhookEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ConversationAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Direction = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ExternalMessageId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    TemplateId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    SendAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    SendStartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    FailureMessage = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_ChannelAccounts_ChannelAccountId",
                        column: x => x.ChannelAccountId,
                        principalTable: "ChannelAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_MessageTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "MessageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    FileName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    MimeType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    RejectionReason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    StorageKey = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageAttachments_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageAttachments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MessageStatusEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    GatewayAck = table.Column<int>(type: "int", nullable: true),
                    Applied = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageStatusEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageStatusEvents_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageStatusEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccountNotifications_ChannelAccountId",
                table: "ChannelAccountNotifications",
                column: "ChannelAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccountNotifications_TenantId",
                table: "ChannelAccountNotifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccountNotifications_TenantId_ChannelAccountId_Create~",
                table: "ChannelAccountNotifications",
                columns: new[] { "TenantId", "ChannelAccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_ChannelId",
                table: "ChannelAccounts",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_GatewayAccountId",
                table: "ChannelAccounts",
                column: "GatewayAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_TenantId",
                table: "ChannelAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAccounts_WebhookKey",
                table: "ChannelAccounts",
                column: "WebhookKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelCredentials_ChannelAccountId",
                table: "ChannelCredentials",
                column: "ChannelAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelCredentials_TenantId",
                table: "ChannelCredentials",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_TenantId",
                table: "Channels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_TenantId_Type_Provider",
                table: "Channels",
                columns: new[] { "TenantId", "Type", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_ConversationId",
                table: "ConversationAssignments",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_TenantId",
                table: "ConversationAssignments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_TenantId_ConversationId_AssignedAtUtc",
                table: "ConversationAssignments",
                columns: new[] { "TenantId", "ConversationId", "AssignedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ChannelAccountId",
                table: "Conversations",
                column: "ChannelAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ContactId",
                table: "Conversations",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId",
                table: "Conversations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_ChannelAccountId_ExternalChatId",
                table: "Conversations",
                columns: new[] { "TenantId", "ChannelAccountId", "ExternalChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_ContactId",
                table: "Conversations",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_LastMessageAtUtc",
                table: "Conversations",
                columns: new[] { "TenantId", "LastMessageAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_MessageId",
                table: "MessageAttachments",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_TenantId",
                table: "MessageAttachments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_TenantId_MessageId",
                table: "MessageAttachments",
                columns: new[] { "TenantId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChannelAccountId",
                table: "Messages",
                column: "ChannelAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ContactId",
                table: "Messages",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Status_NextAttemptAtUtc",
                table: "Messages",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TemplateId",
                table: "Messages",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId",
                table: "Messages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_ChannelAccountId_ExternalMessageId",
                table: "Messages",
                columns: new[] { "TenantId", "ChannelAccountId", "ExternalMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_ChannelAccountId_SentAtUtc",
                table: "Messages",
                columns: new[] { "TenantId", "ChannelAccountId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_ConversationId_OccurredAtUtc",
                table: "Messages",
                columns: new[] { "TenantId", "ConversationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_IdempotencyKey",
                table: "Messages",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageStatusEvents_MessageId",
                table: "MessageStatusEvents",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageStatusEvents_TenantId",
                table: "MessageStatusEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageStatusEvents_TenantId_MessageId_RecordedAtUtc",
                table: "MessageStatusEvents",
                columns: new[] { "TenantId", "MessageId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId",
                table: "MessageTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId_Name",
                table: "MessageTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_NextAttemptAtUtc",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId",
                table: "OutboxMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId_CreatedAtUtc",
                table: "OutboxMessages",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedMessages_TenantId",
                table: "ProcessedMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedMessages_TenantId_ConsumerName_MessageId",
                table: "ProcessedMessages",
                columns: new[] { "TenantId", "ConsumerName", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_ChannelAccountId_ExternalEventId",
                table: "WebhookEvents",
                columns: new[] { "ChannelAccountId", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_ExpiresAtUtc",
                table: "WebhookEvents",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_TenantId",
                table: "WebhookEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_TenantId_ReceivedAtUtc",
                table: "WebhookEvents",
                columns: new[] { "TenantId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_TenantId_Status",
                table: "WebhookEvents",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelAccountNotifications");

            migrationBuilder.DropTable(
                name: "ChannelCredentials");

            migrationBuilder.DropTable(
                name: "ConversationAssignments");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "MessageStatusEvents");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "ProcessedMessages");

            migrationBuilder.DropTable(
                name: "WebhookEvents");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "MessageTemplates");

            migrationBuilder.DropTable(
                name: "ChannelAccounts");

            migrationBuilder.DropTable(
                name: "Channels");
        }
    }
}
