using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Npgsql;
using Pinotechrd.Security.Interfaces;
using Serilog.Events;

namespace MyApp.Api.Tests.Access.Auth;

// A client that aborts its request must not stop what protects the account: the revocation of a reused token's family, the
// count of a wrong current password, the end of a session on logout. Each test holds the row the work must write with
// FOR UPDATE on another connection, waits until the request blocks on it, aborts the request, releases the row and polls the
// database until the server side finished.
[Collection(PostgresContainer.Name)]
public class AbortSafetyTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Schema(Guid tenant) => "\"" + TenantSchemaName.For(tenant) + "\"";

#pragma warning disable CA2100 // The SQL is a constant of the tests; the only interpolated part is a schema name made from a GUID.
    private static async Task<long> ScalarAsync(AccessWorld world, string sql, object? parameter = null)
    {
        await using var connection = await world.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameter is not null)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Opens a transaction that holds the rows with FOR UPDATE; disposing it rolls back and releases them.
    private static async Task<Held> LockAsync(AccessWorld world, string sql, Guid id)
    {
        var connection = await world.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { Parameters = { new NpgsqlParameter { Value = id } } };
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) >= 1, "Nothing to lock.");
        return new Held(connection, transaction);
    }

    private sealed class Held(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync(Ct);
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
#pragma warning restore CA2100

    private static Task<Held> LockFamiliesAsync(AccessWorld world, Guid user) =>
        LockAsync(world, $"SELECT count(*) FROM (SELECT 1 FROM {Schema(world.TenantA)}.refresh_token_families WHERE user_id = $1 FOR UPDATE) AS l", user);

    private static Task<long> RevokedFamiliesAsync(AccessWorld world, Guid user) =>
        ScalarAsync(world, $"SELECT count(*) FROM {Schema(world.TenantA)}.refresh_token_families WHERE user_id = $1 AND revoked_at IS NOT NULL", user);

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting until {what}.");
            await Task.Delay(50, Ct);
        }
    }

    // Sends the request while the row is held, waits until a backend of the database waits on a row lock (the request
    // reached the held row), then aborts it the way a client does: by cancelling. The test host answers the client only
    // once the server side ends, so the abort is awaited after the row is released (AbortedAsync).
    private static async Task<Task<HttpResponseMessage>> SendAndAbortAsync(
        AccessWorld world, HttpClient client, HttpRequestMessage request, CancellationTokenSource abort)
    {
        var sending = client.SendAsync(request, abort.Token);
        await UntilAsync(
            async () => sending.IsCompleted || await ScalarAsync(world, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") > 0,
            "the request blocks on the held row");
        Assert.False(sending.IsCompleted, "The request ended before it reached the held row.");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await abort.CancelAsync();
        return sending;
    }

    private static async Task AbortedAsync(Task<HttpResponseMessage> sending) => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

    private static HttpRequestMessage Post(string path, string? cookie = null, string? bearer = null, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Tenant", AccessWorld.SlugA);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{AuthRequests.CookieName}={cookie}");
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        request.Content = body is null ? null : JsonContent.Create(body);
        return request;
    }

    // Breaks when an aborted replay of a spent refresh token no longer revokes its family (the attacker keeps the rotated
    // session alive by cancelling), or when the library no longer logs the reuse (event 4204) for it.
    [Fact]
    public async Task AnAbortedReplayOfASpentTokenStillRevokesTheFamilyAndIsLogged()
    {
        var sink = default(CollectingSink)!;
        using var world = await AccessWorld.StartAsync(postgres, f =>
        {
            sink = f.CaptureLogs();
            return f;
        });
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, spent) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        string live;
        using (var rotated = await client.RefreshAsync(AccessWorld.SlugA, spent))
        {
            live = rotated.RefreshTokenOf();
        }

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<HttpResponseMessage> sending;
        await using (await LockFamiliesAsync(world, user))
        {
            using var replay = Post(AuthRequests.Refresh, cookie: spent);
            sending = await SendAndAbortAsync(world, client, replay, abort);
        }

        await AbortedAsync(sending);

        await UntilAsync(async () => await RevokedFamiliesAsync(world, user) == 1, "the family is revoked");
        await UntilAsync(() => Task.FromResult(sink.Events.Any(e => SecurityUpgradeTests.EventIdOf(e) == 4204)), "event 4204 is logged");
        Assert.Contains(sink.Events, e => SecurityUpgradeTests.EventIdOf(e) == 4204 && e.Level == LogEventLevel.Warning);
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, live);
        await AuthScenarios.AssertRefreshRefusedAsync(refresh);
    }

    // Breaks when an aborted change-password with a wrong current password is not counted as a failed attempt (a stolen
    // access token could guess the password without ever locking the account).
    [Fact]
    public async Task AnAbortedWrongCurrentPasswordIsStillCounted()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (access, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        var before = (await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user)).AccessFailedCount;

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<HttpResponseMessage> sending;
        await using (await LockAsync(world, $"SELECT count(*) FROM (SELECT 1 FROM {Schema(world.TenantA)}.users WHERE id = $1 FOR UPDATE) AS l", user))
        {
            using var change = Post(AuthRequests.ChangePassword, bearer: access, body: new { currentPassword = "Wr0ng-guess-for-test", newPassword = "N3w-passw0rd-for-tests" });
            sending = await SendAndAbortAsync(world, client, change, abort);
        }

        await AbortedAsync(sending);

        await UntilAsync(
            async () => (await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user)).AccessFailedCount == before + 1,
            "the failed attempt is counted");
    }

    // Breaks when an aborted logout leaves the session alive: its family is not revoked and the cookie still refreshes.
    [Fact]
    public async Task AnAbortedLogoutStillEndsTheSession()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<HttpResponseMessage> sending;
        await using (await LockFamiliesAsync(world, user))
        {
            using var logout = Post(AuthRequests.Logout, cookie: cookie);
            sending = await SendAndAbortAsync(world, client, logout, abort);
        }

        await AbortedAsync(sending);

        await UntilAsync(async () => await RevokedFamiliesAsync(world, user) == 1, "the family is revoked");
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        await AuthScenarios.AssertRefreshRefusedAsync(refresh);
    }

    // Breaks when the store no longer honours a token cancelled before the call (it starts work the caller gave up on), or
    // when a cancelled call changes something anyway: the token is consumed or its family revoked.
    [Fact]
    public async Task ACallCancelledBeforeItStartsThrowsAndChangesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        // The cookie is URL-escaped; the store takes the token itself.
        var token = Uri.UnescapeDataString(cookie);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
        {
            var store = scope.GetRequiredService<IRefreshTokenStore>();
            Assert.IsType<DatabaseRefreshTokenStore>(store);
            // The value targets a real stored token: otherwise "changes nothing" would hold even if cancellation were ignored.
            Assert.True((await store.ValidateAsync(token, Ct)).IsValid, "The store does not find the signed-in token.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConsumeAsync(token, world.TenantA.ToString("D"), cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RevokeAsync(token, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RevokeAllForUserAsync(user.ToString("D"), cancelled.Token));
            Assert.True((await store.ValidateAsync(token, Ct)).IsValid, "A cancelled call changed the token.");
        });

        Assert.Equal(1, await ScalarAsync(world, $"SELECT count(*) FROM {Schema(world.TenantA)}.refresh_tokens WHERE token_hash = sha256(convert_to($1, 'UTF8')) AND consumed_at IS NULL AND revoked_at IS NULL", token));
        Assert.Equal(0, await RevokedFamiliesAsync(world, user));
        Assert.Equal(0, await ScalarAsync(world, $"SELECT count(*) FROM {Schema(world.TenantA)}.refresh_tokens WHERE user_id = $1 AND (consumed_at IS NOT NULL OR revoked_at IS NOT NULL)", user));
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // Breaks when a password change whose session revocation is not confirmed (the security manager answers false) fails the
    // request, logs that every session ended, or logs no warning.
    [Fact]
    public async Task AnUnconfirmedRevocationAfterAPasswordChangeIsAWarning()
    {
        var sink = default(CollectingSink)!;
        using var world = await AccessWorld.StartAsync(postgres, f =>
        {
            sink = f.CaptureLogs();
            return f.WithServices(UnconfirmedRevocation.Decorate);
        });
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (access, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var change = await client.ChangePasswordAsync(access, AccessDatabase.DefaultPassword, "N3w-passw0rd-for-tests");

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        var own = sink.Events.Where(e => e.IsFrom(typeof(AccessAuthentication).FullName!)).ToArray();
        var warning = Assert.Single(own, e => e.Level == LogEventLevel.Warning);
        Assert.Equal(
            $"Password changed for user {user:D} in tenant {world.TenantA:D}, but ending every session was not confirmed.",
            warning.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Replace("\"", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(own, e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("every session ended", StringComparison.Ordinal));
    }

    /// <summary>A security manager that forwards everything to the real one but answers false to RevokeUserSessionsAsync, without revoking.</summary>
    public class UnconfirmedRevocation : DispatchProxy
    {
        private object _inner = default!;

        internal static void Decorate(IServiceCollection services)
        {
            var service = typeof(IPinotechrdSecurityManager<AccessUser, AccessRole>);
            var descriptor = services.Last(d => d.ServiceType == service
                || (d.ServiceType.IsGenericTypeDefinition && d.ServiceType == service.GetGenericTypeDefinition()));
            Func<IServiceProvider, object> inner = descriptor switch
            {
                { ImplementationFactory: { } factory } => factory,
                { ImplementationType: { IsGenericTypeDefinition: true } open } =>
                    sp => ActivatorUtilities.CreateInstance(sp, open.MakeGenericType(service.GenericTypeArguments)),
                { ImplementationType: { } type } => sp => ActivatorUtilities.CreateInstance(sp, type),
                _ => throw new InvalidOperationException("The security manager is registered in a way the test does not decorate."),
            };
            services.Add(ServiceDescriptor.Describe(service, sp => Create(inner(sp)), descriptor.Lifetime));
        }

        private static IPinotechrdSecurityManager<AccessUser, AccessRole> Create(object inner)
        {
            var proxy = Create<IPinotechrdSecurityManager<AccessUser, AccessRole>, UnconfirmedRevocation>();
            ((UnconfirmedRevocation)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "RevokeUserSessionsAsync")
            {
                return Task.FromResult(false);
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }
    }
}
