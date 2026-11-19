using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.BuildingBlocks.Web;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

/// <summary>A route of the real host that touches a tenant context, so a failure of the tenant open reaches the error handler.</summary>
internal sealed class TenantFailureEndpoint : IEndpoint
{
    public const string Path = EndpointRegistration.RoutePrefix + "/tenant-failure-fixture";

    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapGet("/tenant-failure-fixture", async (AccessContext context, ICurrentTenantInitializer tenant, CancellationToken cancellation) =>
        {
            tenant.Initialize(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
            return Results.Ok(await context.Items.CountAsync(cancellation));
        }).AllowAnonymous();
}

// The failure of a tenant open through the real host: a plain 500, with nothing of the database in the body. Needs Docker.
[Collection(PostgresContainer.Name)]
public class DefaultPathHostTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when the default-path failure leaks to the client (the role, the database, the setting or the exception type), or is
    // not answered as the 500 internal_error of the error contract. The role and database names must be in the error log
    // instead, for the operator.
    [Fact]
    public async Task ARequestHittingADefaultPathFailureAnswersAPlain500AndTheNamesAreOnlyInTheLog()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        await using var drop = new DatabaseDrop(postgres, database);
        var admin = postgres.Connection(database);
        admin.Pooling = false;
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var alter = new NpgsqlCommand($"ALTER ROLE {PostgresFixture.Username} IN DATABASE \"{database}\" SET search_path = default_schema_marker", connection);
            await alter.ExecuteNonQueryAsync(Token);
        }

        await using var factory = new ApiHostFactory().WithDatabase(connectionString).WithServices(services =>
        {
            services.AddTenantDbContext<AccessContext>("Access");
            services.AddSingleton<IEndpoint, TenantFailureEndpoint>();
        });
        var logs = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(TenantFailureEndpoint.Path, Token);

        await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        var body = await response.Content.ReadAsStringAsync(Token);
        foreach (var secret in new[] { PostgresFixture.Username, database, "default_schema_marker", "pg_db_role_setting", "search_path", nameof(InvalidOperationException), "ALTER" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            logs.Events,
            e => e.Level == Serilog.Events.LogEventLevel.Error && e.TextsWithoutException().Any(t => t.Contains(database, StringComparison.Ordinal)));
    }
}

// The migrator's ApplicationRole: any role name PostgreSQL accepts, quoted safely, validated before any SQL.
[Collection(PostgresContainer.Name)]
public class ApplicationRoleTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Action<IServiceCollection> Register(string role) =>
        services =>
        {
            services.AddTenantDbContext<AccessContext>("Access");
            services.AddTenantMigrator(options => options.ApplicationRole = role);
        };

    private sealed class RoleDrop(PostgresFixture postgres, string role) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var admin = postgres.Connection("postgres");
            admin.Pooling = false;
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
#pragma warning disable CA2100 // The name is made by the test.
            await using var command = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    // Breaks when a role name with a dash and capitals (App-Role) is not quoted, so USAGE on extensions is not granted to it.
    [Fact]
    public async Task ARoleNamedWithADashAndCapitalsIsGrantedUsageOnTheExtensionsSchema()
    {
        var role = "App-Role-" + Guid.NewGuid().ToString("N")[..8];
        await using var dropRole = new RoleDrop(postgres, role);
        await using var rig = await TenantRig.StartAsync(postgres, register: Register(role));
        await using var dropDatabase = new DatabaseDrop(postgres, rig.Database);
        await rig.ExecuteAsync($"CREATE ROLE \"{role}\"");

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(await rig.ScalarAsync<bool>($"SELECT has_schema_privilege('{role}', 'extensions', 'USAGE')"));
    }

    // Breaks when the role name is put into SQL unquoted or with its quotes unescaped: a hostile name must fail as a role that
    // does not exist (42704), and the table it names must survive.
    [Fact]
    public async Task AHostileRoleNameFailsAsAnUnknownRoleAndDropsNothing()
    {
        const string Hostile = "x\"; DROP TABLE public.victim; --";
        await using var rig = await TenantRig.StartAsync(postgres, register: Register(Hostile));
        await using var dropDatabase = new DatabaseDrop(postgres, rig.Database);
        await rig.ExecuteAsync("CREATE TABLE public.victim (id int)");

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        var chain = new List<Exception>();
        for (var e = result.Error; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        Assert.Contains(chain, e => e is PostgresException { SqlState: "42704" });
        Assert.Equal(1L, await rig.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = 'victim'"));
    }

    private static NpgsqlDataSource Unused() => new NpgsqlDataSourceBuilder(TestDatabase.PlaceholderConnectionString).Build();

    // Breaks when a name that PostgreSQL would truncate (64 bytes) or cut at a NUL reaches SQL: it is rejected when the migrator
    // is built, before any command (nothing listens at the placeholder address, so any command would fail another way). 63
    // bytes are accepted, and the byte count is the UTF-8 one.
    [Theory]
    [InlineData(64, "a")]
    [InlineData(32, "é")]
    [InlineData(1, "ok\0name")]
    public void ARoleNameOfMoreThan63BytesOrWithANulIsRejectedBeforeAnySql(int repeat, string unit)
    {
        var role = unit.Length == 1 ? string.Concat(Enumerable.Repeat(unit, repeat)) : unit;
        var services = new ServiceCollection();
        services.AddSingleton(Unused());
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator(options => options.ApplicationRole = role);
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<ArgumentException>(() => provider.GetRequiredService<ITenantMigrator>());
    }

    [Fact]
    public void ARoleNameOf63BytesIsAccepted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Unused());
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator(options => options.ApplicationRole = new string('r', 63));
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ITenantMigrator>());
    }
}

// The cache of verdicts of the statement check, inspected through reflection (it is private).
public class VerdictCacheTests
{
    private static readonly Type Scanner = typeof(TenantSchemaName).Assembly.GetType(typeof(TenantSchemaName).Namespace + ".SessionStatements", throwOnError: true)!;

    private static readonly IDictionary Verdicts = (IDictionary)Scanner.GetField("Verdicts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    // Breaks when a text longer than 4 096 characters is kept (one large generated statement per request would grow memory), or
    // when it is not analysed at all.
    [Fact]
    public void ALongTextIsAnalysedButNotCached()
    {
        var text = "SELECT 1 /* " + new string('x', 5000) + Guid.NewGuid().ToString("N") + " */; RESET ALL";

        var verdict = SessionStatementsProbe.Refusal(text);

        Assert.NotNull(verdict);
        Assert.False(Verdicts.Contains(text));
        Assert.Equal(verdict, SessionStatementsProbe.Refusal(text));
    }

    // Breaks when the cache has no bound: after far more distinct short texts than its cap, it stops growing.
    [Fact]
    public void ManyDistinctShortTextsStopGrowingTheCacheAtItsCap()
    {
        var unique = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 6000; i++)
        {
            _ = SessionStatementsProbe.Refusal($"SELECT {i} AS c_{unique}");
        }

        var saturated = Verdicts.Count;
        for (var i = 6000; i < 8000; i++)
        {
            _ = SessionStatementsProbe.Refusal($"SELECT {i} AS c_{unique}");
        }

        Assert.True(saturated <= 4096 + 64, $"The cache holds {saturated} verdicts.");
        Assert.Equal(saturated, Verdicts.Count);
    }

    // Breaks when concurrent analyses of one text disagree (a verdict built from shared mutable state).
    [Fact]
    public async Task ConcurrentAnalysisOfTheSameTextGivesTheSameVerdict()
    {
        var unique = Guid.NewGuid().ToString("N");
        var blocked = $"SELECT set_config('search_path','x',false) AS c_{unique}";
        var allowed = $"SELECT set_config('app.x','1',false) AS c_{unique}";

        var verdicts = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(
            () => (Blocked: SessionStatementsProbe.Refusal(blocked), Allowed: SessionStatementsProbe.Refusal(allowed)),
            TestContext.Current.CancellationToken)));

        Assert.All(verdicts, v => Assert.NotNull(v.Blocked));
        Assert.All(verdicts, v => Assert.Null(v.Allowed));
        Assert.Single(verdicts.Select(v => v.Blocked).Distinct());
    }
}

// The limits the README documents, pinned: each test describes behaviour that is NOT a guarantee. If one fails, a limit was
// lifted: update the test and the README together.
[Collection(PostgresContainer.Name)]
public class PinnedLimitTests(PostgresFixture postgres)
{
    private const string OtherTenantSchema = "tenant_b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // PINNED LIMIT, not a guarantee: with one database role owning every schema, schema-qualified raw SQL reads another tenant.
    // The README lists it as the residual risk, "The guard prevents accidents through EF, not deliberate SQL".
    [Fact]
    public async Task PinnedLimitSchemaQualifiedRawSqlOfATenantContextReadsAnotherTenantsRows()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var (a, b) = (Guid.NewGuid(), new Guid("b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0"));
        Assert.Equal(OtherTenantSchema, TenantSchemaName.For(b));
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        await rig.ExecuteAsync($"INSERT INTO \"{OtherTenantSchema}\".access_items (id, name) VALUES (gen_random_uuid(), 'secret-of-b')");
        await using var scope = rig.ScopeFor(a);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var stolen = await context.Items.FromSqlRaw("SELECT * FROM \"tenant_b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0\".access_items").Select(i => i.Name).ToListAsync(Token);

        Assert.Equal(["secret-of-b"], stolen);
        Assert.Empty(await context.Items.Select(i => i.Name).ToListAsync(Token));
    }

    // The README states the limits the pinned tests above and in the statement tests rely on.
    [Fact]
    public void TheReadmeStatesTheLimitsOfTheStatementCheckAndTheResidualRisk()
    {
        var readme = File.ReadAllText(Path.Combine(SolutionLocator.SolutionRoot(), "README.md"));

        Assert.Contains("not a security boundary", readme, StringComparison.Ordinal);
        Assert.Contains("guard against mistakes", readme, StringComparison.Ordinal);
        Assert.Contains("cannot see SQL that runs inside the server", readme, StringComparison.Ordinal);
        Assert.Contains("`DO` blocks", readme, StringComparison.Ordinal);
        Assert.Contains("get no statement check", readme, StringComparison.Ordinal);
        Assert.Contains("Residual risk", readme, StringComparison.Ordinal);
        Assert.Contains("schema-qualified raw SQL reads another tenant", readme, StringComparison.Ordinal);
        Assert.Contains("deliberate SQL", readme, StringComparison.Ordinal);
    }
}
