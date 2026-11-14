using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase10Conversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversionSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SlotMinutes = table.Column<int>(type: "int", nullable: false),
                    MinNoticeMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxDaysAhead = table.Column<int>(type: "int", nullable: false),
                    Assignment = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    MeetingLinkTemplate = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    ConfirmAutomatedBookings = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RemindersEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ReminderChannelAccountId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ReminderOffsets = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    ReminderBody = table.Column<string>(type: "varchar(600)", maxLength: 600, nullable: false),
                    TrialDefaultDays = table.Column<int>(type: "int", nullable: false),
                    TrialExpiryNoticeHours = table.Column<int>(type: "int", nullable: false),
                    TrialCustomerNoticesEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TrialNoticeBody = table.Column<string>(type: "varchar(600)", maxLength: 600, nullable: false),
                    PaymentDueDays = table.Column<int>(type: "int", nullable: false),
                    PaymentInstructions = table.Column<string>(type: "varchar(1500)", maxLength: 1500, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversionSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversionSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PrimaryContactId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    AccountReference = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    AccountNotes = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ConvertedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ConvertedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Contacts_PrimaryContactId",
                        column: x => x.PrimaryContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DemoReps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    BufferMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxPerDay = table.Column<int>(type: "int", nullable: false),
                    LastAssignedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoReps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoReps_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoReps_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Demos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true),
                    RepUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    MeetingLink = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CancelReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    ActiveSlot = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Demos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Demos_AspNetUsers_RepUserId",
                        column: x => x.RepUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Demos_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Demos_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Demos_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ProviderKey = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    ExternalEventId = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    PaymentRequestId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Outcome = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ProviderKey = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    WebhookId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SecretProtected = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProviderConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentProviderConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Trials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LeadId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContactId = table.Column<Guid>(type: "char(36)", nullable: true),
                    PlanId = table.Column<Guid>(type: "char(36)", nullable: true),
                    PlanName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    ProductName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    DurationDays = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ProvisioningNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ExternalAccountRef = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    LoginUrl = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LoginCount = table.Column<int>(type: "int", nullable: false),
                    ActiveUsers = table.Column<int>(type: "int", nullable: false),
                    UsageNotes = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ExpiryNoticeAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    EndReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    ConvertedCustomerId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trials_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trials_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trials_PricingPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "PricingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trials_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DemoAvailabilityExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RepId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Date = table.Column<DateTime>(type: "date", precision: 6, nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    StartMinute = table.Column<int>(type: "int", nullable: true),
                    EndMinute = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoAvailabilityExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoAvailabilityExceptions_DemoReps_RepId",
                        column: x => x.RepId,
                        principalTable: "DemoReps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoAvailabilityExceptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DemoAvailabilityRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RepId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    StartMinute = table.Column<int>(type: "int", nullable: false),
                    EndMinute = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoAvailabilityRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoAvailabilityRules_DemoReps_RepId",
                        column: x => x.RepId,
                        principalTable: "DemoReps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoAvailabilityRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DemoHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DemoId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Summary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ActorKind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoHistory_Demos_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Demos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoHistory_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DemoReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DemoId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    OffsetMinutes = table.Column<int>(type: "int", nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoReminders_Demos_DemoId",
                        column: x => x.DemoId,
                        principalTable: "Demos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoReminders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TrialEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TrialId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Summary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ActorKind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrialEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrialEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrialEvents_Trials_TrialId",
                        column: x => x.TrialId,
                        principalTable: "Trials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CommercialAgreements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CustomerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Terms = table.Column<string>(type: "text", nullable: false),
                    ListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    BillingCycle = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialAgreements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercialAgreements_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercialAgreements_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercialAgreements_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercialAgreements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CustomerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AgreementId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Reference = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Instructions = table.Column<string>(type: "varchar(1500)", maxLength: 1500, nullable: true),
                    PayUrl = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    RequestedVia = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ApprovalRequestId = table.Column<Guid>(type: "char(36)", nullable: true),
                    OpenForSubscription = table.Column<Guid>(type: "char(36)", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CloseReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRequests_CommercialAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CommercialAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentRequests_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentRequests_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PaymentRequestId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CustomerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    Method = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    EvidenceReference = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    EvidenceKey = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    VerifiedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ProviderKey = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    ProviderEventId = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Notes = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_PaymentRequests_PaymentRequestId",
                        column: x => x.PaymentRequestId,
                        principalTable: "PaymentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CustomerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PlanId = table.Column<Guid>(type: "char(36)", nullable: true),
                    PlanName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    ProductName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    BillingCycle = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ActivatedByPaymentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CancelReason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.CheckConstraint("CK_Subscriptions_ActiveNeedsPayment", "`Status` <> 'Active' OR `ActivatedByPaymentId` IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_Subscriptions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Payments_ActivatedByPaymentId",
                        column: x => x.ActivatedByPaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_PricingPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "PricingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialAgreements_ApprovedByUserId",
                table: "CommercialAgreements",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialAgreements_CustomerId",
                table: "CommercialAgreements",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialAgreements_OpportunityId",
                table: "CommercialAgreements",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialAgreements_SubscriptionId",
                table: "CommercialAgreements",
                column: "SubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialAgreements_TenantId",
                table: "CommercialAgreements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialAgreements_TenantId_OpportunityId",
                table: "CommercialAgreements",
                columns: new[] { "TenantId", "OpportunityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversionSettings_TenantId",
                table: "ConversionSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_LeadId",
                table: "Customers",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PrimaryContactId",
                table: "Customers",
                column: "PrimaryContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId",
                table: "Customers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_LeadId",
                table: "Customers",
                columns: new[] { "TenantId", "LeadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_Number",
                table: "Customers",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_Status",
                table: "Customers",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoAvailabilityExceptions_RepId",
                table: "DemoAvailabilityExceptions",
                column: "RepId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoAvailabilityExceptions_TenantId",
                table: "DemoAvailabilityExceptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoAvailabilityExceptions_TenantId_RepId_Date",
                table: "DemoAvailabilityExceptions",
                columns: new[] { "TenantId", "RepId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoAvailabilityRules_RepId",
                table: "DemoAvailabilityRules",
                column: "RepId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoAvailabilityRules_TenantId",
                table: "DemoAvailabilityRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoAvailabilityRules_TenantId_RepId_Day",
                table: "DemoAvailabilityRules",
                columns: new[] { "TenantId", "RepId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoHistory_DemoId_OccurredAtUtc",
                table: "DemoHistory",
                columns: new[] { "DemoId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoHistory_TenantId",
                table: "DemoHistory",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoReminders_DemoId_Generation_OffsetMinutes",
                table: "DemoReminders",
                columns: new[] { "DemoId", "Generation", "OffsetMinutes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoReminders_TenantId",
                table: "DemoReminders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoReminders_TenantId_Status_DueAtUtc",
                table: "DemoReminders",
                columns: new[] { "TenantId", "Status", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoReps_TenantId",
                table: "DemoReps",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoReps_TenantId_UserId",
                table: "DemoReps",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoReps_UserId",
                table: "DemoReps",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Demos_ContactId",
                table: "Demos",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Demos_LeadId",
                table: "Demos",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Demos_RepUserId",
                table: "Demos",
                column: "RepUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Demos_TenantId",
                table: "Demos",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Demos_TenantId_ActiveSlot",
                table: "Demos",
                columns: new[] { "TenantId", "ActiveSlot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Demos_TenantId_LeadId",
                table: "Demos",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_Demos_TenantId_RepUserId_StartUtc",
                table: "Demos",
                columns: new[] { "TenantId", "RepUserId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Demos_TenantId_Status_StartUtc",
                table: "Demos",
                columns: new[] { "TenantId", "Status", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_TenantId",
                table: "PaymentEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_TenantId_ProviderKey_ExternalEventId",
                table: "PaymentEvents",
                columns: new[] { "TenantId", "ProviderKey", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderConfigs_TenantId",
                table: "PaymentProviderConfigs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderConfigs_TenantId_ProviderKey",
                table: "PaymentProviderConfigs",
                columns: new[] { "TenantId", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderConfigs_WebhookId",
                table: "PaymentProviderConfigs",
                column: "WebhookId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_AgreementId",
                table: "PaymentRequests",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_CustomerId",
                table: "PaymentRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_SubscriptionId",
                table: "PaymentRequests",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_TenantId",
                table: "PaymentRequests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_TenantId_OpenForSubscription",
                table: "PaymentRequests",
                columns: new[] { "TenantId", "OpenForSubscription" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_TenantId_Reference",
                table: "PaymentRequests",
                columns: new[] { "TenantId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_TenantId_Status_CreatedAtUtc",
                table: "PaymentRequests",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CustomerId",
                table: "Payments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentRequestId",
                table: "Payments",
                column: "PaymentRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId",
                table: "Payments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_EvidenceKey",
                table: "Payments",
                columns: new[] { "TenantId", "EvidenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ActivatedByPaymentId",
                table: "Subscriptions",
                column: "ActivatedByPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CustomerId",
                table: "Subscriptions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PlanId",
                table: "Subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_TenantId",
                table: "Subscriptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_TenantId_CustomerId_Status",
                table: "Subscriptions",
                columns: new[] { "TenantId", "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialEvents_TenantId",
                table: "TrialEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TrialEvents_TrialId_OccurredAtUtc",
                table: "TrialEvents",
                columns: new[] { "TrialId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Trials_ContactId",
                table: "Trials",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_LeadId",
                table: "Trials",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_PlanId",
                table: "Trials",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_TenantId",
                table: "Trials",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_TenantId_LeadId",
                table: "Trials",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trials_TenantId_Status_ExpiresAtUtc",
                table: "Trials",
                columns: new[] { "TenantId", "Status", "ExpiresAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_CommercialAgreements_Subscriptions_SubscriptionId",
                table: "CommercialAgreements",
                column: "SubscriptionId",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Subscriptions_SubscriptionId",
                table: "PaymentRequests",
                column: "SubscriptionId",
                principalTable: "Subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CommercialAgreements_Customers_CustomerId",
                table: "CommercialAgreements");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Customers_CustomerId",
                table: "PaymentRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Customers_CustomerId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Customers_CustomerId",
                table: "Subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_CommercialAgreements_Subscriptions_SubscriptionId",
                table: "CommercialAgreements");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Subscriptions_SubscriptionId",
                table: "PaymentRequests");

            migrationBuilder.DropTable(
                name: "ConversionSettings");

            migrationBuilder.DropTable(
                name: "DemoAvailabilityExceptions");

            migrationBuilder.DropTable(
                name: "DemoAvailabilityRules");

            migrationBuilder.DropTable(
                name: "DemoHistory");

            migrationBuilder.DropTable(
                name: "DemoReminders");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "PaymentProviderConfigs");

            migrationBuilder.DropTable(
                name: "TrialEvents");

            migrationBuilder.DropTable(
                name: "DemoReps");

            migrationBuilder.DropTable(
                name: "Demos");

            migrationBuilder.DropTable(
                name: "Trials");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "PaymentRequests");

            migrationBuilder.DropTable(
                name: "CommercialAgreements");
        }
    }
}
