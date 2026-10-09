using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEntitle.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkloadIdentityGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ActivityEvidenceComplete",
                table: "CloudPrincipals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityEvidenceSource",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActivityObservationDays",
                table: "CloudPrincipals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialEvidenceSource",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialExpiresAtUtc",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CredentialInventoryComplete",
                table: "CloudPrincipals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialLastRotatedAtUtc",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FederationAudience",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FederationInventoryComplete",
                table: "CloudPrincipals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FederationIssuer",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FederationSubject",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FederationTrustVerified",
                table: "CloudPrincipals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FederationTrusts",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastObservedWorkloadAtUtc",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerEvidenceSource",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerIds",
                table: "CloudPrincipals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "OwnerInventoryComplete",
                table: "CloudPrincipals",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityEvidenceComplete",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "ActivityEvidenceSource",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "ActivityObservationDays",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "CredentialEvidenceSource",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "CredentialExpiresAtUtc",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "CredentialInventoryComplete",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "CredentialLastRotatedAtUtc",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "FederationAudience",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "FederationInventoryComplete",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "FederationIssuer",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "FederationSubject",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "FederationTrustVerified",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "FederationTrusts",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "LastObservedWorkloadAtUtc",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "OwnerEvidenceSource",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "OwnerIds",
                table: "CloudPrincipals");

            migrationBuilder.DropColumn(
                name: "OwnerInventoryComplete",
                table: "CloudPrincipals");
        }
    }
}
