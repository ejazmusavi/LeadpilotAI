using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenancyAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Code = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Group = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    IsPlatform = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "varchar(63)", maxLength: 63, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    IsPlatform = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Industry = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Timezone = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    SuspendedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    SuspensionReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    DisplayName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ActorTenantId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ActorEmail = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    EntityId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    Summary = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    IpAddress = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    CreatedByIp = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    IsSystem = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Roles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false),
                    IsSecret = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserTenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    IsDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserTenants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserTenants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RoleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PermissionId = table.Column<Guid>(type: "char(36)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TenantInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Email = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    RoleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    AcceptedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    UpdatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TenantId = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RoleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "char(36)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId",
                table: "AuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Action",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_CreatedAtUtc",
                table: "AuditLogs",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Code",
                table: "Permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TenantId",
                table: "RefreshTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionId",
                table: "RolePermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_PermissionId",
                table: "RolePermissions",
                columns: new[] { "RoleId", "PermissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_TenantId",
                table: "RolePermissions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId",
                table: "Roles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_Name",
                table: "Roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_RoleId",
                table: "TenantInvitations",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId",
                table: "TenantInvitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId_Email_Status",
                table: "TenantInvitations",
                columns: new[] { "TenantId", "Email", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TokenHash",
                table: "TenantInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Status",
                table: "Tenants",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId",
                table: "TenantSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_TenantId",
                table: "UserRoles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_TenantId_UserId_RoleId",
                table: "UserRoles",
                columns: new[] { "TenantId", "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId",
                table: "UserRoles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTenants_TenantId",
                table: "UserTenants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTenants_TenantId_UserId",
                table: "UserTenants",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTenants_UserId",
                table: "UserTenants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "TenantInvitations");

            migrationBuilder.DropTable(
                name: "TenantSettings");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "UserTenants");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
