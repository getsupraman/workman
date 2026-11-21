using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// mcp-fit/4: a confirmation by a person, and the explicit marker that an item's check is manual.
    /// <para>
    /// <c>item.verify_manual</c> is set only by work_create / work_update, never derived from <c>verify_cmd</c> (C345). A
    /// confirmation is a <c>verification</c> row with <c>run_by = human</c>, <c>passed</c>, no command, and the person's name and
    /// words in <c>confirmed_by</c> / <c>quote</c>; the checks make a half-written or mixed one unwritable. A question answer
    /// stays a human verification with neither column.
    /// </para>
    /// Raw SQL and outside the EF model, as <see cref="ItemDates"/>: written and read by SQL only.
    /// </summary>
    public partial class HumanConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE item ADD COLUMN verify_manual boolean NOT NULL DEFAULT false;

                ALTER TABLE verification
                    ADD COLUMN confirmed_by text NULL,
                    ADD COLUMN quote        text NULL,
                    ADD CONSTRAINT verification_confirmation_whole
                        CHECK ((confirmed_by IS NULL) = (quote IS NULL)),
                    ADD CONSTRAINT verification_confirmation_human
                        CHECK (confirmed_by IS NULL OR (run_by = 'human' AND passed AND command IS NULL));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE verification
                    DROP CONSTRAINT verification_confirmation_human,
                    DROP CONSTRAINT verification_confirmation_whole,
                    DROP COLUMN quote,
                    DROP COLUMN confirmed_by;

                ALTER TABLE item DROP COLUMN verify_manual;
                """);
        }
    }
}
