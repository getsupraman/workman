using Microsoft.EntityFrameworkCore.Migrations;
using Workman.Core.Data;

#nullable disable

namespace Workman.Core.Migrations
{
    /// <summary>
    /// Server 2.0: the database's own names say workman. The request path's role moves to
    /// <see cref="WorkmanConnections.AppRole"/>, the project-access functions are renamed (a policy and a trigger hold a
    /// function by its oid, so neither is recreated), the revision trigger reads <see cref="ActorSetting"/>, and the
    /// journal notifies on <see cref="Channel"/>. The earlier migrations still create the 1.x names, so a fresh database
    /// and a 1.2.0 one arrive here alike.
    /// </summary>
    public partial class WorkmanNames : Migration
    {
        /// <summary>The 1.x role, which TenantRls and ProjectMembers still create and grant.</summary>
        public const string OldAppRole = "workstate_app";

        public const string HiddenFunction = "workman_hidden_projects";

        public const string AccessFunction = "workman_project_access";

        public const string BumpFunction = "workman_bump_access_seq";

        public const string ActorSetting = "workman.actor";

        public const string Channel = "workman";

        private const string OldActorSetting = "workstate.actor";

        private const string OldChannel = "workstate";

        /// <summary>ProjectAccessSeq's policies as Postgres now prints them: the hidden-projects function renamed.</summary>
        public static readonly IReadOnlyDictionary<string, string> ProjectTables = ProjectAccessSeq.Tables.ToDictionary(
            t => t.Key, t => t.Value.Replace(ProjectAccess.HiddenFunction, HiddenFunction, StringComparison.Ordinal));

        /// <summary>The 1.x <c>/api</c> scope permissions and their 2.0 names, as OpenIddict stores them.</summary>
        private static readonly (string Old, string New)[] ScopePermissions =
        [
            ("scp:workstate.read", "scp:workman.read"),
            ("scp:workstate.sessions", "scp:workman.sessions"),
        ];

        private static readonly (string Old, string New, string Arguments)[] Functions =
        [
            (ProjectAccess.HiddenFunction, HiddenFunction, "()"),
            (ProjectAccessSeq.AccessFunction, AccessFunction, "(uuid, uuid)"),
            (ProjectAccessSeq.BumpFunction, BumpFunction, "()"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            MoveRole(migrationBuilder, OldAppRole, WorkmanConnections.AppRole);

            foreach (var (old, @new, arguments) in Functions)
            {
                migrationBuilder.Sql($"ALTER FUNCTION {old}{arguments} RENAME TO {@new};");
            }

            migrationBuilder.Sql(DocBlockRevise(ActorSetting));
            migrationBuilder.Sql(EventNotify(Channel));

            // A client registered under 1.x holds the old scope permissions only; it may now ask for the new names.
            // Down leaves them: a 1.x server never asks a client for them.
            foreach (var (old, @new) in ScopePermissions)
            {
                migrationBuilder.Sql(
                    $"""
                     UPDATE openiddict_application
                     SET    permissions = (permissions::jsonb || '["{@new}"]'::jsonb)::text
                     WHERE  permissions IS NOT NULL
                       AND  permissions::jsonb ? '{old}'
                       AND  NOT permissions::jsonb ? '{@new}';
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(EventNotify(OldChannel));
            migrationBuilder.Sql(DocBlockRevise(OldActorSetting));

            foreach (var (old, @new, arguments) in Functions)
            {
                migrationBuilder.Sql($"ALTER FUNCTION {@new}{arguments} RENAME TO {old};");
            }

            MoveRole(migrationBuilder, WorkmanConnections.AppRole, OldAppRole);
        }

        /// <summary>
        /// A role is the cluster's, not the database's, so it is not renamed: another database of the cluster may still
        /// be a 1.x one that sets the old role. This database's grants move to the new role, as TenantRls made them, and
        /// the old role goes only when no database grants it anything any more.
        /// </summary>
        private static void MoveRole(MigrationBuilder migrationBuilder, string from, string to)
            => migrationBuilder.Sql(
                $"""
                 DO $$
                 BEGIN
                   CREATE ROLE {to} NOLOGIN;
                 EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL;
                 END $$;

                 DO $$
                 BEGIN
                   IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
                     EXECUTE format('GRANT {to} TO %I', current_user);
                   END IF;
                 END $$;

                 GRANT USAGE ON SCHEMA public TO {to};
                 GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {to};
                 GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {to};
                 REVOKE ALL ON {string.Join(", ", TenantRls.OwnerOnly)} FROM {to};
                 ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {to};
                 ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {to};

                 DO $$
                 BEGIN
                   IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{from}') THEN
                     ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM {from};
                     ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM {from};
                     REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {from};
                     REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {from};
                     REVOKE USAGE ON SCHEMA public FROM {from};
                     BEGIN
                       DROP ROLE {from};
                     EXCEPTION WHEN dependent_objects_still_exist OR object_in_use OR insufficient_privilege THEN NULL;
                     END;
                   END IF;
                 END $$;
                 """);

        private static string DocBlockRevise(string actorSetting)
            => $"""
                CREATE OR REPLACE FUNCTION doc_block_revise() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.heading IS NOT DISTINCT FROM OLD.heading
                     AND NEW.body IS NOT DISTINCT FROM OLD.body THEN
                    RETURN NEW;
                  END IF;

                  INSERT INTO doc_block_revision (block_id, revision, heading, body, at, actor)
                  VALUES (OLD.id, OLD.revision, OLD.heading, OLD.body, now(),
                          current_setting('{actorSetting}', true));

                  NEW.revision   := OLD.revision + 1;
                  NEW.updated_at := now();
                  RETURN NEW;
                END
                $fn$;
                """;

        private static string EventNotify(string channel)
            => $"""
                CREATE OR REPLACE FUNCTION event_notify() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  PERFORM pg_notify('{channel}', json_build_object(
                    'ws',   NEW.workspace_id,
                    'seq',  NEW.seq,
                    'item', NEW.item_id,
                    'kind', NEW.kind)::text);
                  RETURN NULL;
                END
                $fn$;
                """;
    }
}
