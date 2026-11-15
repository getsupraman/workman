using System.Net;
using Dapper;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.App.AdminApi;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// deadlines/2: a workspace's time zone — the zone a date-only due or not_before is read in — is set by an admin of that
/// workspace through <c>PATCH /api/admin/tenants/{id}/settings</c>. An unknown IANA name is refused naming real ones, and
/// a member or another workspace's admin gets 403.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class TimezoneSettingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Timezone_an_admin_sets_it_in_canonical_form_and_reads_it_back()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var a = world.A.WorkspaceId;
        await MakeAdminAsync(world, await world.UserIdAsync(world.UserA), a);
        var admin = await world.CookieAsync(world.UserA);

        var before = await JsonAsync(await admin.GetAsync($"/api/admin/tenants/{a}/settings", Ct), HttpStatusCode.OK);
        Assert.Equal("UTC", before.GetProperty("timezone").GetString());

        var set = await JsonAsync(
            await SendAsync(admin, HttpMethod.Patch, $"/api/admin/tenants/{a}/settings", new { timezone = "europe/rome" }),
            HttpStatusCode.OK);
        Assert.Equal("Europe/Rome", set.GetProperty("timezone").GetString());

        var after = await JsonAsync(await admin.GetAsync($"/api/admin/tenants/{a}/settings", Ct), HttpStatusCode.OK);
        Assert.Equal("Europe/Rome", after.GetProperty("timezone").GetString());
        Assert.Equal("Europe/Rome", await StoredAsync(a));
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons", "is not an IANA time zone this server knows")]
    [InlineData("", "Name the time zone as an IANA name")]
    public async Task Timezone_an_unknown_or_missing_name_is_refused_and_nothing_changes(string timezone, string error)
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var a = world.A.WorkspaceId;
        await MakeAdminAsync(world, await world.UserIdAsync(world.UserA), a);
        var admin = await world.CookieAsync(world.UserA);

        var refused = await SendAsync(admin, HttpMethod.Patch, $"/api/admin/tenants/{a}/settings", new { timezone });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var message = await ErrorAsync(refused);
        Assert.Contains(error, message, StringComparison.Ordinal);
        Assert.Contains("Europe/Rome", message, StringComparison.Ordinal);
        Assert.Null(await StoredAsync(a));
    }

    [Fact]
    public async Task Timezone_a_member_and_another_workspaces_admin_get_403()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var a = world.A.WorkspaceId;
        var b = world.B.WorkspaceId;
        await MakeAdminAsync(world, await world.UserIdAsync(world.UserA), a);

        var member = await world.CookieAsync(world.UserB);
        var adminOfA = await world.CookieAsync(world.UserA);

        foreach (var client in new[] { member, adminOfA })
        {
            var patch = await SendAsync(client, HttpMethod.Patch, $"/api/admin/tenants/{b}/settings", new { timezone = "Europe/Rome" });
            Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
            var get = await client.GetAsync($"/api/admin/tenants/{b}/settings", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        }

        Assert.Null(await StoredAsync(b));
    }

    private async Task<string?> StoredAsync(Guid workspace)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT settings->>'timezone' FROM workspace WHERE id = @workspace;", new { workspace });
    }
}
