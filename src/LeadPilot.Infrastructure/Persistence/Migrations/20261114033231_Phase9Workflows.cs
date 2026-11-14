using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase9Workflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ActiveVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    IsKilled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    KilledAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    KillReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workflows_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowTenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    KillSwitch = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    KillSwitchAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    KillSwitchReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    MaxConcurrentExecutions = table.Column<int>(type: "int", nullable: false),
                    MonthlyExecutionLimit = table.Column<int>(type: "int", nullable: false),
                    DailyMessageLimit = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTenantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowTenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ReEntry = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    CooldownHours = table.Column<int>(type: "int", nullable: false),
                    MaxStartsPerLeadPer30Days = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowVersions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TriggerEventId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TriggerKey = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    CurrentStepKey = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    StatusReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    StartedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ActiveSlot = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutions_Leads_EntityId",
                        column: x => x.EntityId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutions_WorkflowVersions_WorkflowVersionId",
                        column: x => x.WorkflowVersionId,
                        principalTable: "WorkflowVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Key = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: false),
                    ConditionJson = table.Column<string>(type: "text", nullable: true),
                    OnFailure = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    MaxRetries = table.Column<int>(type: "int", nullable: false),
                    IsSealed = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowSteps_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowSteps_WorkflowVersions_WorkflowVersionId",
                        column: x => x.WorkflowVersionId,
                        principalTable: "WorkflowVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowTriggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    EventType = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    ConditionJson = table.Column<string>(type: "text", nullable: true),
                    ConfigJson = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    IsSealed = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTriggers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowTriggers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTriggers_WorkflowVersions_WorkflowVersionId",
                        column: x => x.WorkflowVersionId,
                        principalTable: "WorkflowVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkflowExecutionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    StepKey = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    StepName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    StepType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    EffectRef = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutionLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowExecutionLogs_WorkflowExecutions_WorkflowExecutionId",
                        column: x => x.WorkflowExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WorkflowNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: true),
                    WorkflowExecutionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    StepKey = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Body = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowNotifications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowNotifications_WorkflowExecutions_WorkflowExecutionId",
                        column: x => x.WorkflowExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutionLogs_TenantId",
                table: "WorkflowExecutionLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutionLogs_WorkflowExecutionId_OccurredAtUtc",
                table: "WorkflowExecutionLogs",
                columns: new[] { "WorkflowExecutionId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_EntityId",
                table: "WorkflowExecutions",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_TenantId",
                table: "WorkflowExecutions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_TenantId_EntityId",
                table: "WorkflowExecutions",
                columns: new[] { "TenantId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_TenantId_Status_NextRunAtUtc",
                table: "WorkflowExecutions",
                columns: new[] { "TenantId", "Status", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_TenantId_WorkflowId_CreatedAtUtc",
                table: "WorkflowExecutions",
                columns: new[] { "TenantId", "WorkflowId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_WorkflowId_EntityId_ActiveSlot",
                table: "WorkflowExecutions",
                columns: new[] { "WorkflowId", "EntityId", "ActiveSlot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_WorkflowVersionId_TriggerEventId_EntityId",
                table: "WorkflowExecutions",
                columns: new[] { "WorkflowVersionId", "TriggerEventId", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotifications_TenantId",
                table: "WorkflowNotifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotifications_TenantId_UserId_ReadAtUtc",
                table: "WorkflowNotifications",
                columns: new[] { "TenantId", "UserId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotifications_WorkflowExecutionId_StepKey_UserId",
                table: "WorkflowNotifications",
                columns: new[] { "WorkflowExecutionId", "StepKey", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_TenantId",
                table: "Workflows",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_TenantId_IsDeleted_Status",
                table: "Workflows",
                columns: new[] { "TenantId", "IsDeleted", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_TenantId_Name",
                table: "Workflows",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSteps_TenantId",
                table: "WorkflowSteps",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSteps_WorkflowVersionId_Key",
                table: "WorkflowSteps",
                columns: new[] { "WorkflowVersionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSteps_WorkflowVersionId_Position",
                table: "WorkflowSteps",
                columns: new[] { "WorkflowVersionId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTenantSettings_TenantId",
                table: "WorkflowTenantSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTriggers_TenantId",
                table: "WorkflowTriggers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTriggers_TenantId_EventType",
                table: "WorkflowTriggers",
                columns: new[] { "TenantId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTriggers_WorkflowVersionId_EventType",
                table: "WorkflowTriggers",
                columns: new[] { "WorkflowVersionId", "EventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_TenantId",
                table: "WorkflowVersions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_TenantId_WorkflowId_Number",
                table: "WorkflowVersions",
                columns: new[] { "TenantId", "WorkflowId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_WorkflowId",
                table: "WorkflowVersions",
                column: "WorkflowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowExecutionLogs");

            migrationBuilder.DropTable(
                name: "WorkflowNotifications");

            migrationBuilder.DropTable(
                name: "WorkflowSteps");

            migrationBuilder.DropTable(
                name: "WorkflowTenantSettings");

            migrationBuilder.DropTable(
                name: "WorkflowTriggers");

            migrationBuilder.DropTable(
                name: "WorkflowExecutions");

            migrationBuilder.DropTable(
                name: "WorkflowVersions");

            migrationBuilder.DropTable(
                name: "Workflows");
        }
    }
}
