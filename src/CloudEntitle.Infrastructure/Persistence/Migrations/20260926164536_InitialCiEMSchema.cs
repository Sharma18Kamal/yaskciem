using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCiEMSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessPaths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PathType = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPaths", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: true),
                    Metadata = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CloudProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    SubscriptionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ConnectionStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastScanAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccountType = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentAccountId = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderPath = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MemberPrincipalIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Resource = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    Service = table.Column<string>(type: "TEXT", nullable: true),
                    ActionName = table.Column<string>(type: "TEXT", nullable: true),
                    ResourcePattern = table.Column<string>(type: "TEXT", nullable: true),
                    AccessType = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudPermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyType = table.Column<string>(type: "TEXT", nullable: true),
                    Document = table.Column<string>(type: "TEXT", nullable: true),
                    IsManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    DocumentVersion = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudPrincipals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    IdentityType = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPrivileged = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastActivity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsExternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudPrincipals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true),
                    OrganizationId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudProjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    CloudRegion = table.Column<string>(type: "TEXT", nullable: true),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentResourceId = table.Column<string>(type: "TEXT", nullable: true),
                    ResourceUri = table.Column<string>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudResources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    IsPrivileged = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudServiceAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrivileged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudServiceAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NormalizedRiskFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EffectiveAccessId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NormalizedRiskFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NormalizedRiskRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NormalizedRiskRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Remediation = table.Column<string>(type: "TEXT", nullable: false),
                    RiskFindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrentState = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedState = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recommendations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Principal = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    Resource = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Permission = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EffectiveAccessId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudProvider = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RuleExpression = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessPathNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccessPathId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPathNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessPathNodes_AccessPaths_AccessPathId",
                        column: x => x.AccessPathId,
                        principalTable: "AccessPaths",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsDiscovered = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentitiesDiscovered = table.Column<int>(type: "INTEGER", nullable: false),
                    PermissionsDiscovered = table.Column<int>(type: "INTEGER", nullable: false),
                    FindingsGenerated = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Scans_CloudAccounts_CloudAccountId",
                        column: x => x.CloudAccountId,
                        principalTable: "CloudAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityRecords_CloudPrincipals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "CloudPrincipals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MembershipType = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMemberships_CloudGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "CloudGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMemberships_CloudPrincipals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "CloudPrincipals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    AssignmentType = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyAssignments_CloudPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "CloudPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PolicyAssignments_CloudPrincipals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "CloudPrincipals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EffectiveAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    AccessType = table.Column<int>(type: "INTEGER", nullable: false),
                    AssignmentType = table.Column<int>(type: "INTEGER", nullable: false),
                    AccessPathId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDirect = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsInherited = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrivileged = table.Column<bool>(type: "INTEGER", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EffectiveAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EffectiveAccesses_CloudPermissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "CloudPermissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EffectiveAccesses_CloudPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "CloudPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EffectiveAccesses_CloudPrincipals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "CloudPrincipals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EffectiveAccesses_CloudResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "CloudResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EffectiveAccesses_CloudRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "CloudRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Entitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    AssignmentType = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entitlements_CloudAccounts_CloudAccountId",
                        column: x => x.CloudAccountId,
                        principalTable: "CloudAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Entitlements_CloudPermissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "CloudPermissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Entitlements_CloudPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "CloudPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Entitlements_CloudPrincipals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "CloudPrincipals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Entitlements_CloudRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "CloudRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NormalizedRecommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RiskFindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentState = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedState = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NormalizedRecommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NormalizedRecommendations_NormalizedRiskFindings_RiskFindingId",
                        column: x => x.RiskFindingId,
                        principalTable: "NormalizedRiskFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPathNodes_AccessPathId_Sequence",
                table: "AccessPathNodes",
                columns: new[] { "AccessPathId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRecords_Action",
                table: "ActivityRecords",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRecords_PrincipalId_CloudAccountId_Timestamp",
                table: "ActivityRecords",
                columns: new[] { "PrincipalId", "CloudAccountId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventType",
                table: "AuditEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrganizationId_Timestamp",
                table: "AuditEvents",
                columns: new[] { "OrganizationId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudAccounts_AccountType",
                table: "CloudAccounts",
                column: "AccountType");

            migrationBuilder.CreateIndex(
                name: "IX_CloudAccounts_CloudProvider_ExternalAccountId",
                table: "CloudAccounts",
                columns: new[] { "CloudProvider", "ExternalAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudAccounts_CloudProvider_SubscriptionId",
                table: "CloudAccounts",
                columns: new[] { "CloudProvider", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudAccounts_ParentAccountId",
                table: "CloudAccounts",
                column: "ParentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CloudAccounts_ProjectId",
                table: "CloudAccounts",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_CloudGroups_AccountId_GroupId",
                table: "CloudGroups",
                columns: new[] { "AccountId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPermissions_AccountId_Action",
                table: "CloudPermissions",
                columns: new[] { "AccountId", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPermissions_AccountId_Resource",
                table: "CloudPermissions",
                columns: new[] { "AccountId", "Resource" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPolicies_AccountId_ExternalId",
                table: "CloudPolicies",
                columns: new[] { "AccountId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPolicies_AccountId_PolicyType",
                table: "CloudPolicies",
                columns: new[] { "AccountId", "PolicyType" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPrincipals_AccountId_IdentityType_IsPrivileged",
                table: "CloudPrincipals",
                columns: new[] { "AccountId", "IdentityType", "IsPrivileged" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPrincipals_AccountId_PrincipalId",
                table: "CloudPrincipals",
                columns: new[] { "AccountId", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudPrincipals_Email_IsExternal",
                table: "CloudPrincipals",
                columns: new[] { "Email", "IsExternal" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudResources_AccountId_ResourceId",
                table: "CloudResources",
                columns: new[] { "AccountId", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudResources_AccountId_Type",
                table: "CloudResources",
                columns: new[] { "AccountId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudRoles_AccountId_IsPrivileged",
                table: "CloudRoles",
                columns: new[] { "AccountId", "IsPrivileged" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudRoles_AccountId_RoleId",
                table: "CloudRoles",
                columns: new[] { "AccountId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_PermissionId_IsPrivileged",
                table: "EffectiveAccesses",
                columns: new[] { "PermissionId", "IsPrivileged" });

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_PolicyId",
                table: "EffectiveAccesses",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_PrincipalId_CloudAccountId_ResourceId",
                table: "EffectiveAccesses",
                columns: new[] { "PrincipalId", "CloudAccountId", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_ResourceId",
                table: "EffectiveAccesses",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_EffectiveAccesses_RoleId",
                table: "EffectiveAccesses",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_CloudAccountId",
                table: "Entitlements",
                column: "CloudAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_PermissionId_ResourceId_Scope",
                table: "Entitlements",
                columns: new[] { "PermissionId", "ResourceId", "Scope" });

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_PolicyId",
                table: "Entitlements",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_PrincipalId_CloudAccountId",
                table: "Entitlements",
                columns: new[] { "PrincipalId", "CloudAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Entitlements_RoleId",
                table: "Entitlements",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMemberships_GroupId_PrincipalId",
                table: "GroupMemberships",
                columns: new[] { "GroupId", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMemberships_PrincipalId",
                table: "GroupMemberships",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRecommendations_RiskFindingId_Priority",
                table: "NormalizedRecommendations",
                columns: new[] { "RiskFindingId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRiskFindings_RuleId_CloudAccountId_Severity_Status",
                table: "NormalizedRiskFindings",
                columns: new[] { "RuleId", "CloudAccountId", "Severity", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_NormalizedRiskRules_Code",
                table: "NormalizedRiskRules",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAssignments_PolicyId",
                table: "PolicyAssignments",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAssignments_PrincipalId_PolicyId_CloudAccountId",
                table: "PolicyAssignments",
                columns: new[] { "PrincipalId", "PolicyId", "CloudAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Scans_CloudAccountId_StartedAt_Status",
                table: "Scans",
                columns: new[] { "CloudAccountId", "StartedAt", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessPathNodes");

            migrationBuilder.DropTable(
                name: "ActivityRecords");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "CloudProjects");

            migrationBuilder.DropTable(
                name: "CloudServiceAccounts");

            migrationBuilder.DropTable(
                name: "CloudSubscriptions");

            migrationBuilder.DropTable(
                name: "EffectiveAccesses");

            migrationBuilder.DropTable(
                name: "Entitlements");

            migrationBuilder.DropTable(
                name: "GroupMemberships");

            migrationBuilder.DropTable(
                name: "NormalizedRecommendations");

            migrationBuilder.DropTable(
                name: "NormalizedRiskRules");

            migrationBuilder.DropTable(
                name: "PermissionAssignments");

            migrationBuilder.DropTable(
                name: "PolicyAssignments");

            migrationBuilder.DropTable(
                name: "Recommendations");

            migrationBuilder.DropTable(
                name: "RiskFindings");

            migrationBuilder.DropTable(
                name: "RiskRules");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "Scans");

            migrationBuilder.DropTable(
                name: "AccessPaths");

            migrationBuilder.DropTable(
                name: "CloudResources");

            migrationBuilder.DropTable(
                name: "CloudPermissions");

            migrationBuilder.DropTable(
                name: "CloudRoles");

            migrationBuilder.DropTable(
                name: "CloudGroups");

            migrationBuilder.DropTable(
                name: "NormalizedRiskFindings");

            migrationBuilder.DropTable(
                name: "CloudPolicies");

            migrationBuilder.DropTable(
                name: "CloudPrincipals");

            migrationBuilder.DropTable(
                name: "CloudAccounts");
        }
    }
}
