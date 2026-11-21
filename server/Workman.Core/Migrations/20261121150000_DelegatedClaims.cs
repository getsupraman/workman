using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// mcp-fit/6: a claim an orchestrator takes for another session. <c>session_id</c> stays the holder; <c>claimed_by</c>
    /// and <c>claimed_by_session_id</c> name who claimed it for that holder, and <c>lease_minutes</c> is the lease it renews
    /// to (NULL: the workspace's <c>claim_ttl_minutes</c>). A claim with <c>claimed_by</c> set is delegated: releasing its
    /// holder's name releases it instead of refusing. Raw SQL and outside the EF model, as <see cref="NoteAcknowledged"/>.
    /// </summary>
    public partial class DelegatedClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE claim
                    ADD COLUMN lease_minutes int NULL,
                    ADD COLUMN claimed_by text NULL,
                    ADD COLUMN claimed_by_session_id uuid NULL
                        CONSTRAINT fk_claim_agent_session_claimed_by_session_id
                        REFERENCES agent_session (id) ON DELETE SET NULL,
                    ADD CONSTRAINT claim_lease_minutes_range
                        CHECK (lease_minutes IS NULL OR lease_minutes BETWEEN 1 AND 480);

                CREATE INDEX ix_claim_claimed_by_session_id ON claim (claimed_by_session_id)
                    WHERE released_at IS NULL AND claimed_by_session_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX ix_claim_claimed_by_session_id;

                ALTER TABLE claim
                    DROP CONSTRAINT claim_lease_minutes_range,
                    DROP COLUMN claimed_by_session_id,
                    DROP COLUMN claimed_by,
                    DROP COLUMN lease_minutes;
                """);
        }
    }
}
