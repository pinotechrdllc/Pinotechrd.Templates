using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Web;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// The readiness probe with tenant persistence in the code base: it needs no tenant, and its own command is the only one
// the tracing filter skips. Through the real host. Needs Docker.
[Collection(PostgresContainer.Name)]
public class TenantReadinessTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when the probe needs a tenant (it runs without any scope's tenant) or fails against a database that has no
    // tenant schema at all.
    [Fact]
    public async Task ReadinessAnswersHealthyWithoutATenant()
    {
        using var factory = new ApiHostFactory().WithDatabase(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateClient();
        await WarmUpAsync(factory);

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, Token);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(Token));
    }

    // Breaks when the probe is skipped by the statement text instead of by its own command (an application command with
    // the same text 'SELECT 1' would then go untraced), or when the probe is traced. The application's command comes first
    // and the probe runs twice around it; only the application's command may leave a span.
    [Fact]
    public async Task AnApplicationSelectOneIsTracedAndTheReadinessProbeIsNot()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connection).Database!;
        using var factory = new ApiHostFactory().WithDatabase(connection);
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        await using (var application = dataSource.CreateCommand("SELECT 1"))
        {
            Assert.Equal(1, await application.ExecuteScalarAsync(Token));
        }

        Assert.True(await TelemetryCapture.UntilAsync(() => SelectOneSpans(capture, database) >= 1), "The application's SELECT 1 was not traced.");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthRegistration.ReadyPath, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthRegistration.ReadyPath, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthRegistration.ReadyPath, Token)).StatusCode);
        await using (var marker = dataSource.CreateCommand("SELECT 2 AS after_probes"))
        {
            await marker.ExecuteScalarAsync(Token);
        }

        Assert.True(await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.Tags.TryGetValue("db.query.text", out var t) && t.Contains("after_probes", StringComparison.Ordinal))));
        Assert.Equal(1, SelectOneSpans(capture, database));
        Assert.Equal(DatabaseHealthCheck.Query, "SELECT 1");
    }

    // Breaks when the statement that binds a connection to the tenant is traced (it would add a span to every request), or
    // when the tenant's own statements are not traced. The host's data source carries the tracing filter, so a tenant context
    // is built on it.
    [Fact]
    public async Task ATenantQueryIsTracedAndTheBindingStatementIsNot()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connection).Database!;
        using var factory = new ApiHostFactory().WithDatabase(connection);
        using var capture = factory.CaptureTelemetry();
        await using var rig = TenantRig.StartOn(connection, factory.Services.GetRequiredService<NpgsqlDataSource>(), ownsDataSource: false);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);

        await using (var scope = rig.ScopeFor(tenant))
        {
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            context.Items.Add(new FixtureItem { Name = "traced-row" });
            await context.SaveChangesAsync(Token);
            Assert.Single(await context.Items.ToListAsync(Token));
        }

        await using (var marker = rig.DataSource.CreateCommand("SELECT 3 AS after_tenant_work"))
        {
            await marker.ExecuteScalarAsync(Token);
        }

        Assert.True(await TelemetryCapture.UntilAsync(() => Texts(capture, database).Any(t => t.Contains("after_tenant_work", StringComparison.Ordinal))));
        var texts = Texts(capture, database);
        Assert.Contains(texts, t => t.Contains("SELECT", StringComparison.Ordinal) && t.Contains("access_items", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("INSERT INTO access_items", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("set_config", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(texts, t => t.Contains("search_path", StringComparison.OrdinalIgnoreCase));
    }

    // The probe has a two-second limit, which a cold first connection of a loaded test run can exceed: a connection is
    // opened (and returned to the pool) first.
    private static async Task WarmUpAsync(ApiHostFactory factory)
    {
        await using var warm = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT 0 AS warm_up");
        await warm.ExecuteScalarAsync(Token);
    }

    private static List<string> Texts(TelemetryCapture capture, string database) =>
        [.. capture.Spans
            .Where(s => s.Source == DatabaseRegistration.TelemetryName && s.Tags.Values.Contains(database))
            .Select(s => s.Tags.GetValueOrDefault("db.query.text") ?? string.Empty)];

    private static int SelectOneSpans(TelemetryCapture capture, string database) =>
        capture.Spans.Count(s => s.Source == DatabaseRegistration.TelemetryName
            && s.Tags.TryGetValue("db.query.text", out var text)
            && text == "SELECT 1"
            && s.Tags.Values.Contains(database));
}
