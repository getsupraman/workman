using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// deadlines/3, doc 02 "Kinds are data": a <c>wait</c> is waiting on something outside the system. It <c>blocks</c> the
    /// real work, its <c>due</c> is the chase date, and it closes on a note of what arrived rather than on criteria, a
    /// verification or a commit — so every requires_* flag is false. InitialTree is the only seed of <c>item_kind</c>.
    /// </summary>
    public partial class WaitKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO item_kind
                    (key, label, rank, allowed_parent_kinds, scopes_refs,
                     requires_verify, requires_criteria, requires_commit, is_gateable)
                VALUES
                    ('wait', 'Wait', 60, '{run,phase,task}', false, false, false, false, false)
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM item_kind WHERE key = 'wait';");
        }
    }
}
