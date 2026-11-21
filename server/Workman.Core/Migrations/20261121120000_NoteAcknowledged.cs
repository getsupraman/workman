using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// mcp-fit/5: a note can be acknowledged without being consumed. <c>acknowledged_at</c> takes it out of the attention
    /// line, the inbox and the board flag; <c>consumed_at</c> stays NULL, so the next worker brief of its target still renders
    /// it and consumes it there. Written by <c>work_note(acknowledge:…)</c>, an orchestrator's, controller's or human's call.
    /// Raw SQL and outside the EF model, as <see cref="HumanConfirmation"/>: written and read by SQL only.
    /// </summary>
    public partial class NoteAcknowledged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE note
                    ADD COLUMN acknowledged_at timestamptz NULL,
                    ADD COLUMN acknowledged_by text NULL,
                    ADD CONSTRAINT note_acknowledged_whole
                        CHECK ((acknowledged_at IS NULL) = (acknowledged_by IS NULL));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE note
                    DROP CONSTRAINT note_acknowledged_whole,
                    DROP COLUMN acknowledged_by,
                    DROP COLUMN acknowledged_at;
                """);
        }
    }
}
