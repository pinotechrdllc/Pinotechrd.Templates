using System.Security.Cryptography;
using Npgsql;
using Pinotechrd.Security.Core;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>Q3: two truly concurrent RefreshAsync calls with the same token, through the library's auth manager.</summary>
public static class Q3Refresh
{
    private const int Pairs = 50;

    public static async Task<Outcome> RunAsync(NpgsqlDataSource shared, IReadOnlyDictionary<string, Guid> users)
    {
        var q = new Outcome("Q3", required: true);

        var atomic = await RaceAsync(shared, users, ds => new AtomicRefreshTokenStore(ds));
        var naive = await RaceAsync(shared, users, ds => new NaiveRefreshTokenStore(ds));
        var inMemory = await RaceAsync(shared, users, _ => new InMemoryRefreshTokenStore());
        q.Check(atomic.Both == 0 && atomic.One == Pairs,
            $"atomic store (validate = UPDATE ... RETURNING): of {Pairs} pairs, exactly one succeeded in {atomic.One}, both in {atomic.Both}, none in {atomic.None}");
        q.Fact($"naive store (validate = SELECT, then the library's RevokeAsync): both succeeded in {naive.Both} of {Pairs} pairs, one in {naive.One}, none in {naive.None}");
        q.Fact($"the library's default InMemoryRefreshTokenStore: both succeeded in {inMemory.Both} of {Pairs} pairs, one in {inMemory.One}, none in {inMemory.None}");
        q.Fact("race window in PinotechrdAuthManager.RefreshAsync: ValidateAsync, then UserManager.FindByIdAsync (a query), then RevokeAsync; " +
            "the contract has no single consume operation, so only a store whose ValidateAsync consumes closes it");

        // The token's tenant against the request's tenant.
        await using (var sp = Build(shared, ds => new AtomicRefreshTokenStore(ds)))
        {
            var store = sp.GetRequiredService<IRefreshTokenStore>();
            var token = NewToken();
            await store.SaveAsync(users["a"].ToString(), "a", token, DateTime.UtcNow.AddDays(1));
            await using var scope = sp.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant("b");
            var result = await scope.ServiceProvider.GetRequiredService<IPinotechrdAuthManager<AppUser>>().RefreshAsync(token);
            var again = await store.ValidateAsync(token);
            q.Fact($"token issued for tenant a, refreshed while the request's tenant is b: Succeeded={result.Succeeded} '{result.ErrorMessage}'; " +
                $"token still usable afterwards = {again.IsValid} (the manager never compares the token's tenant with the current one; the user is looked up in b)");

            var token2 = NewToken();
            await store.SaveAsync(users["a"].ToString(), "a", token2, DateTime.UtcNow.AddDays(1));
            await using var noTenant = sp.CreateAsyncScope();
            string observed;
            try
            {
                var r = await noTenant.ServiceProvider.GetRequiredService<IPinotechrdAuthManager<AppUser>>().RefreshAsync(token2);
                observed = $"Succeeded={r.Succeeded} '{r.ErrorMessage}'";
            }
            catch (Exception ex)
            {
                observed = $"{ex.GetType().Name}";
            }

            q.Fact($"refresh with no current tenant (the library's sample skips tenant resolution on its refresh path): {observed}; " +
                $"token still usable afterwards = {(await store.ValidateAsync(token2)).IsValid}");
        }

        q.Summary = q.Passed
            ? $"a database store whose ValidateAsync consumes the token atomically lets exactly one of two concurrent refreshes through ({atomic.One}/{Pairs}); " +
              $"a naive store lets both through in {naive.Both}/{Pairs}."
            : "the atomic store let both refreshes through at least once";
        return q;
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static ServiceProvider Build(NpgsqlDataSource shared, Func<NpgsqlDataSource, IRefreshTokenStore> store)
    {
        var services = new ServiceCollection().AddSpikeSecurity(shared);
        services.AddSingleton(_ => store(shared)); // after AddPinotechrdSecurity: replaces the in-memory default
        return services.BuildServiceProvider();
    }

    private static async Task<(int Both, int One, int None)> RaceAsync(
        NpgsqlDataSource shared, IReadOnlyDictionary<string, Guid> users, Func<NpgsqlDataSource, IRefreshTokenStore> factory)
    {
        await using var sp = Build(shared, factory);
        var store = sp.GetRequiredService<IRefreshTokenStore>();
        int both = 0, one = 0, none = 0;
        for (var i = 0; i < Pairs; i++)
        {
            var token = NewToken();
            await store.SaveAsync(users["b"].ToString(), "b", token, DateTime.UtcNow.AddDays(1));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new CountdownEvent(2);
            var first = Task.Run(() => RefreshAsync(sp, token, gate.Task, ready));
            var second = Task.Run(() => RefreshAsync(sp, token, gate.Task, ready));
            ready.Wait(TimeSpan.FromSeconds(10));
            gate.SetResult();
            var results = await Task.WhenAll(first, second);
            switch (results.Count(r => r))
            {
                case 2: both++; break;
                case 1: one++; break;
                default: none++; break;
            }
        }

        return (both, one, none);
    }

    private static async Task<bool> RefreshAsync(IServiceProvider sp, string token, Task gate, CountdownEvent ready)
    {
        await using var scope = sp.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant("b");
        var auth = scope.ServiceProvider.GetRequiredService<IPinotechrdAuthManager<AppUser>>();
        ready.Signal();
        await gate;
        var result = await auth.RefreshAsync(token);
        return result.Succeeded;
    }
}
