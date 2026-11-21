using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.BuildingBlocks.Web;
using MyApp.Modules.Access.Endpoints;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Npgsql;
using Pinotechrd.Security.Enums;
using Pinotechrd.Security.Interfaces;
using Serilog.Events;

namespace MyApp.Api.Tests.Access;

// The scheduled refresh token purge on a real PostgreSQL database. A round is run on a service built from the host's own
// services (its scopes, its purger, its logging); the hosted instance itself stays off (ApiHostFactory turns it off in every
// test host) unless a test turns it on. Due rows are seeded and counted as the fixture's superuser, schema-qualified.
[Collection(PostgresContainer.Name)]
public class RefreshTokenPurgeServiceTests(PostgresFixture postgres)
{
    private const string Prefix = RefreshTokenPurgeOptions.SectionName + ":";

    private static readonly string Category = typeof(RefreshTokenPurgeService).FullName!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class World(ApiHostFactory factory, CollectingSink logs, Guid[] tenants) : IDisposable
    {
        public IServiceProvider Services => factory.Services;

        public CollectingSink Logs => logs;

        public Guid[] Tenants => tenants;

        public void Dispose() => factory.Dispose();
    }

    // A host on a new database with one tenant per status (schema, role and catalog row each), its logs captured.
    private async Task<World> StartAsync(TenantStatus[] statuses, Func<ApiHostFactory, ApiHostFactory>? configure = null)
    {
        var ids = statuses.Select(_ => Guid.NewGuid()).ToArray();
        var factory = AccessHost.For(await AccessDatabase.CreateAsync(postgres, ids));
        var logs = factory.CaptureLogs();
        factory = configure?.Invoke(factory) ?? factory;
        await AccessDatabase.SeedPlanAsync(factory.Services, ids);
        await AccessDatabase.SeedTenantRowsAsync(factory.Services, [.. ids.Select((id, i) => (id, "t" + i.ToString(CultureInfo.InvariantCulture), statuses[i]))]);
        return new World(factory, logs, ids);
    }

    private Task<World> StartActiveAsync(int count, Func<ApiHostFactory, ApiHostFactory>? configure = null) =>
        StartAsync([.. Enumerable.Repeat(TenantStatus.Active, count)], configure);

    private static RefreshTokenPurgeService NewService(IServiceProvider services, RefreshTokenPurgeOptions? options = null, TimeProvider? clock = null) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options ?? new RefreshTokenPurgeOptions()),
            services.GetRequiredService<ILogger<RefreshTokenPurgeService>>(), clock);

    private static async Task<object?> SqlAsync(IServiceProvider services, string sql, NpgsqlConnection? on = null)
    {
        await using var owned = on is null ? await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token) : null;
        await using var command = new NpgsqlCommand(sql, on ?? owned);
        return await command.ExecuteScalarAsync(Token);
    }

    private static string Schema(Guid tenant) => TenantSchemaName.For(tenant);

    // Families (one token each) that ended 30 days ago, far past the default grace; or a live one when live is true.
    private static async Task SeedAsync(IServiceProvider services, Guid tenant, int count, bool live = false) => await SqlAsync(services, $"""
        WITH f AS (
            INSERT INTO "{Schema(tenant)}".refresh_token_families (id, user_id, created_at, absolute_expires_at)
            SELECT gen_random_uuid(), gen_random_uuid(), now() - interval '60 days', now() {(live ? "+" : "-")} interval '30 days' FROM generate_series(1, {count})
            RETURNING id, user_id)
        INSERT INTO "{Schema(tenant)}".refresh_tokens (id, family_id, token_hash, user_id, created_at, expires_at)
        SELECT gen_random_uuid(), f.id, uuid_send(gen_random_uuid()), f.user_id, now() - interval '60 days', now() - interval '30 days' FROM f
        """);

    private static async Task<long> FamiliesAsync(IServiceProvider services, Guid tenant) =>
        (long)(await SqlAsync(services, $"SELECT count(*) FROM \"{Schema(tenant)}\".refresh_token_families"))!;

    private static IEnumerable<LogEvent> ServiceEvents(CollectingSink logs) => logs.Events.Where(e => e.IsFrom(Category));

    private static int Rounds(CollectingSink logs) => ServiceEvents(logs).Count(e => e.Properties.ContainsKey("Tenants"));

    private static List<LogEvent> LockWarnings(CollectingSink logs) => [.. ServiceEvents(logs).Where(e => e.Properties.ContainsKey("Rounds"))];

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var clock = Stopwatch.StartNew(); !condition(); await Task.Delay(20, Token))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "Timed out waiting for " + what + ".");
        }
    }

    // A fake clock that reports each timer the service creates for a wait (with its due time) and counts the timers that
    // fired, so a test advances only once the wait exists and sees synchronously whether an Advance ended it.
    private sealed class SignallingClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
    {
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public bool HasPendingTimer => _created.Reader.TryPeek(out _);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(s => { Interlocked.Increment(ref _fired); callback(s); }, state, dueTime, period);
            Assert.True(_created.Writer.TryWrite(dueTime));
            return timer;
        }

        public async Task<TimeSpan> NextTimerAsync() =>
            await _created.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
    }

    // A session of the superuser holding the advisory lock the purger takes for the tenant.
    private static async Task<NpgsqlConnection> HoldPurgeLockAsync(IServiceProvider services, Guid tenant)
    {
        var holder = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        await SqlAsync(services, $"SELECT pg_advisory_lock(hashtextextended('refresh-token-purge:{Schema(tenant)}', 0))", holder);
        return holder;
    }

    // ---- Rounds ----

    // Breaks when a round skips an Active or Suspended tenant, purges a Provisioning or Cancelled one, deletes a live
    // family, or miscounts the tenants or families.
    [Fact]
    public async Task ARoundPurgesActiveAndSuspendedTenantsOnlyAndCountsThem()
    {
        using var world = await StartAsync([TenantStatus.Active, TenantStatus.Suspended, TenantStatus.Provisioning, TenantStatus.Cancelled]);
        var (active, suspended, provisioning, cancelled) = (world.Tenants[0], world.Tenants[1], world.Tenants[2], world.Tenants[3]);
        await SeedAsync(world.Services, active, 3);
        await SeedAsync(world.Services, active, 1, live: true);
        await SeedAsync(world.Services, suspended, 2);
        await SeedAsync(world.Services, provisioning, 4);
        await SeedAsync(world.Services, cancelled, 5);

        var round = await NewService(world.Services).RunRoundAsync(Token);

        Assert.Equal(new RefreshTokenPurgeRound(2, 5, 0), round);
        Assert.Equal(1L, await FamiliesAsync(world.Services, active));
        Assert.Equal(0L, await FamiliesAsync(world.Services, suspended));
        Assert.Equal(4L, await FamiliesAsync(world.Services, provisioning));
        Assert.Equal(5L, await FamiliesAsync(world.Services, cancelled));
    }

    // Breaks when a catalog row with the empty id is visited (counted, failed, or logged as a warning or error) instead
    // of left out: no tenant can have that id, and setting it as the current tenant throws.
    [Fact]
    public async Task ACatalogRowWithTheEmptyIdIsNotVisitedAndLogsNoError()
    {
        using var world = await StartActiveAsync(1);
        await SeedAsync(world.Services, world.Tenants[0], 2);
        const string Tenants = "\"" + AccessDbContext.CatalogSchema + "\".tenants";
        var empty = Guid.Empty.ToString("D");
        await SqlAsync(world.Services, $"""
            CREATE TEMP TABLE zero AS SELECT * FROM {Tenants} LIMIT 1;
            UPDATE zero SET id = '{empty}', identifier = 'zero', name = 'Tenant zero', db_schema = 'tenant_zero', status = {(int)TenantStatus.Active};
            INSERT INTO {Tenants} SELECT * FROM zero;
            DROP TABLE zero;
            """);
        Assert.Equal(1L, await SqlAsync(world.Services, $"SELECT count(*) FROM {Tenants} WHERE id = '{empty}' AND status = {(int)TenantStatus.Active}"));

        var round = await NewService(world.Services).RunRoundAsync(Token);

        Assert.Equal(new RefreshTokenPurgeRound(1, 2, 0), round);
        Assert.Equal(0L, await FamiliesAsync(world.Services, world.Tenants[0]));
        Assert.DoesNotContain(ServiceEvents(world.Logs), e => e.Level >= LogEventLevel.Warning);
        Assert.DoesNotContain(world.Logs.Events, e => e.Level >= LogEventLevel.Error);
    }

    // Breaks when a tenant whose schema or role is missing ends the round or throws, is not counted as failed, is not
    // logged as a warning naming it, or when the log carries the exception (its message) instead of its type.
    [Theory]
    [InlineData("schema")]
    [InlineData("role")]
    public async Task ATenantWithoutItsSchemaOrRoleFailsAloneWithAWarningAndTheOthersArePurged(string missing)
    {
        using var world = await StartActiveAsync(3);
        foreach (var tenant in world.Tenants)
        {
            await SeedAsync(world.Services, tenant, 1);
        }

        var broken = world.Tenants[1];
        var dataSource = world.Services.GetRequiredService<NpgsqlDataSource>();
        var role = TenantRoles.For(dataSource.ConnectionString, broken);
        await SqlAsync(world.Services, missing == "schema"
            ? $"DROP SCHEMA \"{Schema(broken)}\" CASCADE"
            : $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");

        var round = await NewService(world.Services).RunRoundAsync(Token);

        Assert.Equal(new RefreshTokenPurgeRound(3, 2, 1), round);
        Assert.Equal(0L, await FamiliesAsync(world.Services, world.Tenants[0]));
        Assert.Equal(0L, await FamiliesAsync(world.Services, world.Tenants[2]));
        var warning = Assert.Single(ServiceEvents(world.Logs), e => e.Level == LogEventLevel.Warning);
        Assert.Equal(broken, warning.ScalarOf("TenantId"));
        Assert.False(string.IsNullOrEmpty(warning.ScalarOf("ExceptionType") as string));
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(ServiceEvents(world.Logs), e => e.Level >= LogEventLevel.Error);
    }

    // Breaks when an unreachable catalog makes the round throw or report counts, is not logged as a warning, or stops the
    // hosted service (or the host): the loop must go on and the host keep serving.
    [Fact]
    public async Task WithTheCatalogUnreachableARoundReturnsZerosWithAWarningAndTheHostedServiceKeepsRunning()
    {
        var missing = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { Database = "missing_" + Guid.NewGuid().ToString("N") };
        using var factory = AccessHost.For(missing.ConnectionString)
            .WithSetting(ApiHostFactory.PurgeEnabledKey, "true")
            .WithSetting(Prefix + "InitialDelay", "00:00:00")
            .WithSetting(Prefix + "Interval", "00:01:00");
        var logs = factory.CaptureLogs();
        using var client = factory.CreateClient();

        await WaitForAsync(() => ServiceEvents(logs).Any(e => e.Level == LogEventLevel.Warning), "the catalog warning");
        using var live = await client.GetAsync("/health/live", Token);
        var hosted = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<RefreshTokenPurgeService>());
        var round = await hosted.RunRoundAsync(Token);

        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);
        Assert.False(hosted.ExecuteTask!.IsCompleted);
        Assert.Equal(new RefreshTokenPurgeRound(0, 0, 0), round);
        Assert.Equal(2, ServiceEvents(logs).Count(e => e.Level == LogEventLevel.Warning && e.Properties.ContainsKey("SqlState") && !e.Properties.ContainsKey("TenantId")));
        Assert.DoesNotContain(ServiceEvents(logs), e => e.Level >= LogEventLevel.Error);
    }

    // Breaks when a held lock is never reported, reported before the 24th round in a row or more than once by then, does
    // not name the tenant, or when the count survives a round that purged (the next streak must start again at 1).
    [Fact]
    public async Task ALockHeldFor24RoundsInARowIsReportedOnceAtThe24thAndTheCountResetsOnceReleased()
    {
        using var world = await StartActiveAsync(2);
        var (locked, other) = (world.Tenants[0], world.Tenants[1]);
        await SeedAsync(world.Services, locked, 1);
        var service = NewService(world.Services);

        await using (await HoldPurgeLockAsync(world.Services, locked))
        {
            for (var i = 1; i < RefreshTokenPurgeService.SkippedRoundsWarning; i++)
            {
                Assert.Equal(new RefreshTokenPurgeRound(2, 0, 0), await service.RunRoundAsync(Token));
            }

            Assert.Empty(LockWarnings(world.Logs));
            await service.RunRoundAsync(Token);
        }

        var warning = Assert.Single(LockWarnings(world.Logs));
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Equal(locked, warning.ScalarOf("TenantId"));
        Assert.Equal(24, warning.ScalarOf("Rounds"));
        Assert.NotEqual(other, warning.ScalarOf("TenantId"));

        Assert.Equal(new RefreshTokenPurgeRound(2, 1, 0), await service.RunRoundAsync(Token));
        Assert.Equal(0L, await FamiliesAsync(world.Services, locked));

        await using (await HoldPurgeLockAsync(world.Services, locked))
        {
            for (var i = 0; i < RefreshTokenPurgeService.SkippedRoundsWarning; i++)
            {
                await service.RunRoundAsync(Token);
            }
        }

        Assert.Equal(2, LockWarnings(world.Logs).Count);
        Assert.Equal(24, LockWarnings(world.Logs)[1].ScalarOf("Rounds"));
    }

    // Breaks when the round's log line is missing, repeated or miscounts, or when any log of the round carries a token,
    // its hash, the user's email or the user's id.
    [Fact]
    public async Task ARoundLogsOneInformationLineWithItsCountsAndNoTokenHashEmailOrUserId()
    {
        using var world = await StartActiveAsync(2);
        var tenant = world.Tenants[0];
        var email = $"purge-{Guid.NewGuid():N}@example.test";
        var user = await AccessDatabase.AddUserAsync(world.Services, tenant, email);
        var token = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await AccessDatabase.InTenantAsync(world.Services, tenant, scope => scope.GetRequiredService<IRefreshTokenStore>()
            .SaveAsync(user.ToString("D"), tenant.ToString("D"), token, DateTime.UtcNow.AddDays(7), null, "stamp", Token));
        await SqlAsync(world.Services, $"UPDATE \"{Schema(tenant)}\".refresh_token_families SET absolute_expires_at = now() - interval '8 days'");
        var before = world.Logs.Events.Count;

        Assert.Equal(new RefreshTokenPurgeRound(2, 1, 0), await NewService(world.Services).RunRoundAsync(Token));

        var line = Assert.Single(ServiceEvents(world.Logs));
        Assert.Equal(LogEventLevel.Information, line.Level);
        Assert.Equal(2, line.ScalarOf("Tenants"));
        Assert.Equal(1, line.ScalarOf("Deleted"));
        Assert.Equal(0, line.ScalarOf("Failed"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        string[] forbidden = [token, Convert.ToHexString(hash), Convert.ToHexStringLower(hash), Convert.ToBase64String(hash), email, user.ToString("D"), user.ToString("N")];
        var texts = world.Logs.Events.Skip(before).SelectMany(e => e.TextsOf()).ToList();
        Assert.NotEmpty(texts);
        foreach (var secret in forbidden)
        {
            Assert.DoesNotContain(texts, text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- Schedule and lifetime ----

    // Breaks when a round runs before the initial delay, not after it, earlier than Interval*(1-Jitter) or not by
    // Interval*(1+Jitter); or when a stop during a wait is slow, faults the loop or logs an error.
    [Fact]
    public async Task RoundsFollowTheInitialDelayAndTheJitteredIntervalAndAStopDuringAWaitIsPrompt()
    {
        using var world = await StartActiveAsync(1);
        var logs = world.Logs;
        var clock = new SignallingClock();
        var options = new RefreshTokenPurgeOptions { Interval = TimeSpan.FromHours(1), Jitter = 0.10, InitialDelay = TimeSpan.FromMinutes(1) };
        var (earliest, latest) = (options.Interval * (1 - options.Jitter), options.Interval * (1 + options.Jitter));
        using var service = NewService(world.Services, options, clock);

        await service.StartAsync(Token);
        var initial = await clock.NextTimerAsync();
        Assert.InRange(initial, TimeSpan.Zero, options.InitialDelay);
        Assert.Equal(0, Rounds(logs));

        clock.Advance(options.InitialDelay);
        Assert.Equal(1, clock.Fired);
        var wait = await clock.NextTimerAsync(); // Created right after the first round.
        Assert.Equal(1, Rounds(logs));
        Assert.InRange(wait, earliest, latest);

        clock.Advance(earliest - TimeSpan.FromSeconds(1));
        Assert.Equal(1, clock.Fired);
        Assert.False(clock.HasPendingTimer);
        Assert.Equal(1, Rounds(logs));

        clock.Advance(latest - (earliest - TimeSpan.FromSeconds(1)));
        Assert.Equal(2, clock.Fired);
        await clock.NextTimerAsync(); // The wait after the second round: the loop is waiting again.
        Assert.Equal(2, Rounds(logs));

        var stopping = Stopwatch.StartNew();
        await service.StopAsync(Token);
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(2), $"The stop took {stopping.Elapsed}.");
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(2, Rounds(logs));
        Assert.DoesNotContain(logs.Events, e => e.Level >= LogEventLevel.Error);
    }

    // Breaks when a stop while a purge is blocked waits for the purge (its lock timeout is a minute here), faults the
    // loop, logs an error, or commits the batch it cancelled.
    [Fact]
    public async Task AStopDuringABlockedRoundCancelsItPromptlyWithoutAnError()
    {
        using var world = await StartActiveAsync(1, f => f.WithSetting(Prefix + "StatementTimeout", "00:01:00").WithSetting(Prefix + "LockTimeout", "00:01:00"));
        var logs = world.Logs;
        var tenant = world.Tenants[0];
        await SeedAsync(world.Services, tenant, 1);
        await using var holder = await world.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        await using var rowLock = await holder.BeginTransactionAsync(Token);
        await using (var take = new NpgsqlCommand($"SELECT id FROM \"{Schema(tenant)}\".refresh_tokens FOR UPDATE", holder, rowLock))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        using var service = NewService(world.Services, new RefreshTokenPurgeOptions { InitialDelay = TimeSpan.Zero }, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await service.StartAsync(Token);
        const string Waiting = "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE NOT l.granted AND a.datname = current_database()";
        for (var waited = Stopwatch.StartNew(); (long)(await SqlAsync(world.Services, Waiting))! == 0;)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15), "The purge never blocked on the token row.");
            await Task.Delay(25, Token);
        }

        var stopping = Stopwatch.StartNew();
        await service.StopAsync(Token);
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(5), $"The stop took {stopping.Elapsed}.");
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        await rowLock.CommitAsync(Token);
        Assert.Equal(1L, await FamiliesAsync(world.Services, tenant));
        Assert.Equal(0, Rounds(logs));
        Assert.DoesNotContain(logs.Events, e => e.Level >= LogEventLevel.Error);
    }

    // Breaks when a disabled service waits, runs a round, or keeps a loop alive.
    [Fact]
    public async Task WhenDisabledTheServiceReturnsAtOnceAndNeverRunsARound()
    {
        using var world = await StartActiveAsync(1);
        var logs = world.Logs;
        await SeedAsync(world.Services, world.Tenants[0], 1);
        var clock = new SignallingClock();
        using var service = NewService(world.Services, new RefreshTokenPurgeOptions { Enabled = false, InitialDelay = TimeSpan.Zero }, clock);

        await service.StartAsync(Token);
        // Since .NET 10 ExecuteAsync runs on its own task: "at once" is within a moment, with the clock not moved.
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        clock.Advance(TimeSpan.FromDays(2));
        await service.StopAsync(Token);

        // The loop ended without ever creating a wait, so nothing can run later.
        Assert.False(clock.HasPendingTimer);
        Assert.Equal(0, clock.Fired);

        Assert.Equal(0, Rounds(logs));
        Assert.Equal(1L, await FamiliesAsync(world.Services, world.Tenants[0]));
    }

    // Breaks when Enabled=false also skips the validation at start of the schedule settings.
    [Theory]
    [InlineData("Interval", "00:00:30")]
    [InlineData("Jitter", "NaN")]
    [InlineData("InitialDelay", "01:00:01")]
    public void WhenDisabledAnOutOfRangeScheduleSettingStillStopsTheHost(string property, string value)
    {
        using var factory = AccessHost.WithoutDatabase().WithSetting(ApiHostFactory.PurgeEnabledKey, "false").WithSetting(Prefix + property, value);

        StartupFailure.AssertFailsNaming(factory, Prefix + property);
    }

    // ---- Registration ----

    // Breaks when the API host does not run the service (once).
    [Fact]
    public void TheApiHostRegistersTheServiceAsAHostedService()
    {
        using var factory = AccessHost.WithoutDatabase();

        Assert.Single(factory.Services.GetServices<IHostedService>().OfType<RefreshTokenPurgeService>());
    }

    // Breaks when the contract generation (no configuration, no database) registers the service, or a normal start does
    // not. The flag is read from the entry assembly, which a test cannot be: the context is given it instead.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void TheModuleRegistersTheServiceExceptDuringContractGeneration(bool generating, int expected)
    {
        var services = new ServiceCollection();
        var context = new ModuleContext(services, new ConfigurationBuilder().Build(), new HostingEnvironment { EnvironmentName = Environments.Development }, generating);

        new AccessModule().AddServices(context);

        Assert.Equal(expected, services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RefreshTokenPurgeService)));
    }

    // ---- Validation of the schedule settings ----

    private static RefreshTokenPurgeOptions Schedule(string property, string value)
    {
        var options = new RefreshTokenPurgeOptions();
        switch (property)
        {
            case nameof(RefreshTokenPurgeOptions.Interval): options.Interval = TimeSpan.Parse(value, CultureInfo.InvariantCulture); break;
            case nameof(RefreshTokenPurgeOptions.Jitter): options.Jitter = double.Parse(value, CultureInfo.InvariantCulture); break;
            case nameof(RefreshTokenPurgeOptions.InitialDelay): options.InitialDelay = TimeSpan.Parse(value, CultureInfo.InvariantCulture); break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, null);
        }

        return options;
    }

    // Breaks when a schedule value outside its range (or NaN, or infinite) is accepted, or the failure does not name it.
    [Theory]
    [InlineData("Interval", "00:00:59.999")]
    [InlineData("Interval", "00:00:00")]
    [InlineData("Interval", "-01:00:00")]
    [InlineData("Interval", "1.00:00:00.001")]
    [InlineData("Jitter", "-0.001")]
    [InlineData("Jitter", "0.501")]
    [InlineData("Jitter", "NaN")]
    [InlineData("Jitter", "Infinity")]
    [InlineData("Jitter", "-Infinity")]
    [InlineData("InitialDelay", "-00:00:00.001")]
    [InlineData("InitialDelay", "01:00:00.001")]
    public void AScheduleValueOutsideItsRangeIsRefusedNamingTheSetting(string property, string value)
    {
        var failure = Assert.Single(new RefreshTokenPurgeOptionsValidator().Validate(null, Schedule(property, value)).Failures!);
        Assert.StartsWith(Prefix + property + " is ", failure, StringComparison.Ordinal);
    }

    // Breaks when a boundary of a schedule range is refused (every range is inclusive), or Enabled=false is refused.
    [Theory]
    [InlineData("Interval", "00:01:00")]
    [InlineData("Interval", "1.00:00:00")]
    [InlineData("Jitter", "0")]
    [InlineData("Jitter", "0.5")]
    [InlineData("InitialDelay", "00:00:00")]
    [InlineData("InitialDelay", "01:00:00")]
    public void AScheduleBoundaryIsAccepted(string property, string value)
    {
        var validator = new RefreshTokenPurgeOptionsValidator();

        Assert.True(validator.Validate(null, Schedule(property, value)).Succeeded);
        Assert.True(validator.Validate(null, new RefreshTokenPurgeOptions { Enabled = false }).Succeeded);
    }
}
