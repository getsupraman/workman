using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workman.Core.Board;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Core.Writing;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// mcp-fit/1. A followup created under project <c>acme</c> as <c>ga4-access</c> beside the run
/// <c>acme/ga4-access</c>: <c>work_get(acme/ga4-access)</c> was refused as ambiguous and offered only the run,
/// so the followup could not be reached at all. Same for a project and a run sharing a name. New collisions
/// are refused on creation; existing ones stay readable, each candidate offered by a form that resolves to it.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class QualifiedRefCollisionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_ambiguous_ref_offers_a_form_for_every_candidate_and_each_resolves_to_its_item()
    {
        var w = await CollidedAsync();
        var reader = Reader;
        var home = (ScopeResult.Found)await reader.ResolveAsync(w.Slug, "acme", Ct);

        var text = await reader.GetAsync(home, ["acme/ga4-access"], [], Ct);

        var offered = Offered(text);
        Assert.Equal(2, offered.Count);

        var reached = new List<Guid>();
        foreach (var form in offered)
        {
            reached.Add(Assert.Single(await ResolveAsync(w.WorkspaceId, form)).Id);
            Assert.DoesNotContain("qualify it", await reader.GetAsync(home, [form], [], Ct), StringComparison.Ordinal);
        }

        Assert.Equal(new[] { w.Run, w.Followup }.Order(), reached.Order());

        var scoped = Assert.IsType<ScopeResult.Ambiguous>(await reader.ResolveAsync(w.Slug, "acme/ga4-access", Ct));
        Assert.Equal(offered.Order(StringComparer.Ordinal), scoped.Candidates.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_project_and_a_run_sharing_its_name_are_each_reachable()
    {
        var w = await CollidedAsync();

        foreach (var asked in new[] { "neo", "neo/neo" })
        {
            var refused = await Reader.GetAsync(
                (ScopeResult.Found)await Reader.ResolveAsync(w.Slug, "acme", Ct), [asked], [], Ct);
            if (!refused.Contains("qualify it", StringComparison.Ordinal))
            {
                continue;
            }

            var reached = new List<Guid>();
            foreach (var form in Offered(refused))
            {
                reached.Add(Assert.Single(await ResolveAsync(w.WorkspaceId, form)).Id);
            }

            Assert.Equal(reached.Distinct().Count(), reached.Count);
        }

        Assert.Equal(w.Neo, Assert.Single(await ResolveAsync(w.WorkspaceId, "neo/neo")).Id);
        Assert.Equal(w.NeoRun, Assert.Single(await ResolveAsync(w.WorkspaceId, "neo/neo/neo")).Id);
        Assert.Equal(w.ElsewhereNeo, Assert.Single(await ResolveAsync(w.WorkspaceId, "acme/neo")).Id);

        var board = new BoardQueries(fixture.DataSource);
        foreach (var id in new[] { w.Neo, w.NeoRun, w.ElsewhereNeo })
        {
            var item = (await board.ResolveAsync(w.WorkspaceId, "neo", Ct)).Single(m => m.Id == id);
            var form = await board.UniqueRefAsync(w.WorkspaceId, item, Ct);
            Assert.NotNull(form);
            Assert.Equal(id, Assert.Single(await ResolveAsync(w.WorkspaceId, form!)).Id);
        }
    }

    [Fact]
    public async Task Existing_colliding_data_is_left_as_it_was()
    {
        var w = await CollidedAsync();
        var before = await SnapshotAsync(w.WorkspaceId);

        var home = (ScopeResult.Found)await Reader.ResolveAsync(w.Slug, "acme", Ct);
        foreach (var asked in new[] { "acme/ga4-access", "ga4-access/ga4-access", "acme/acme/ga4-access", "neo", "neo/neo" })
        {
            await Reader.GetAsync(home, [asked], [], Ct);
        }

        Assert.Equal(before, await SnapshotAsync(w.WorkspaceId));
        Assert.Equal(2, before.Count(r => r.StartsWith("ga4-access ", StringComparison.Ordinal)));

        async Task<List<string>> SnapshotAsync(Guid workspaceId)
        {
            await using var db = fixture.NewContext();
            return (await db.Items.Where(i => i.WorkspaceId == workspaceId)
                    .Select(i => new { i.Id, i.Ref, i.Revision, i.ParentId }).ToListAsync(Ct))
                .Select(r => $"{r.Ref} {r.Id} {r.Revision} {r.ParentId}")
                .Order(StringComparer.Ordinal)
                .ToList();
        }
    }

    [Fact]
    public async Task Work_create_refuses_a_project_level_item_named_like_a_run_under_it_and_names_a_free_ref()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateAsync(world, new NewItemArg("project", "Acme", Ref: "acme"));
        await CreateAsync(world, new NewItemArg("run", "GA4 access", Ref: "ga4-access", Parent: "acme"));

        var refused = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("followup", "Grant GA4 read access", Ref: "ga4-access", Parent: "acme")], Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("\"acme/ga4-access\" would name both this followup and the run ga4-access", refused.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"ga4-access-2\"", refused.Text, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(world, "ga4-access"));

        await CreateAsync(world, new NewItemArg("followup", "Grant GA4 read access", Ref: "ga4-access-2", Parent: "acme"));
        Assert.Single(await ResolveAsync(world.WorkspaceId, "acme/ga4-access"));
    }

    [Fact]
    public async Task Work_create_refuses_a_run_named_like_a_project_level_item_or_its_own_project()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateAsync(world, new NewItemArg("project", "Neo", Ref: "neo"));
        await CreateAsync(world, new NewItemArg("followup", "Map the domain", Ref: "domain-map", Parent: "neo"));

        var beside = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("run", "Domain map", Ref: "domain-map", Parent: "neo")], Ct);
        var itself = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("run", "Neo itself", Ref: "neo", Parent: "neo")], Ct);

        foreach (var (refused, names) in new[] { (beside, "the followup domain-map"), (itself, "the project neo itself") })
        {
            Assert.True(refused.Refused, refused.Text);
            RefusalShape.SelfCorrecting(refused.Text);
            Assert.Contains(names, refused.Text, StringComparison.Ordinal);
        }

        Assert.Contains("ref:\"domain-map-2\"", beside.Text, StringComparison.Ordinal);
        Assert.Contains("ref:\"neo-2\"", itself.Text, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(world, "domain-map"));
        Assert.Equal(1, await CountAsync(world, "neo"));
    }

    [Fact]
    public async Task A_suggested_slug_skips_the_name_of_a_run_under_the_project()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await CreateAsync(world, new NewItemArg("project", "Acme", Ref: "acme"));
        await CreateAsync(world, new NewItemArg("run", "GA4 access", Ref: "ga4-access", Parent: "acme"));

        var unnamed = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("followup", "GA4 access", Parent: "acme")], Ct);

        Assert.True(unnamed.Refused, unnamed.Text);
        Assert.Contains("ref:\"ga4-access-2\"", unnamed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_attention_and_history_print_refs_outside_the_readers_run_run_qualified()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        await ForceDoneAsync(world, "9.1");
        await ForceDoneAsync(world, "1.1");

        string project;
        await using (var db = fixture.NewContext())
        {
            project = (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
        }

        var reader = world.Reader;
        var above = await world.ScopeAsync(project);

        var audit = await reader.AuditAsync(above, Ct);
        Assert.Contains("✗ run-b/9.1", audit, StringComparison.Ordinal);
        Assert.Contains("✗ run-a/1.1", audit, StringComparison.Ordinal);
        Assert.Contains("work_verify(ref:\"run-b/9.1\"", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("✗ 9.1 ", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("ref:\"9.1\"", audit, StringComparison.Ordinal);

        var attention = Attention.Full(project, await reader.AttentionAsync(above, Ct));
        Assert.Contains("run-b/9.1 is done with no orchestrator verification", attention, StringComparison.Ordinal);
        Assert.Contains("run-a/1.1 is done with no orchestrator verification", attention, StringComparison.Ordinal);
        Assert.DoesNotContain(" 9.1 is done", attention, StringComparison.Ordinal);

        var history = await reader.HistoryAsync(world.WorkspaceId, above, 0, 50, Ct);
        Assert.Contains(" run-b/9.1 ", history, StringComparison.Ordinal);
        Assert.Contains(" run-a/1.1 ", history, StringComparison.Ordinal);
        Assert.DoesNotMatch(BareRefRow(), history);

        // Inside its own run a ref stays bare.
        var inside = await world.ScopeAsync("run-b");
        Assert.Contains("✗ 9.1 ", await reader.AuditAsync(inside, Ct), StringComparison.Ordinal);
        Assert.Contains("9.1 is done with no orchestrator verification",
            Attention.Full("run-b", await reader.AttentionAsync(inside, Ct)), StringComparison.Ordinal);
        Assert.DoesNotContain("run-b/9.1", await reader.HistoryAsync(world.WorkspaceId, inside, 0, 50, Ct), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\d+\s+9\.1\s", RegexOptions.Multiline)]
    private static partial Regex BareRefRow();

    [GeneratedRegex(@"work_get\(refs:\[(?<forms>[^\]]*)\]\)")]
    private static partial Regex OfferedForms();

    private static List<string> Offered(string text)
    {
        var line = text.Split('\n').Single(l => l.Contains("qualify it", StringComparison.Ordinal));
        var forms = OfferedForms().Match(line).Groups["forms"].Value;
        return [.. forms.Split(',').Select(f => f.Trim().Trim('"')).Where(f => f.Length > 0)];
    }

    private BoardReader Reader => new(new WorkmanQueries(fixture.DataSource), new BoardQueries(fixture.DataSource));

    private Task<IReadOnlyList<ScopeRef>> ResolveAsync(Guid workspaceId, string reference)
        => new BoardQueries(fixture.DataSource).ResolveAsync(workspaceId, reference, Ct);

    private static async Task CreateAsync(WriteWorld world, NewItemArg item)
    {
        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator, [item], Ct);
        Assert.False(created.Refused, created.Text);
    }

    private async Task<int> CountAsync(WriteWorld world, string reference)
    {
        await using var db = fixture.NewContext();
        return await db.Items.CountAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == reference, Ct);
    }

    private static async Task ForceDoneAsync(WriteWorld world, string reference)
    {
        var item = await world.ItemAsync(reference);
        await WriteSeed.StatusAsync(world, item.Id, ItemStatus.InProgress);
        var forced = await world.Writer.StatusAsync(
            world.WorkspaceId, reference, "done", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "closed by hand for the test", Force: true), Ct);
        Assert.False(forced.Refused, forced.Text);
    }

    private sealed record Collided(string Slug, Guid WorkspaceId, Guid Run, Guid Followup, Guid Neo, Guid NeoRun, Guid ElsewhereNeo);

    /// <summary>
    /// Seeded directly, as data written before the guard: project <c>acme</c> with a run <c>ga4-access</c> and a
    /// followup <c>ga4-access</c> numbered in the project; project <c>neo</c> with a run <c>neo</c>; and a run
    /// <c>neo</c> under <c>acme</c>.
    /// </summary>
    private async Task<Collided> CollidedAsync()
    {
        await using var db = fixture.NewContext();

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = $"w-{Guid.CreateVersion7():n}" };
        db.Workspaces.Add(workspace);

        var acme = Seed.NewItem(workspace.Id, null, "project", "acme", 1m);
        var neo = Seed.NewItem(workspace.Id, null, "project", "neo", 2m);
        db.Items.AddRange(acme, neo);
        await db.SaveChangesAsync(Ct);

        var run = Seed.NewItem(workspace.Id, acme.Id, "run", "ga4-access", 1m);
        var followup = Seed.NewItem(workspace.Id, acme.Id, "followup", "ga4-access", 2m);
        var neoRun = Seed.NewItem(workspace.Id, neo.Id, "run", "neo", 1m);
        var elsewhere = Seed.NewItem(workspace.Id, acme.Id, "run", "neo", 3m);
        db.Items.AddRange(run, followup, neoRun, elsewhere);
        await db.SaveChangesAsync(Ct);

        return new Collided(workspace.Slug, workspace.Id, run.Id, followup.Id, neo.Id, neoRun.Id, elsewhere.Id);
    }
}
