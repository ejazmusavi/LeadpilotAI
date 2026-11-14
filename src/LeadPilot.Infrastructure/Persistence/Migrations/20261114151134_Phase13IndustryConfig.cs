using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase13IndustryConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "LeadCategories",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "CatalogProducts",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Product");

            migrationBuilder.CreateTable(
                name: "CustomFieldDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    EntityType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: true),
                    IsRequired = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MinValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Pattern = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    HelpText = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    IsFilterable = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PackDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Key = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    Industry = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true),
                    Scope = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    OwnerTenantId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ScopeKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    IsPublished = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsSample = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ContentJson = table.Column<string>(type: "longtext", nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackDefinitions_Tenants_OwnerTenantId",
                        column: x => x.OwnerTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PackItemLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PackKey = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    ItemType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ItemKey = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    EntityId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AppliedHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    PackVersion = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackItemLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackItemLinks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TenantPackApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PackKey = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    PackVersion = table.Column<int>(type: "int", nullable: false),
                    PackName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    ScopeKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ConflictPolicy = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    SummaryJson = table.Column<string>(type: "text", nullable: false),
                    AppliedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AppliedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPackApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantPackApplications_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    FieldId = table.Column<Guid>(type: "char(36)", nullable: false),
                    EntityType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    EntityId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TextValue = table.Column<string>(type: "varchar(600)", maxLength: 600, nullable: true),
                    NumberValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DateValue = table.Column<DateTime>(type: "date", precision: 6, nullable: true),
                    BoolValue = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    SearchText = table.Column<string>(type: "varchar(600)", maxLength: 600, nullable: false),
                    Display = table.Column<string>(type: "varchar(600)", maxLength: 600, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_CustomFieldDefinitions_FieldId",
                        column: x => x.FieldId,
                        principalTable: "CustomFieldDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_LeadCategories_ParentId",
                table: "LeadCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldDefinitions_TenantId",
                table: "CustomFieldDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomFieldDefinitions_Key",
                table: "CustomFieldDefinitions",
                columns: new[] { "TenantId", "EntityType", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_FieldId",
                table: "CustomFieldValues",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_TenantId",
                table: "CustomFieldValues",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_TenantId_EntityType_EntityId",
                table: "CustomFieldValues",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomFieldValues_Field_Entity",
                table: "CustomFieldValues",
                columns: new[] { "TenantId", "FieldId", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackDefinitions_OwnerTenantId_Key",
                table: "PackDefinitions",
                columns: new[] { "OwnerTenantId", "Key" });

            migrationBuilder.CreateIndex(
                name: "UX_PackDefinitions_Key_Version_Scope",
                table: "PackDefinitions",
                columns: new[] { "Key", "VersionNumber", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackItemLinks_TenantId",
                table: "PackItemLinks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_PackItemLinks_Item",
                table: "PackItemLinks",
                columns: new[] { "TenantId", "PackKey", "ItemType", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantPackApplications_TenantId",
                table: "TenantPackApplications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPackApplications_TenantId_PackKey_AppliedAtUtc",
                table: "TenantPackApplications",
                columns: new[] { "TenantId", "PackKey", "AppliedAtUtc" });

            // Existing workspaces that already use the former built-in School ERP sample (its sample agent) or the former education discovery starter
            // (its category mappings) keep exactly what they have: it is now the "education" industry pack, so record that it was applied (mode Migrated).
            // No item links are written: a later import matches their configuration by name, so nothing is duplicated and their edits are never overwritten.
            migrationBuilder.Sql(@"
INSERT INTO `TenantPackApplications` (`Id`, `TenantId`, `PackKey`, `PackVersion`, `PackName`, `ScopeKey`, `ContentHash`, `Mode`, `ConflictPolicy`, `SummaryJson`, `AppliedByUserId`, `AppliedBy`, `AppliedAtUtc`)
SELECT UUID(), t.`TenantId`, 'education', 1, 'Education (schools/colleges)', 'builtin', 'migrated', 'Migrated', 'KeepMine', '{""migrated"":true}', NULL, 'migration', UTC_TIMESTAMP(6)
FROM (
    SELECT DISTINCT `TenantId` FROM `SalesAgents` WHERE `Name` = 'School ERP sales assistant (SAMPLE)'
    UNION
    SELECT DISTINCT m.`TenantId` FROM `DiscoveryCategoryMappings` m JOIN `LeadCategories` c ON c.`Id` = m.`CategoryId` AND c.`TenantId` = m.`TenantId`
    WHERE c.`Name` IN ('Private schools','Primary schools','Secondary schools','Higher secondary schools','Colleges','Academies','Educational institutions')
) t;");

            migrationBuilder.AddForeignKey(
                name: "FK_LeadCategories_LeadCategories_ParentId",
                table: "LeadCategories",
                column: "ParentId",
                principalTable: "LeadCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LeadCategories_LeadCategories_ParentId",
                table: "LeadCategories");

            migrationBuilder.DropTable(
                name: "CustomFieldValues");

            migrationBuilder.DropTable(
                name: "PackDefinitions");

            migrationBuilder.DropTable(
                name: "PackItemLinks");

            migrationBuilder.DropTable(
                name: "TenantPackApplications");

            migrationBuilder.DropTable(
                name: "CustomFieldDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_LeadCategories_ParentId",
                table: "LeadCategories");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "LeadCategories");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "CatalogProducts");
        }
    }
}
