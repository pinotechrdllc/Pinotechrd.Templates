using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Pinotechrd.Security.Extensions;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>Q4 (status codes) and Q6 (tenant resolution) on an in-process ASP.NET Core host (TestServer).</summary>
public static class Q4Q6Http
{
    public static async Task<WebApplication> StartAsync(
        NpgsqlDataSource shared, bool tenantBeforeAuthentication, Action<IServiceCollection>? extra = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSpikeSecurity(shared);
        extra?.Invoke(builder.Services);

        var app = builder.Build();
        if (tenantBeforeAuthentication)
        {
            app.UseTenantResolution();
            app.UseAuthentication();
        }
        else
        {
            // The order the library's README documents.
            app.UseAuthentication();
            app.UseTenantResolution();
        }

        app.UseAuthorization();
        app.MapGet("/api/sales", () => "sales").RequireAuthorization("SALES.VIEW");
        app.MapGet("/api/reports", () => "reports").RequireAuthorization("REPORTS.VIEW");
        app.MapGet("/api/whoami", (TenantContext t) => t.TenantId ?? "(none)");
        app.MapPost("/api/whoami", (TenantContext t) => t.TenantId ?? "(none)");
        app.MapGet("/api/a/whoami", (TenantContext t) => t.TenantId ?? "(none)");
        app.MapGet("/public/whoami", (TenantContext t) => t.TenantId ?? "(none)");
        app.MapGet("/healthy-admin", (TenantContext t) => t.TenantId ?? "(none)");
        await app.StartAsync();
        return app;
    }

    public static async Task<string> AccessTokenAsync(WebApplication app, string tenant)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<IPinotechrdAuthManager<AppUser>>()
            .LoginAsync(Seed.UserName, Seed.Password, tenant);
        return result.Token?.AccessToken ?? throw new InvalidOperationException(result.ErrorMessage);
    }

    public static async Task<Outcome> Q4Async(NpgsqlDataSource shared)
    {
        var q = new Outcome("Q4", required: true);
        await using var app = await StartAsync(shared, tenantBeforeAuthentication: false);
        var client = app.GetTestClient();
        var token = await AccessTokenAsync(app, "a");

        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        q.Fact($"registered schemes: {string.Join(", ", (await schemes.GetAllSchemesAsync()).Select(s => s.Name))}; " +
            $"default authenticate={(await schemes.GetDefaultAuthenticateSchemeAsync())?.Name}, challenge={(await schemes.GetDefaultChallengeSchemeAsync())?.Name}, " +
            $"forbid={(await schemes.GetDefaultForbidSchemeAsync())?.Name}, sign-in={(await schemes.GetDefaultSignInSchemeAsync())?.Name}");

        var anonymous = await SendAsync(client, HttpMethod.Get, "/api/reports", tenantHeader: "a");
        var anonymousNoTenant = await SendAsync(client, HttpMethod.Get, "/api/reports");
        var allowed = await SendAsync(client, HttpMethod.Get, "/api/sales", bearer: token);
        var forbidden = await SendAsync(client, HttpMethod.Get, "/api/reports", bearer: token);
        q.Fact($"anonymous, TenantId header, protected endpoint: {anonymous}");
        q.Fact($"anonymous, no tenant, protected endpoint: {anonymousNoTenant} (tenant middleware answers before authorization)");
        q.Fact($"bearer of tenant a, permission granted (SALES.VIEW): {allowed}");
        q.Fact($"bearer of tenant a, module not in plan (REPORTS.VIEW): {forbidden}");

        var ok = anonymous.Status == HttpStatusCode.Unauthorized && forbidden.Status == HttpStatusCode.Forbidden && allowed.Status == HttpStatusCode.OK;
        if (!ok && (anonymous.Status == HttpStatusCode.Redirect || forbidden.Status == HttpStatusCode.Redirect))
        {
            // Minimal setting: forbid with the bearer scheme too.
            await using var fixedApp = await StartAsync(shared, false, s => s.Configure<AuthenticationOptions>(o =>
            {
                o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                o.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
            }));
            var fixedClient = fixedApp.GetTestClient();
            var a2 = await SendAsync(fixedClient, HttpMethod.Get, "/api/reports", tenantHeader: "a");
            var f2 = await SendAsync(fixedClient, HttpMethod.Get, "/api/reports", bearer: await AccessTokenAsync(fixedApp, "a"));
            q.Fact($"with DefaultChallengeScheme and DefaultForbidScheme = Bearer: anonymous {a2}; forbidden {f2}");
            ok = a2.Status == HttpStatusCode.Unauthorized && f2.Status == HttpStatusCode.Forbidden;
        }

        q.Check(ok, "anonymous -> 401 and authenticated without permission -> 403, no redirect");
        q.Summary = q.Passed
            ? "401 for anonymous and 403 when authorization fails, no redirect and no extra setting: " +
              "the library sets the bearer scheme as default authenticate and challenge after AddIdentity, and forbid falls back to challenge. " +
              $"Without a tenant an /api request gets {anonymousNoTenant.Status:D} before authorization."
            : "a redirect or a wrong status remains";
        return q;
    }

    public static async Task<Outcome> Q6Async(NpgsqlDataSource shared)
    {
        var q = new Outcome("Q6", required: false);
        await using var app = await StartAsync(shared, tenantBeforeAuthentication: false);
        var client = app.GetTestClient();
        var tokenA = await AccessTokenAsync(app, "a");

        var header = await SendAsync(client, HttpMethod.Get, "/api/whoami", tenantHeader: "a");
        var query = await SendAsync(client, HttpMethod.Get, "/api/whoami?tenantId=a");
        var body = await SendAsync(client, HttpMethod.Post, "/api/whoami", json: "{\"tenantId\":\"a\"}");
        var claim = await SendAsync(client, HttpMethod.Get, "/api/whoami", bearer: tokenA);
        var claimVsHeader = await SendAsync(client, HttpMethod.Get, "/api/whoami", bearer: tokenA, tenantHeader: "b");
        var headerVsQuery = await SendAsync(client, HttpMethod.Get, "/api/whoami?tenantId=b", tenantHeader: "a");
        var path = await SendAsync(client, HttpMethod.Get, "/api/a/whoami");
        var missing = await SendAsync(client, HttpMethod.Get, "/api/whoami");
        var unknown = await SendAsync(client, HttpMethod.Get, "/api/whoami", tenantHeader: "zz");
        var upper = await SendAsync(client, HttpMethod.Get, "/api/whoami", tenantHeader: "A");
        var nonApi = await SendAsync(client, HttpMethod.Get, "/public/whoami");
        var nonApiUnknown = await SendAsync(client, HttpMethod.Get, "/public/whoami", tenantHeader: "zz");
        var skipPrefix = await SendAsync(client, HttpMethod.Get, "/healthy-admin", tenantHeader: "a");

        q.Fact($"header TenantId: a -> {header}");
        q.Fact($"query ?tenantId=a -> {query}");
        q.Fact($"JSON body {{\"tenantId\":\"a\"}} -> {body}");
        q.Fact($"claim tenant_id of a valid bearer -> {claim}");
        q.Fact($"claim a + header b -> {claimVsHeader} (claim wins when authentication ran first)");
        q.Fact($"header a + query b -> {headerVsQuery}");
        q.Fact($"path /api/a/whoami, no other source -> {path} (no path segment is read)");
        q.Fact($"missing, /api path -> {missing}");
        q.Fact($"unknown tenant -> {unknown}");
        q.Fact($"header 'A' (store compares exactly) -> {upper}");
        q.Fact($"missing, non-/api path -> {nonApi} (passes with no tenant)");
        q.Fact($"unknown tenant, non-/api path -> {nonApiUnknown}");
        q.Fact($"SkipPaths '/health' and request /healthy-admin with header a -> {skipPrefix} (raw prefix match, resolution skipped)");

        await using var early = await StartAsync(shared, tenantBeforeAuthentication: true);
        var earlyClient = early.GetTestClient();
        var earlyToken = await AccessTokenAsync(early, "a");
        var earlyMix = await SendAsync(earlyClient, HttpMethod.Get, "/api/whoami", bearer: earlyToken, tenantHeader: "b");
        var earlySales = await SendAsync(earlyClient, HttpMethod.Get, "/api/sales", bearer: earlyToken, tenantHeader: "b");
        q.Fact($"UseTenantResolution BEFORE UseAuthentication: bearer of a + header b -> {earlyMix}; protected SALES.VIEW -> {earlySales} " +
            "(the tenant comes from the client's header; the user of a is checked against b's data)");

        q.Check(header.Body == "a" && query.Body == "a" && body.Body == "a" && claim.Body == "a" && claimVsHeader.Body == "a",
            "claim, header, JSON body and query string each resolve the tenant; the claim wins over the header");
        q.Check(missing.Status == HttpStatusCode.BadRequest && unknown.Status == HttpStatusCode.BadRequest,
            "missing (on /api) and unknown tenant answer 400, as the library README documents");
        q.Summary = "reads claim 'tenant_id' (only if authenticated), header 'TenantId', JSON body 'tenantId', query 'tenantId', in that order; " +
            $"no path segment. Missing on /api: {missing.Status:D} {missing.ContentType} '{missing.Body}'; unknown: {unknown.Status:D} {unknown.ContentType} '{unknown.Body}'; " +
            $"missing elsewhere: passes with no tenant. Order matters: before authentication a header overrides the token's tenant ({earlyMix.Body}).";
        return q;
    }

    public sealed record Reply(HttpStatusCode Status, string Body, string ContentType, string? Location, string? WwwAuthenticate)
    {
        public override string ToString() =>
            $"{(int)Status} type={ContentType} body='{Body}'" +
            (Location is null ? string.Empty : $" location={Location}") +
            (WwwAuthenticate is null ? string.Empty : $" www-authenticate={WwwAuthenticate}");
    }

    public static async Task<Reply> SendAsync(
        HttpClient client, HttpMethod method, string url, string? tenantHeader = null, string? bearer = null, string? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (tenantHeader is not null)
        {
            request.Headers.Add("TenantId", tenantHeader);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(
            response.StatusCode,
            text.Length > 60 ? text[..60] : text,
            response.Content.Headers.ContentType?.ToString() ?? "(none)",
            response.Headers.Location?.ToString(),
            response.Headers.WwwAuthenticate.Count == 0 ? null : response.Headers.WwwAuthenticate.ToString());
    }
}
