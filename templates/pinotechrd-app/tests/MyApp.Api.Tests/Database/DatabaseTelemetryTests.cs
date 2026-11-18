using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Infrastructure.Database;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// Criteria 13 and 14: what the host's OpenTelemetry providers record for database work. Activity sources are
// process-wide, so each test finds its spans by a statement it chose. Needs Docker.
[Collection(PostgresContainer.Name)]
public class DatabaseTelemetryTests(PostgresFixture postgres)
{
    private const string ParameterValue = "param-secret-value-7781";

    private static bool IsDatabaseSpan(CapturedSpan span) => span.Source == DatabaseRegistration.TelemetryName;

    // Breaks when the database spans are not collected, are not client spans, lack the statement text, carry a
    // parameter value anywhere, or carry the user name or the password.
    [Fact]
    public async Task QueryRecordsAClientSpanWithTheStatementAndNoParameterValueOrCredential()
    {
        var marker = "probe_" + Guid.NewGuid().ToString("N");
        var statement = $"SELECT $1::text AS {marker}";
        using var factory = new ApiHostFactory().WithDatabase(await postgres.CreateDatabaseAsync());
        using var capture = factory.CaptureTelemetry();
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        await using (var command = dataSource.CreateCommand(statement))
        {
            command.Parameters.AddWithValue(ParameterValue);
            Assert.Equal(ParameterValue, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        Assert.True(await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => IsDatabaseSpan(s) && s.Texts.Any(t => t.Contains(marker, StringComparison.Ordinal)))));
        var span = Assert.Single(capture.Spans, s => IsDatabaseSpan(s) && s.Texts.Any(t => t.Contains(marker, StringComparison.Ordinal)));
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Contains(marker, span.Tags["db.query.text"], StringComparison.Ordinal);
        foreach (var database in capture.Spans.Where(IsDatabaseSpan))
        {
            Assert.DoesNotContain(database.Texts, t => t.Contains(ParameterValue, StringComparison.Ordinal));
            Assert.DoesNotContain(database.Texts, t => t.Contains(PostgresFixture.Password, StringComparison.Ordinal));
            Assert.DoesNotContain(database.Texts, t => t.Contains(PostgresFixture.Username, StringComparison.Ordinal));
        }
    }

    // Breaks when a healthy readiness probe records a database span (the README: health probes produce no span). The
    // control query after it proves this host's capture does see database spans.
    [Fact]
    public async Task HealthyReadinessProbeRecordsNoDatabaseSpan()
    {
        var marker = "control_" + Guid.NewGuid().ToString("N");
        using var factory = new ApiHostFactory().WithDatabase(await postgres.CreateDatabaseAsync());
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using (var control = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand($"SELECT 1 AS {marker}"))
        {
            await control.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => IsDatabaseSpan(s) && s.Texts.Any(t => t.Contains(marker, StringComparison.Ordinal)))));
        Assert.DoesNotContain(
            capture.Spans.Where(IsDatabaseSpan),
            s => s.Tags.TryGetValue("db.query.text", out var text) && text.Equals(DatabaseHealthCheck.Query, StringComparison.Ordinal));
    }

    // Breaks when a failing readiness probe records a database span (a failed open or a failed command).
    [Fact]
    public async Task UnhealthyReadinessProbeRecordsNoDatabaseSpan()
    {
        using var factory = new ApiHostFactory().WithDatabase(TestDatabase.PlaceholderConnectionString);
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.DoesNotContain(
            capture.Spans.Where(IsDatabaseSpan),
            s => s.Texts.Any(t => t.Contains(TestDatabase.PlaceholderPassword, StringComparison.Ordinal)
                || t.Equals(DatabaseHealthCheck.Query, StringComparison.Ordinal)));
    }
}
