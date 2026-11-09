using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionUsageAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PermissionAction",
                table: "ActivityRecords",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermissionMappingEvidence",
                table: "ActivityRecords",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PermissionMappingStatus",
                table: "ActivityRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CloudActivityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantOrAccountId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PrincipalType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PermissionAction = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PermissionMappingStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    PermissionMappingEvidence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ResourceReference = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OutcomeKnown = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RawEvidence = table.Column<string>(type: "TEXT", nullable: true),
                    EventTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CollectionTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudActivityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionObservationWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedFromUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ObservedThroughUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IncludesActions = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludesResourceIds = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludesRoleIds = table.Column<bool>(type: "INTEGER", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: false),
                    CollectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionObservationWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EffectiveAccessId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EffectiveAccessIds = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    State30Days = table.Column<int>(type: "INTEGER", nullable: false),
                    State60Days = table.Column<int>(type: "INTEGER", nullable: false),
                    State90Days = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SuccessfulActions30Days = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessfulActions60Days = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessfulActions90Days = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrelationStatus = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CalculatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionUsageRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionUsageSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowDays = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BucketType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BucketStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    BucketEndUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TotalEffectivePermissions = table.Column<int>(type: "INTEGER", nullable: false),
                    Used = table.Column<int>(type: "INTEGER", nullable: false),
                    NotObserved = table.Column<int>(type: "INTEGER", nullable: false),
                    InsufficientObservation = table.Column<int>(type: "INTEGER", nullable: false),
                    UnknownMapping = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionIncomplete = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedEventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidateActionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CalculatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionUsageSummaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageAnalyticsFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RiskFindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RiskScore = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservationWindowDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageAnalyticsFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageRecommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    RecommendationType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRecommendations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRecords_CloudAccountId_PrincipalId_PermissionAction_Timestamp",
                table: "ActivityRecords",
                columns: new[] { "CloudAccountId", "PrincipalId", "PermissionAction", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudActivityEvents_Action",
                table: "CloudActivityEvents",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_CloudActivityEvents_CloudAccountId_Provider_EventTimestamp",
                table: "CloudActivityEvents",
                columns: new[] { "CloudAccountId", "Provider", "EventTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudActivityEvents_DeduplicationKey",
                table: "CloudActivityEvents",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CloudActivityEvents_IdentityId_EventTimestamp",
                table: "CloudActivityEvents",
                columns: new[] { "IdentityId", "EventTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_CloudActivityEvents_ResourceReference",
                table: "CloudActivityEvents",
                column: "ResourceReference");

            migrationBuilder.CreateIndex(
                name: "IX_CloudActivityEvents_SourceEventId",
                table: "CloudActivityEvents",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionObservationWindows_CloudAccountId_Provider_ObservedFromUtc_ObservedThroughUtc",
                table: "PermissionObservationWindows",
                columns: new[] { "CloudAccountId", "Provider", "ObservedFromUtc", "ObservedThroughUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionObservationWindows_SnapshotId",
                table: "PermissionObservationWindows",
                column: "SnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionUsageRecords_CloudAccountId_Provider_IdentityId_PermissionId_ResourceId_CalculatedAtUtc",
                table: "PermissionUsageRecords",
                columns: new[] { "CloudAccountId", "Provider", "IdentityId", "PermissionId", "ResourceId", "CalculatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionUsageRecords_PermissionId_State90Days",
                table: "PermissionUsageRecords",
                columns: new[] { "PermissionId", "State90Days" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionUsageSummaries_CloudAccountId_Provider_WindowDays_BucketType_BucketStartUtc_IdentityId",
                table: "PermissionUsageSummaries",
                columns: new[] { "CloudAccountId", "Provider", "WindowDays", "BucketType", "BucketStartUtc", "IdentityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageAnalyticsFindings_CloudAccountId_Provider_IdentityId_ObservedAtUtc",
                table: "UsageAnalyticsFindings",
                columns: new[] { "CloudAccountId", "Provider", "IdentityId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageAnalyticsFindings_PermissionId_Severity",
                table: "UsageAnalyticsFindings",
                columns: new[] { "PermissionId", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageAnalyticsFindings_RiskFindingId",
                table: "UsageAnalyticsFindings",
                column: "RiskFindingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecommendations_CloudAccountId_Provider_Status_CreatedAtUtc",
                table: "UsageRecommendations",
                columns: new[] { "CloudAccountId", "Provider", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecommendations_RecommendationId",
                table: "UsageRecommendations",
                column: "RecommendationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudActivityEvents");

            migrationBuilder.DropTable(
                name: "PermissionObservationWindows");

            migrationBuilder.DropTable(
                name: "PermissionUsageRecords");

            migrationBuilder.DropTable(
                name: "PermissionUsageSummaries");

            migrationBuilder.DropTable(
                name: "UsageAnalyticsFindings");

            migrationBuilder.DropTable(
                name: "UsageRecommendations");

            migrationBuilder.DropIndex(
                name: "IX_ActivityRecords_CloudAccountId_PrincipalId_PermissionAction_Timestamp",
                table: "ActivityRecords");

            migrationBuilder.DropColumn(
                name: "PermissionAction",
                table: "ActivityRecords");

            migrationBuilder.DropColumn(
                name: "PermissionMappingEvidence",
                table: "ActivityRecords");

            migrationBuilder.DropColumn(
                name: "PermissionMappingStatus",
                table: "ActivityRecords");
        }
    }
}
