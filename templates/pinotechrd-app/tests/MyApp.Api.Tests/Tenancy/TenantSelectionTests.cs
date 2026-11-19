using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Tenancy;
using Pinotechrd.Security.Enums;

namespace MyApp.Api.Tests.Tenancy;

// An anonymous endpoint marked TenantFromHeader (the login of the next unit) takes the tenant from the X-Tenant slug. A slug
// that does not lead to an active tenant leaves the request without a tenant, and every such case answers the same bytes, so the
// answer never tells which slugs exist. Real PostgreSQL, real host; the endpoint echoes the current tenant.
[Collection(PostgresContainer.Name)]
public class TenantSelectionTests(PostgresFixture postgres)
{
    private static async Task<HttpResponseMessage> SendAsync(AccessWorld world, string path, string[] slugs, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var slug in slugs)
        {
            request.Headers.Add("X-Tenant", slug);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> MarkedAsync(AccessWorld world, string slug, string? token = null) =>
        SendAsync(world, AccessHost.MarkedRoute, [slug], token);

    private static async Task<(bool IsSet, Guid? TenantId)> ReadEchoAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var id = root.GetProperty("tenantId");
        return (root.GetProperty("isSet").GetBoolean(), id.ValueKind == JsonValueKind.Null ? null : id.GetGuid());
    }

    // The whole answer as text, headers included except the per-request ones, to compare two answers byte for byte.
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase) && !h.Key.Contains("Trace", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => h.Key + "=" + string.Join(",", h.Value));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (int)response.StatusCode + "\n" + string.Join("\n", headers) + "\n" + body;
    }

    // Breaks when the slug is not looked up in the catalog, or the tenant found is not the one set for the request.
    [Fact]
    public async Task TheSlugOfAnActiveTenantSetsThatTenantForTheRequest()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var forA = await MarkedAsync(world, AccessWorld.SlugA);
        using var forB = await MarkedAsync(world, AccessWorld.SlugB);

        Assert.Equal((true, (Guid?)world.TenantA), await ReadEchoAsync(forA));
        Assert.Equal((true, (Guid?)world.TenantB), await ReadEchoAsync(forB));
    }

    // Breaks when a slug that does not match the pattern, does not exist, or differs only in case is resolved to a tenant,
    // or when the request is refused instead of left without a tenant.
    [Theory]
    [InlineData("nobody")]
    [InlineData("A")]
    [InlineData("a--b")]
    [InlineData("-a")]
    [InlineData("a b")]
    [InlineData("a_b")]
    public async Task ASlugThatIsUnknownOrDoesNotFitThePatternLeavesTheRequestWithoutATenant(string slug)
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var response = await MarkedAsync(world, slug);

        Assert.Equal((false, (Guid?)null), await ReadEchoAsync(response));
    }

    // Breaks when the length of the slug is not bounded by the pattern (a 64 character slug is longer than a valid one).
    [Fact]
    public async Task ASlugOf64CharactersLeavesTheRequestWithoutATenant()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var response = await MarkedAsync(world, new string('a', 64));

        Assert.Equal((false, (Guid?)null), await ReadEchoAsync(response));
    }

    // Breaks when a tenant that is not active can be selected: provisioning, suspended and cancelled tenants are not usable.
    [Theory]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    public async Task ATenantThatIsNotActiveLeavesTheRequestWithoutATenant(TenantStatus status)
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, status);

        using var response = await MarkedAsync(world, AccessWorld.SlugA);

        Assert.Equal((false, (Guid?)null), await ReadEchoAsync(response));
    }

    // Breaks when the status is cached or read once: the same slug leads to the tenant again as soon as it is active.
    [Fact]
    public async Task ATenantThatBecomesActiveAgainCanBeSelectedAgainWithoutARestart()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);
        using var suspended = await MarkedAsync(world, AccessWorld.SlugA);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Active);

        using var active = await MarkedAsync(world, AccessWorld.SlugA);

        Assert.False((await ReadEchoAsync(suspended)).IsSet);
        Assert.Equal((true, (Guid?)world.TenantA), await ReadEchoAsync(active));
    }

    // Breaks when a "no tenant" case differs from another in status, headers or body: the answer would tell an unknown slug
    // from a suspended tenant, a cancelled one or a malformed slug (enumeration of tenants).
    [Fact]
    public async Task EveryNoTenantCaseAnswersTheSameStatusHeadersAndBody()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantB, TenantStatus.Cancelled);

        var prints = new List<string>();
        foreach (var slug in new[] { "nobody", "A", "a--b", new string('a', 64), AccessWorld.SlugA, AccessWorld.SlugB })
        {
            using var response = await MarkedAsync(world, slug);
            Assert.False((await ReadEchoAsync(response)).IsSet, $"'{slug}' selected a tenant.");
            prints.Add(await FingerprintAsync(response));
        }

        Assert.Single(prints.Distinct(StringComparer.Ordinal));
    }

    // Breaks when a missing X-Tenant is let through as "no tenant" instead of being refused as a malformed request.
    [Fact]
    public async Task ARequestWithoutTheHeaderIsAnswered400BadRequest()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var response = await SendAsync(world, AccessHost.MarkedRoute, []);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
    }

    // Breaks when the header given twice is accepted (the first value, the last, or the joined text).
    [Fact]
    public async Task TheHeaderGivenTwiceIsAnswered400BadRequest()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var response = await SendAsync(world, AccessHost.MarkedRoute, [AccessWorld.SlugA, AccessWorld.SlugB]);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
    }

    // Breaks when the step runs on endpoints that did not ask for it: a header would then choose the tenant anywhere.
    [Fact]
    public async Task AnAnonymousEndpointWithoutTheMarkerIgnoresTheHeader()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var withSlug = await SendAsync(world, AccessHost.UnmarkedRoute, [AccessWorld.SlugA]);
        using var twice = await SendAsync(world, AccessHost.UnmarkedRoute, [AccessWorld.SlugA, AccessWorld.SlugB]);
        using var without = await SendAsync(world, AccessHost.UnmarkedRoute, []);

        Assert.Equal((false, (Guid?)null), await ReadEchoAsync(withSlug));
        Assert.Equal((false, (Guid?)null), await ReadEchoAsync(twice));
        Assert.Equal((false, (Guid?)null), await ReadEchoAsync(without));
    }

    // Breaks when the header can override the tenant of a token: A's token with B's slug must be refused, not served for B.
    [Fact]
    public async Task ATokenOfOneTenantWithTheSlugOfAnotherIsAnswered401()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        var token = AccessJwt.TokenFor(user, world.TenantA);

        using var other = await MarkedAsync(world, AccessWorld.SlugB, token);
        using var same = await MarkedAsync(world, AccessWorld.SlugA, token);

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Equal((true, (Guid?)world.TenantA), await ReadEchoAsync(same));
    }

    // Breaks when the contract does not tell a client that the endpoint needs X-Tenant, or tells it for an endpoint that ignores it.
    [Fact]
    public async Task TheContractDocumentsARequiredTenantHeaderOnMarkedEndpointsOnly()
    {
        using var factory = AccessHost.WithoutDatabase();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var document = OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var marked = OpenApiContract.Paths(document)[AccessHost.MarkedRoute]!["get"]!.AsObject();
        var unmarked = OpenApiContract.Paths(document)[AccessHost.UnmarkedRoute]!["get"]!.AsObject();
        var parameter = marked["parameters"]!.AsArray()
            .Select(p => p!.AsObject())
            .Single(p => string.Equals(p["name"]!.GetValue<string>(), "X-Tenant", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("header", parameter["in"]!.GetValue<string>());
        Assert.True(parameter["required"]!.GetValue<bool>());
        Assert.DoesNotContain(
            unmarked["parameters"]?.AsArray() ?? [],
            p => string.Equals(p!["name"]!.GetValue<string>(), "X-Tenant", StringComparison.OrdinalIgnoreCase));
    }

    // Breaks when the answer to a token plus a slug depends on what the slug is: another active tenant, an unknown slug, a
    // suspended, cancelled or provisioning tenant all answer 401, so a token holder cannot probe which slugs exist or their state.
    [Theory]
    [InlineData("b", TenantStatus.Active)]
    [InlineData("nobody", TenantStatus.Active)]
    [InlineData("b", TenantStatus.Suspended)]
    [InlineData("b", TenantStatus.Cancelled)]
    [InlineData("b", TenantStatus.Provisioning)]
    public async Task ATokenOfOneTenantWithASlugThatIsNotItsOwnIsAnswered401WhateverTheSlugIs(string slug, TenantStatus statusOfB)
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantB, statusOfB);
        var token = AccessJwt.TokenFor(await world.AddUserAsync(world.TenantA), world.TenantA);

        using var response = await MarkedAsync(world, slug, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Breaks when the 401 of one case differs from another in headers or body (for example a challenge that names the reason).
    [Fact]
    public async Task EveryRefusedTokenAndSlugPairAnswersTheSameStatusHeadersAndBody()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var token = AccessJwt.TokenFor(await world.AddUserAsync(world.TenantA), world.TenantA);
        var prints = new List<string>();

        foreach (var (slug, statusOfB) in new[]
                 {
                     (AccessWorld.SlugB, TenantStatus.Active),
                     ("nobody", TenantStatus.Active),
                     (AccessWorld.SlugB, TenantStatus.Suspended),
                     (AccessWorld.SlugB, TenantStatus.Cancelled),
                 })
        {
            await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantB, statusOfB);
            using var response = await MarkedAsync(world, slug, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            prints.Add(WithoutTraceId(await FingerprintAsync(response)));
        }

        Assert.True(prints.Distinct(StringComparer.Ordinal).Count() == 1, string.Join("\n=====\n", prints));
    }

    // The trace id of a problem body is per request, like the trace headers FingerprintAsync leaves out.
    private static string WithoutTraceId(string print) =>
        System.Text.RegularExpressions.Regex.Replace(print, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"-\"");

    // Breaks when the token's own tenant is refused or reselected once its slug is sent: the same tenant is served normally.
    [Fact]
    public async Task ATokenWithTheSlugOfItsOwnTenantIsServedForThatTenant()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var token = AccessJwt.TokenFor(await world.AddUserAsync(world.TenantB), world.TenantB);

        using var response = await MarkedAsync(world, AccessWorld.SlugB, token);

        Assert.Equal((true, (Guid?)world.TenantB), await ReadEchoAsync(response));
    }

    // Breaks when a catalog that fails (database down) is read as "no such tenant": the request must end in 500, and the
    // endpoint must not answer its normal 200 for a slug that was never checked.
    [Fact]
    public async Task ACatalogThatFailsOnTheSlugLookupEndsTheRequestIn500()
    {
        using var world = await AccessWorld.StartAsync(
            postgres, factory => factory.WithServices(services => services.AddScoped<ITenantCatalog, ThrowingTenantCatalog>()));

        using var response = await MarkedAsync(world, AccessWorld.SlugA);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
