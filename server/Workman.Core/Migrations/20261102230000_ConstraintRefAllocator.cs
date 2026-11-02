using Microsoft.EntityFrameworkCore.Migrations;
using Workman.Core.Data;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// doc 02 §15b. A <c>C…</c> is unique across the workspace (<c>constraint_ref_unique</c>), but the request path reads
    /// <c>constraint_rule</c> under <see cref="ProjectAccess.Policy"/>, which hides the rules of every project the caller
    /// is not a member of; its probe answered "free" for a number a hidden rule held, and the index refused the insert.
    /// The first free number is read here instead, as the owner, so it counts every rule of the workspace and returns
    /// nothing of them but the number.
    /// </summary>
    public partial class ConstraintRefAllocator : Migration
    {
        public const string Function = "workman_free_constraint_ref";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Among C1…C(count + 1) one is always free. The workspace is the tenant's own, or any when no tenant is set
            // (the owner's connection); another tenant's gets null, so a caller cannot count a workspace not theirs.
            migrationBuilder.Sql(
                $"""
                 CREATE FUNCTION {Function}(p_workspace uuid) RETURNS text
                 LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
                   SELECT 'C' || min(n)
                   FROM   generate_series(1, (SELECT count(*) + 1 FROM constraint_rule r WHERE r.workspace_id = p_workspace)::int) AS n
                   WHERE  coalesce(NULLIF(current_setting('{WorkmanConnections.WorkspaceSetting}', true), '')::uuid = p_workspace, true)
                     AND  NOT EXISTS (SELECT 1 FROM constraint_rule r
                                      WHERE  r.workspace_id = p_workspace AND lower(r.ref) = lower('C' || n));
                 $fn$;

                 REVOKE ALL ON FUNCTION {Function}(uuid) FROM PUBLIC;
                 GRANT EXECUTE ON FUNCTION {Function}(uuid) TO {WorkmanConnections.AppRole};
                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {Function}(uuid);");
        }
    }
}
