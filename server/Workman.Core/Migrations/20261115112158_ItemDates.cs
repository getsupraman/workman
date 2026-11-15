using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// deadlines/2: <c>due</c> and <c>not_before</c> on every item, each a calendar day (<c>*_on</c>) or an instant
    /// (<c>*_at</c>), never both. A day is not frozen to an instant at write: it means that whole day in the workspace's
    /// time zone (<c>workspace.settings.timezone</c>, IANA, default UTC), so the functions below read the zone at query
    /// time and changing it re-reads every date. They are the one evaluation of a date (doc 02 §3 "Dates"):
    /// <c>item_due_end(i) &lt;= now</c> is overdue, <c>item_not_before_start(i) &gt; now</c> is not yet actionable.
    /// <para>
    /// Raw SQL and not in the EF model, like the triggers: the columns are written and read by SQL only, and a mapped
    /// <c>item</c> property would make every EF insert name them — including inserts into a database migrated only part
    /// of the way, which the migration tests seed through the current model.
    /// </para>
    /// </summary>
    public partial class ItemDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE item
                    ADD COLUMN due_on        date NULL,
                    ADD COLUMN due_at        timestamptz NULL,
                    ADD COLUMN not_before_on date NULL,
                    ADD COLUMN not_before_at timestamptz NULL,
                    ADD CONSTRAINT item_due_one_form CHECK (due_on IS NULL OR due_at IS NULL),
                    ADD CONSTRAINT item_not_before_one_form CHECK (not_before_on IS NULL OR not_before_at IS NULL);

                CREATE INDEX item_due_idx ON item (workspace_id, due_on, due_at)
                    WHERE status IN ('open','ready','in_progress','blocked','review')
                      AND (due_on IS NOT NULL OR due_at IS NOT NULL);

                CREATE FUNCTION timezone_of(ws uuid) RETURNS text
                LANGUAGE sql STABLE AS $fn$
                    SELECT coalesce((SELECT nullif(w.settings->>'timezone', '') FROM workspace w WHERE w.id = ws), 'UTC')
                $fn$;

                CREATE FUNCTION date_value_end(d date, at timestamptz, zone text) RETURNS timestamptz
                LANGUAGE sql STABLE AS $fn$
                    SELECT CASE WHEN at IS NOT NULL THEN at
                                WHEN d IS NOT NULL THEN (d + 1)::timestamp AT TIME ZONE zone END
                $fn$;

                CREATE FUNCTION date_value_start(d date, at timestamptz, zone text) RETURNS timestamptz
                LANGUAGE sql STABLE AS $fn$
                    SELECT CASE WHEN at IS NOT NULL THEN at
                                WHEN d IS NOT NULL THEN d::timestamp AT TIME ZONE zone END
                $fn$;

                CREATE FUNCTION date_text_end(t text, zone text) RETURNS timestamptz
                LANGUAGE sql STABLE AS $fn$
                    SELECT CASE WHEN t IS NULL OR t = '' THEN NULL
                                WHEN t ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN date_value_end(t::date, NULL, zone)
                                ELSE t::timestamptz END
                $fn$;

                CREATE FUNCTION item_due_end(i item) RETURNS timestamptz
                LANGUAGE sql STABLE AS $fn$
                    SELECT date_value_end(i.due_on, i.due_at, timezone_of(i.workspace_id))
                $fn$;

                CREATE FUNCTION item_not_before_start(i item) RETURNS timestamptz
                LANGUAGE sql STABLE AS $fn$
                    SELECT date_value_start(i.not_before_on, i.not_before_at, timezone_of(i.workspace_id))
                $fn$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION item_not_before_start(item);
                DROP FUNCTION item_due_end(item);
                DROP FUNCTION date_text_end(text, text);
                DROP FUNCTION date_value_start(date, timestamptz, text);
                DROP FUNCTION date_value_end(date, timestamptz, text);
                DROP FUNCTION timezone_of(uuid);
                DROP INDEX item_due_idx;
                ALTER TABLE item
                    DROP CONSTRAINT item_not_before_one_form,
                    DROP CONSTRAINT item_due_one_form,
                    DROP COLUMN not_before_at,
                    DROP COLUMN not_before_on,
                    DROP COLUMN due_at,
                    DROP COLUMN due_on;
                """);
        }
    }
}
