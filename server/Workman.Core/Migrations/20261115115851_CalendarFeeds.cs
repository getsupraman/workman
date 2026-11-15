using Microsoft.EntityFrameworkCore.Migrations;
using Workman.Core.Data;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// deadlines/7: <c>calendar_feed</c>, one long-lived secret per user per workspace for the calendar feed
    /// (<c>GET /api/calendar/{token}.ics</c>, doc 10). Only the token's SHA-256 is stored. The key to
    /// <c>tenant_membership</c> cascades, so removing a user from the workspace ends their feed with the membership. A
    /// tenant table under the same policy as every other (doc 02 §15), listed in <see cref="TenantTables"/> through
    /// <see cref="Direct"/>.
    /// <para>
    /// Raw SQL and not in the EF model, like <c>ItemDates</c>: the table is written and read by Dapper only
    /// (<c>CalendarFeedStore</c>), so an entity would add nothing but snapshot churn.
    /// </para>
    /// </summary>
    public partial class CalendarFeeds : Migration
    {
        /// <summary>The table's workspace column, for the policy; <see cref="TenantTables"/> lists it with TenantRls's.</summary>
        public static readonly IReadOnlyDictionary<string, string> Direct = new Dictionary<string, string>
        {
            ["calendar_feed"] = "workspace_id",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                 CREATE TABLE calendar_feed (
                     id           uuid PRIMARY KEY,
                     user_id      uuid NOT NULL,
                     workspace_id uuid NOT NULL,
                     token_hash   text NOT NULL,
                     created_at   timestamptz NOT NULL DEFAULT now(),
                     CONSTRAINT calendar_feed_membership FOREIGN KEY (user_id, workspace_id)
                         REFERENCES tenant_membership (user_id, workspace_id) ON DELETE CASCADE
                 );
                 CREATE UNIQUE INDEX calendar_feed_token_hash_idx ON calendar_feed (token_hash);
                 CREATE UNIQUE INDEX calendar_feed_user_idx ON calendar_feed (user_id, workspace_id);

                 GRANT SELECT, INSERT, UPDATE, DELETE ON calendar_feed TO {WorkmanConnections.AppRole};
                 """);

            foreach (var (table, column) in Direct)
            {
                var predicate = $"{column} = {TenantRls.Tenant}";
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                     ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                     CREATE POLICY {TenantRls.Policy} ON {table} USING ({predicate}) WITH CHECK ({predicate});
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE calendar_feed;");
        }
    }
}
