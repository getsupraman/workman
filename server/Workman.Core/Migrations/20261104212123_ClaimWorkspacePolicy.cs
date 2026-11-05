using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// doc 15 §7.2. <c>claim</c>'s tenant policy read its item (<see cref="TenantRls.ThroughParent"/>), which the planner
    /// built as a hashed subplan over every item of the workspace before the claim filter. The claim's own
    /// <c>key_workspace_id</c>, which every claim has carried since ClaimKeyWorkspace, becomes its tenant column. The item
    /// lookup also kept a claim to the members of its project (doc 02 §15b), so claim gets a <c>project_access</c>
    /// policy of its own, the one <c>verification</c> has.
    /// </summary>
    public partial class ClaimWorkspacePolicy : Migration
    {
        /// <summary>The tenant column; a write must also name an item of the tenant, which writes are rare enough to afford.</summary>
        public static readonly IReadOnlyDictionary<string, string> Direct = new Dictionary<string, string>
        {
            ["claim"] = "key_workspace_id",
        };

        public static readonly string Check = $"key_workspace_id = {TenantRls.Tenant} AND {TenantRls.ThroughParent["claim"]}";

        /// <summary>verification's <c>project_access</c>, on the claim's item.</summary>
        public static readonly IReadOnlyDictionary<string, string> ByProject = new Dictionary<string, string>
        {
            ["claim"] = WorkmanNames.ProjectTables["verification"].Replace("verification.item_id", "claim.item_id", StringComparison.Ordinal),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A claim released before ClaimKeyWorkspace kept a null workspace; its item names it.
            migrationBuilder.Sql(
                """
                UPDATE claim c
                   SET key_workspace_id = i.workspace_id
                  FROM item i
                 WHERE c.item_id = i.id
                   AND c.key_workspace_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "key_workspace_id",
                table: "claim",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.Sql(
                $"""
                 DROP POLICY {TenantRls.Policy} ON claim;
                 CREATE POLICY {TenantRls.Policy} ON claim USING (key_workspace_id = {TenantRls.Tenant}) WITH CHECK ({Check});
                 CREATE POLICY {ProjectAccess.Policy} ON claim AS RESTRICTIVE USING ({ByProject["claim"]}) WITH CHECK ({ByProject["claim"]});
                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                 DROP POLICY {ProjectAccess.Policy} ON claim;
                 DROP POLICY {TenantRls.Policy} ON claim;
                 CREATE POLICY {TenantRls.Policy} ON claim USING ({TenantRls.ThroughParent["claim"]}) WITH CHECK ({TenantRls.ThroughParent["claim"]});
                 """);

            migrationBuilder.AlterColumn<Guid>(
                name: "key_workspace_id",
                table: "claim",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
