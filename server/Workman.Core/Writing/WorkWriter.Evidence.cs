using System.Text.Json;
using Dapper;
using Npgsql;
using Workman.Core.Domain;
using Workman.Core.Rendering;

namespace Workman.Core.Writing;

/// <summary>doc 01 §3.4 — the report as its existing schema, so the format stops being prose.</summary>
public sealed record ReportArgs(
    string Status,
    string[]? Files = null,
    string? Verify = null,
    string? RedCheck = null,
    string? Deviations = null,
    string[]? Assumptions = null,
    string? Risks = null,
    string? NotDone = null);

public sealed partial class WorkWriter
{
    /// <summary>
    /// doc 01 §3.4. <c>result_line</c> is the actual line the command printed, never an exit code —
    /// an <c>exit 0</c> at the end of a pipe belongs to the <c>tail</c>, not to the build.
    /// <para>
    /// <c>run_by</c> comes from the token when the caller is a worker, never from the argument. That
    /// is the entire reason a worker may hold this tool: its evidence is recorded as a worker's, so
    /// it can never satisfy its own item's <c>done</c> guard (doc 07 §3 condition 2).
    /// </para>
    /// </summary>
    public async Task<WriteResult> VerifyAsync(
        Guid workspaceId,
        string? reference,
        Actor actor,
        string? command,
        string? resultLine,
        bool passed,
        string? runBy,
        string? counts,
        string? redCheck,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        if (string.IsNullOrWhiteSpace(resultLine))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_verify({scope.Ref}) without a result_line.",
                "result_line is the line the command actually printed — \"Passed! 187/187\" — because "
                + "an exit code at the end of a pipe belongs to the last command in it, not to the suite",
                $"work_verify(ref:\"{scope.Ref}\", command:\"…\", result_line:\"Passed! 187/187\", "
                + "run_by:\"orchestrator\")"));
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);
        var runner = actor.RunnerFor(runBy);

        await RecordVerificationAsync(
            connection, transaction, item, actor, runner, command, resultLine, passed, counts, redCheck,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var lines = new List<string>
        {
            $"OK  {item.Ref}  verification recorded {(passed ? "✓" : "✗")} by {Pg.Label(runner)}",
            $"    result  {Glyph.Row(resultLine, 88)}",
        };

        if (runner == VerifyRunner.Worker)
        {
            lines.Add("    note    recorded as run_by=worker, from your token. A worker's own evidence");
            lines.Add("            never satisfies an item's done guard — that needs an orchestrator run.");
            lines.Add(string.Empty);
            lines.Add($"Next: work_report(ref:\"{item.Ref}\", status:\"done\", …) and hand back; "
                    + "closing is the orchestrator's call.");
        }
        else if (Transitions.IsTerminal(item.Status))
        {
            lines.Add(string.Empty);
            lines.Add($"Next: nothing to move — {item.Ref} is already {Transitions.Name(item.Status)}; "
                    + "this evidence is kept with its record."
                    + (passed
                        ? string.Empty
                        : $" work_create(items:[{{kind:\"followup\", parent:\"{item.Ref}\", title:\"…\", verify_cmd:\"…\", criteria:[…]}}]) "
                          + "if the red result needs work."));
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add(passed
                ? $"Next: work_status(ref:\"{item.Ref}\", to:\"done\")."
                : $"Next: work_status(ref:\"{item.Ref}\", to:\"in_progress\") — this one is red.");
        }

        return WriteResult.Ok(string.Join("\n", lines));
    }

    /// <summary>
    /// mcp-fit/4 — a confirmation by a person: their name and their words, recorded as its own kind of evidence and
    /// never as a passed verification (C343). It is refused before anything is written when it is half given, mixed
    /// with a command's run, recorded by a worker (C344), or aimed at an item whose real, non-manual
    /// <c>verify_cmd</c> is the check: a person's word does not stand in for a command that can be run.
    /// </summary>
    public async Task<WriteResult> ConfirmAsync(
        Guid workspaceId,
        string? reference,
        Actor actor,
        string? confirmedBy,
        string? quote,
        string? command = null,
        string? resultLine = null,
        bool passedGiven = false,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        if (ConfirmationShapeRefusal(
                $"work_verify({scope.Ref}) with an incomplete confirmation. Nothing was written.",
                $"work_verify(ref:\"{scope.Ref}\", confirmed_by:\"<person>\", quote:\"<their words>\")",
                actor,
                confirmedBy,
                quote,
                command,
                resultLine,
                passedGiven) is { } malformed)
        {
            return WriteResult.No(malformed);
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);

        if (ConfirmableRefusal(item, "work_verify") is { } unconfirmable)
        {
            return WriteResult.No(unconfirmable);
        }

        await RecordConfirmationAsync(connection, transaction, item, actor, confirmedBy!, quote!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var lines = new List<string>
        {
            $"OK  {item.Ref}  confirmation recorded",
            $"    {Confirmed.Text(confirmedBy, quote)}",
            "    recorded as a person's confirmation, counted apart from verifications and forced closes",
            string.Empty,
            Transitions.IsTerminal(item.Status)
                ? $"Next: nothing to move — {item.Ref} is already {Transitions.Name(item.Status)}; this is kept with its record."
                : $"Next: work_status(ref:\"{item.Ref}\", to:\"done\").",
        };

        return WriteResult.Ok(string.Join("\n", lines));
    }

    /// <summary>
    /// What a confirmation must carry and who may record one, the same for <c>work_verify</c> and the one-call
    /// close: both the person and the quote, none of a command's run, and an actor who can close — never a worker.
    /// </summary>
    private static string? ConfirmationShapeRefusal(
        string headline,
        string example,
        Actor actor,
        string? confirmedBy,
        string? quote,
        string? command,
        string? resultLine,
        bool passedGiven)
    {
        if (actor.Role is not (ActorRole.Orchestrator or ActorRole.Controller or ActorRole.Human))
        {
            return Refusal.Blocked(
                $"{headline.Split(" with ")[0]} — a {Pg.Label(actor.Role)} cannot record a confirmation. Nothing was written.",
                "only an orchestrator, a controller or a human records a person's confirmation — a worker hands back "
                + "what it heard, and the orchestrator records it (C344)",
                "work_report(ref:\"…\", status:\"done\", verify:\"<who confirmed it, and their words>\", …) and hand back");
        }

        var unmet = new List<Unmet>();

        if (Blank(confirmedBy))
        {
            unmet.Add(new Unmet("confirmed_by is missing — name the person who confirmed it", example));
        }

        if (Blank(quote))
        {
            unmet.Add(new Unmet("quote is missing — their words are the evidence, a name alone is not", example));
        }

        var mixed = new[] { (!Blank(command), "command"), (!Blank(resultLine), "result_line"), (passedGiven, "passed") }
            .Where(f => f.Item1)
            .Select(f => f.Item2)
            .ToList();

        if (mixed.Count > 0)
        {
            unmet.Add(new Unmet(
                $"a confirmation carries no {string.Join(", ", mixed)} — a person's word is not a command's run",
                [example, "work_verify(ref:\"…\", command:\"…\", result_line:\"…\") for a run, on its own"]));
        }

        return unmet.Count == 0 ? null : Refusal.Blocked(headline, unmet);
    }

    /// <summary>
    /// C343 — a confirmation can close an item only when its check is a person's: no <c>verify_cmd</c>, or one marked
    /// manual with <c>verify_manual</c>. On a real command it is refused here, at write time, with both ways on.
    /// </summary>
    private static string? ConfirmableRefusal(ItemRow item, string tool)
    {
        if (item.VerifyManual || Blank(item.VerifyCmd))
        {
            return null;
        }

        return Refusal.Blocked(
            $"{tool}({item.Ref}) with a confirmation, but its verify_cmd is a command. Nothing was written.",
            [
                new Unmet(
                    $"{item.Ref} is judged by \"{Glyph.Flat(item.VerifyCmd)}\", which is not marked manual — a person's "
                    + "word does not stand in for a command that can be run",
                    [
                        $"work_verify(ref:\"{item.Ref}\", command:\"{Glyph.Flat(item.VerifyCmd)}\", result_line:\"…\", "
                        + "run_by:\"orchestrator\") — run the command",
                        $"work_update(ref:\"{item.Ref}\", verify_manual:true) if it really is a manual check, then confirm again",
                    ]),
            ]);
    }

    /// <summary>
    /// The confirmation row: <c>run_by = human</c>, passed, no command, the person and their words — and the quote as
    /// <c>result_line</c>, so a surface that knows nothing of confirmations still prints their words, not a pass.
    /// </summary>
    private async Task RecordConfirmationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string confirmedBy,
        string quote,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO verification
                (id, workspace_id, item_id, command, result_line, passed, run_by, session_id, confirmed_by, quote)
            VALUES (@id, @workspace, @item, NULL, @quote, true, 'human'::verify_runner, @session, @person, @quote);
            """;

        var person = Glyph.Flat(confirmedBy);
        var words = Glyph.Flat(quote);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                workspace = item.WorkspaceId,
                item = item.Id,
                session = actor.SessionId,
                person,
                quote = words,
            },
            transaction,
            cancellationToken: cancellationToken));

        await HeartbeatAsync(
            connection,
            transaction,
            item.Id,
            actor,
            await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken),
            cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "confirmed",
            $"{item.Ref} {Confirmed.Text(person, words)}",
            new { confirmed_by = person, quote = words },
            cancellationToken);
    }

    /// <summary>
    /// doc 01 §3.4. The attempt and the report, plus an <c>assumption</c> row per line of
    /// <c>assumptions[]</c> — the field the skills call the highest-value line in the format, which
    /// is the point of taking the report as a schema rather than as text.
    /// </summary>
    public async Task<WriteResult> ReportAsync(
        Guid workspaceId,
        string? reference,
        Actor actor,
        ReportArgs args,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        if (Blank(args.Status))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_report({scope.Ref}) without a status.",
                "status is the first line of the report format: done, partial or blocked",
                $"work_report(ref:\"{scope.Ref}\", status:\"partial\", files:[…], verify:\"…\", "
                + "not_done:\"…\")"));
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);

        var closesWait = item.Kind == WaitKind && string.Equals(args.Status.Trim(), "done", StringComparison.OrdinalIgnoreCase);
        if (closesWait && await WaitReportRefusalAsync(connection, transaction, item, args.Verify, cancellationToken) is { } refused)
        {
            return WriteResult.No(refused);
        }

        const string attemptSql =
            """
            INSERT INTO attempt (id, item_id, n, agent_name, agent_kind, outcome, ended_at)
            SELECT @id, @item, coalesce(max(n), 0) + 1, @agent, @role, @outcome, now()
            FROM   attempt WHERE item_id = @item
            RETURNING n;
            """;

        var attemptId = Guid.CreateVersion7();
        var n = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            attemptSql,
            new
            {
                id = attemptId,
                item = item.Id,
                agent = actor.Name,
                role = Pg.Label(actor.Role),
                outcome = args.Status.Trim(),
            },
            transaction,
            cancellationToken: cancellationToken));

        const string reportSql =
            """
            INSERT INTO report (id, attempt_id, status, files, deviations, risks, not_done, raw, session_id)
            VALUES (@id, @attempt, @status, @files::jsonb, @deviations, @risks, @notDone, @raw, @session);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            reportSql,
            new
            {
                id = Guid.CreateVersion7(),
                session = actor.SessionId,
                attempt = attemptId,
                status = args.Status.Trim(),
                files = JsonSerializer.Serialize(args.Files ?? []),
                deviations = Blank(args.Deviations) ? null : args.Deviations,
                risks = Blank(args.Risks) ? null : args.Risks,
                notDone = Blank(args.NotDone) ? null : args.NotDone,
                raw = Raw(args),
            },
            transaction,
            cancellationToken: cancellationToken));

        var opened = new List<string>();
        foreach (var statement in (args.Assumptions ?? []).Where(a => !Blank(a)))
        {
            opened.Add(await AssumeAsync(
                connection,
                transaction,
                item,
                actor,
                statement.Trim(),
                EvidenceLevel.Asserted,
                ReversalCost.Medium,
                null,
                cancellationToken));
        }

        var callable = opened.Count == 0
            ? null
            : await CallableAssumptionAsync(connection, transaction, item, opened[0], cancellationToken);

        await HeartbeatAsync(
            connection,
            transaction,
            item.Id,
            actor,
            await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken),
            cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "reported",
            $"{item.Ref} attempt {n} reported {args.Status.Trim()} by {actor.Label}",
            new { attempt = n, status = args.Status.Trim(), assumptions = opened },
            cancellationToken);

        if (closesWait)
        {
            await CloseWaitAsync(connection, transaction, item, actor, args.Verify!, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var lines = new List<string>
        {
            $"OK  {item.Ref}  attempt {n} recorded, status {args.Status.Trim()}",
        };

        if (opened.Count > 0)
        {
            lines.Add($"    opened  {opened.Count} assumption(s) from assumptions[]: "
                    + $"{string.Join(", ", opened)}");
            lines.Add($"            work_assume(ref:\"{callable}\", resolve:\"confirmed\", "
                    + "evidence:\"…\") when one settles");
        }

        if (closesWait)
        {
            lines.Add($"    closed  {Transitions.Name(item.Status)} → done — the wait is over");
            lines.Add($"    arrived {Glyph.Flat(args.Verify)}");
            lines.Add("            recorded as an observation note on the wait");
            lines.Add(string.Empty);
            lines.Add("Next: work_next() — the frontier has just moved.");
            return WriteResult.Ok(string.Join("\n", lines));
        }

        lines.Add(string.Empty);
        lines.Add($"Next: work_verify(ref:\"{item.Ref}\", command:\"…\", result_line:\"…\") — "
                + "a report is a claim, not evidence.");

        return WriteResult.Ok(string.Join("\n", lines));
    }

    /// <summary>
    /// deadlines/3 — every role may close a wait (question 10), and a worker's tool for it is the report it already has:
    /// status done with what arrived in <c>verify</c>. The close is held to what <c>work_status</c> holds a wait to, so the
    /// worker route is not a way around an open child or a blocker finding.
    /// </summary>
    private async Task<string?> WaitReportRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        string? arrived,
        CancellationToken cancellationToken)
    {
        var started = item.StartedAt is not null;
        if (!Transitions.Legal(item.Status, Move.Done, item.Kind, started))
        {
            return Transitions.Refuse(item.Ref, item.Status, Move.Done, item.Kind, started);
        }

        if (Blank(arrived))
        {
            return Refusal.Blocked(
                $"work_report({item.Ref}, status:\"done\") on a wait without verify.",
                "a wait closes with what arrived — verify: says it, and becomes the wait's observation note",
                $"work_report(ref:\"{item.Ref}\", status:\"done\", verify:\"<what arrived>\")");
        }

        var unmet = DoneGuard.Evaluate(await DoneFactsAsync(connection, transaction, item, null, cancellationToken));
        return unmet.Count == 0 ? null : Refusal.Transition(item.Ref, "done", unmet);
    }

    /// <summary>The worker's close of a wait: the status write, claim release and journal line <c>work_status</c> makes.</summary>
    private static async Task CloseWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string arrived,
        CancellationToken cancellationToken)
    {
        await ApplyAsync(connection, transaction, item.Id, ItemStatus.Done, ItemResolution.Accepted, actor, cancellationToken);
        await InsertNoteAsync(
            connection, transaction, item.WorkspaceId, actor, NoteKind.Observation, arrived, item.Id, null, cancellationToken);
        await ReleaseAsync(connection, transaction, item, ItemStatus.Done, cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "status",
            $"{item.Ref} {Transitions.Name(item.Status)} → done: {Glyph.Flat(arrived)}",
            new
            {
                from = Transitions.Name(item.Status),
                to = "done",
                reason = arrived,
                forced = (string[]?)null,
            },
            cancellationToken);
    }

    /// <summary>The form of an assumption <paramref name="item"/> just opened that <c>work_assume</c> takes as printed.</summary>
    private static async Task<string> CallableAssumptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        string reference,
        CancellationToken cancellationToken)
    {
        const string sql =
            $$"""
            SELECT {{Queries.BoardQueries.CallableAssumptionSql}}
            FROM   assumption a
            LEFT   JOIN item s ON s.id = a.ref_scope_id
            WHERE  a.scope_item_id = @item
              AND  a.state = 'open'
              AND  a.ref = @ref;
            """;

        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            sql,
            new { item = item.Id, @ref = reference },
            transaction,
            cancellationToken: cancellationToken)) ?? reference;
    }

    private static string Raw(ReportArgs args) => string.Join(
        "\n",
        $"STATUS: {args.Status.Trim()}",
        $"FILES: {string.Join(" · ", args.Files ?? [])}",
        $"VERIFY: {args.Verify ?? string.Empty}",
        $"RED-CHECK: {args.RedCheck ?? string.Empty}",
        $"DEVIATIONS: {args.Deviations ?? string.Empty}",
        $"ASSUMPTIONS: {string.Join(" · ", args.Assumptions ?? [])}",
        $"RISKS: {args.Risks ?? string.Empty}",
        $"NOT DONE: {args.NotDone ?? string.Empty}");

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// The verification row, the claim heartbeat and the <c>verified</c> event, inside the caller's
    /// transaction: <c>work_verify</c> commits it alone, the one-call close commits it with the move
    /// or rolls it back with the refusal.
    /// </summary>
    private async Task RecordVerificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        VerifyRunner runner,
        string? command,
        string resultLine,
        bool passed,
        string? counts,
        string? redCheck,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO verification
                (id, workspace_id, item_id, command, result_line, passed, counts, run_by, red_check, session_id)
            VALUES (@id, @workspace, @item, @command, @result, @passed, @counts::jsonb,
                    @runBy::verify_runner, @redCheck::jsonb, @session);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                session = actor.SessionId,
                workspace = item.WorkspaceId,
                item = item.Id,
                command = Blank(command) ? item.VerifyCmd : command!.Trim(),
                result = Glyph.Flat(resultLine),
                passed,
                counts = Blank(counts) ? null : counts,
                runBy = Pg.Label(runner),
                redCheck = Blank(redCheck) ? null : JsonSerializer.Serialize(new { note = redCheck }),
            },
            transaction,
            cancellationToken: cancellationToken));

        await HeartbeatAsync(
            connection,
            transaction,
            item.Id,
            actor,
            await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken),
            cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "verified",
            $"{item.Ref} {(passed ? "green" : "RED")} by {Pg.Label(runner)}: {Glyph.Flat(resultLine)}",
            new { command, result_line = resultLine, passed, run_by = Pg.Label(runner) },
            cancellationToken);
    }
}
