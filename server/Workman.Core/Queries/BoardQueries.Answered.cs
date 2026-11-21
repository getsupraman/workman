namespace Workman.Core.Queries;

/// <summary>mcp-fit/7: an open question with a <c>decision</c> note from or to it — the newest one's body.</summary>
public sealed record AnsweredQuestionRow(string Ref, string QualifiedRef, Guid Id, string Decision);

public sealed partial class BoardQueries
{
    /// <summary>
    /// mcp-fit/7: open questions under the scope that a <c>decision</c> note already names, from or to, so the answer
    /// given in chat is on record and only the close is missing.
    /// </summary>
    public async Task<IReadOnlyList<AnsweredQuestionRow>> AnsweredQuestionsAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.ref AS "Ref",
                    CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                         AS "QualifiedRef",
                    i.id   AS "Id",
                    d.body AS "Decision",
                    {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             JOIN   LATERAL (
                      SELECT n.body FROM note n
                      WHERE  n.kind = 'decision' AND (n.from_item_id = i.id OR n.to_item_id = i.id)
                      ORDER  BY n.created_at DESC, n.id DESC LIMIT 1) d ON true
             WHERE  i.kind = 'question'
               AND  i.status NOT IN ('done','cancelled','superseded')
             ORDER  BY i.seq, i.id;
             """;

        return [.. (await ScopedAsync<AnsweredQuestionRow>(sql, new { scopes = Scoped.Ids([scopeItemId]) }, cancellationToken))[scopeItemId]];
    }
}
