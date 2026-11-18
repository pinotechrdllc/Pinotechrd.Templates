using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pinotechrd.Security.Core;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>Q2: the library's services per tenant on one shared data source with the search_path interceptor.</summary>
public static class Q2Behaviour
{
    private const int Rounds = 20;

    public static async Task<Outcome> RunAsync(
        IServiceProvider services, NpgsqlDataSource shared, string connectionString, IReadOnlyDictionary<string, Guid> users)
    {
        var q = new Outcome("Q2", required: true);

        // Interleaved, sequential: a, b, a, b, ... Each call is its own scope, as a request would be.
        var errors = new ConcurrentBag<string>();
        for (var i = 0; i < Rounds * 2; i++)
        {
            await CheckOnceAsync(services, i % 2 == 0 ? "a" : "b", users, errors);
        }

        q.Check(errors.IsEmpty, $"{Rounds * 2} sequential alternating calls (a,b,a,b...) each saw only its own tenant" + First(errors));

        // Interleaved, parallel, on a pool of at most 4 physical connections: connections are reused across tenants.
        errors.Clear();
        await Task.WhenAll(Enumerable.Range(0, Rounds * 4).Select(i => CheckOnceAsync(services, i % 2 == 0 ? "a" : "b", users, errors)));
        q.Check(errors.IsEmpty, $"{Rounds * 4} parallel alternating calls on Max Pool Size=4 each saw only its own tenant" + First(errors));
        q.Fact("each call checked: current_schema(); UserManager.FindByName of the shared user name (own id); FindByName of a user " +
            "that exists only in a; SecurityEngine.CanAccessAsync for SALES.VIEW (role grant), SALES.EDIT (granted, denied per user in a only), " +
            "REPORTS.VIEW (granted, module not in plan); DbSecurityStore.GetActiveModuleCodesAsync (joins catalog plan modules to tenant modules); overrides count");

        // Case of the module code: the seeder and the entity factories upper-case codes; the module check compares in SQL.
        await using (var scope = Scope(services, "b", users))
        {
            var engine = scope.ServiceProvider.GetRequiredService<ISecurityEngine>();
            var upper = await engine.CanAccessAsync("SALES", "VIEW");
            var mixed = await engine.CanAccessAsync("Sales", "View");
            q.Fact($"CanAccessAsync(\"SALES\",\"VIEW\") = {upper}; CanAccessAsync(\"Sales\",\"View\") = {mixed} " +
                "(module code compared case-sensitively by PostgreSQL; a policy must be written in upper case)");
        }

        // Password sign-in with lockout counters, through the library's auth manager.
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 2; i++)
        {
            await LoginAsync(services, "a", Seed.LockoutUserName, "wrong-password");
        }

        foreach (var t in new[] { "a", "b" })
        {
            counts[t] = await FailedCountAsync(services, t, Seed.LockoutUserName);
        }

        q.Check(counts["a"] == 2 && counts["b"] == 0, $"two failed logins in a: access_failed_count a={counts["a"]}, b={counts["b"]}");
        for (var i = 0; i < 3; i++)
        {
            await LoginAsync(services, "a", Seed.LockoutUserName, "wrong-password");
        }

        var lockedA = await LoginAsync(services, "a", Seed.LockoutUserName, Seed.Password);
        var openB = await LoginAsync(services, "b", Seed.LockoutUserName, Seed.Password);
        q.Check(!lockedA.Succeeded && lockedA.ErrorMessage == "Account locked out" && openB.Succeeded,
            $"after five failures a answers '{lockedA.ErrorMessage}' with the right password; b signs in = {openB.Succeeded}");

        var loginA = await LoginAsync(services, "a", Seed.UserName, Seed.Password);
        var loginB = await LoginAsync(services, "b", Seed.UserName, Seed.Password);
        var permsA = string.Join(",", (loginA.Permissions ?? []).Order());
        var permsB = string.Join(",", (loginB.Permissions ?? []).Order());
        q.Check(loginA.UserId == users["a"].ToString() && permsA == "SALES.VIEW" && loginB.UserId == users["b"].ToString() && permsB == "SALES.EDIT,SALES.VIEW",
            $"login permissions: a=[{permsA}] (deny applied, REPORTS not in plan), b=[{permsB}]");

        // A row written for a is never visible to b.
        await using (var scope = Scope(services, "b", users))
        {
            var db = scope.ServiceProvider.GetRequiredService<PinotechrdTenantSecurityDbContext>();
            var onlyA = await db.Users.CountAsync(u => u.UserName == Seed.OnlyAUserName);
            var overrides = await db.UserModulePermissions.CountAsync();
            q.Check(onlyA == 0 && overrides == 0, $"from b: users named only-a = {onlyA}, per-user overrides = {overrides} (a has 1)");
        }

        // No current tenant: the open fails before any SQL.
        await using (var scope = services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            string observed;
            try
            {
                await userManager.FindByNameAsync(Seed.UserName);
                observed = "query ran";
            }
            catch (Exception ex)
            {
                observed = $"{ex.GetType().Name}: {ex.Message}";
            }

            q.Check(observed.Contains(TenantSearchPathInterceptor.NoTenantMessage, StringComparison.Ordinal),
                $"no tenant: UserManager.FindByNameAsync -> {observed}");
        }

        q.Check((await Q1Model.Tables(shared, "public")).Count == 0, "public still has no tables");

        // Reset on return to the pool: one physical connection, used by tenant a, then opened raw.
        var reset = await SearchPathAfterTenantUseAsync(connectionString, noResetOnClose: false);
        var noReset = await SearchPathAfterTenantUseAsync(connectionString, noResetOnClose: true);
        q.Check(reset == "\"$user\", public",
            $"same physical connection after a tenant-a query, opened raw: search_path = {reset} (Npgsql resets on close by default)");
        q.Fact($"same with No Reset On Close=true: search_path = {noReset} (state leaks; the interceptor still overwrites it on every EF open)");

        // The library's seeder on a real database (its own tests use the in-memory provider).
        await using (var scope = Scope(services, "a", users))
        {
            string seeded;
            try
            {
                await scope.ServiceProvider.GetRequiredService<SecuritySeeder>()
                    .SeedModuleAsync("Billing", "BILLING", null, 3, [("View", "BILLING.VIEW", null)]);
                seeded = "seeded";
            }
            catch (Exception ex)
            {
                seeded = $"{ex.GetType().Name}: {(ex.InnerException ?? ex).Message.Split('\n')[0]}";
            }

            q.Fact($"SecuritySeeder.SeedModuleAsync(\"Billing\", \"BILLING\", ..., [BILLING.VIEW]) in tenant a -> {seeded}");
        }

        var physical = await PhysicalConnectionsAsync(shared);
        q.Fact($"server connections from the shared data source at the end: {physical}");

        q.Summary = q.Passed
            ? "UserManager, lockout, DbSecurityStore (catalog x tenant join) and CanAccessAsync return only the current tenant's data over one " +
              "shared data source, sequential and parallel; no tenant -> the open fails; Npgsql's reset on close clears search_path."
            : "tenant data crossed or a check failed";
        return q;
    }

    private static string First(ConcurrentBag<string> errors) => errors.IsEmpty ? string.Empty : $"; first error: {errors.First()}";

    public static AsyncServiceScope Scope(IServiceProvider services, string tenant, IReadOnlyDictionary<string, Guid> users)
    {
        var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        scope.ServiceProvider.GetRequiredService<CurrentUser>().UserId = users[tenant].ToString();
        return scope;
    }

    private static async Task CheckOnceAsync(
        IServiceProvider services, string tenant, IReadOnlyDictionary<string, Guid> users, ConcurrentBag<string> errors)
    {
        try
        {
            await using var scope = Scope(services, tenant, users);
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<PinotechrdTenantSecurityDbContext>();
            var schema = await db.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync();
            Expect(schema == $"tenant_{tenant}", $"{tenant}: current_schema() = {schema}");

            var userManager = sp.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByNameAsync(Seed.UserName);
            Expect(user?.Id == users[tenant], $"{tenant}: FindByName returned {user?.Id}");
            var onlyA = await userManager.FindByNameAsync(Seed.OnlyAUserName);
            Expect((onlyA is not null) == (tenant == "a"), $"{tenant}: only-a visible = {onlyA is not null}");

            var engine = sp.GetRequiredService<ISecurityEngine>();
            Expect(await engine.CanAccessAsync("SALES", "VIEW"), $"{tenant}: SALES.VIEW denied");
            Expect(await engine.CanAccessAsync("SALES", "EDIT") == (tenant == "b"), $"{tenant}: SALES.EDIT wrong");
            Expect(!await engine.CanAccessAsync("REPORTS", "VIEW"), $"{tenant}: REPORTS.VIEW allowed though not in plan");

            var store = sp.GetRequiredService<DbSecurityStore>();
            var modules = string.Join(",", await store.GetActiveModuleCodesAsync(tenant));
            Expect(modules == "SALES", $"{tenant}: active modules = {modules}");
            var overrides = await store.GetUserOverridesAsync(users[tenant].ToString());
            Expect(overrides.Count == (tenant == "a" ? 1 : 0), $"{tenant}: overrides = {overrides.Count}");
        }
        catch (Exception ex)
        {
            errors.Add($"{tenant}: {ex.GetType().Name}: {ex.Message}");
        }

        void Expect(bool ok, string message)
        {
            if (!ok)
            {
                errors.Add(message);
            }
        }
    }

    private static async Task<Pinotechrd.Security.Models.AuthResult> LoginAsync(IServiceProvider services, string tenant, string user, string password)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        var auth = scope.ServiceProvider.GetRequiredService<IPinotechrdAuthManager<AppUser>>();
        return await auth.LoginAsync(user, password, tenant);
    }

    private static async Task<int> FailedCountAsync(IServiceProvider services, string tenant, string userName)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(userName);
        return user!.AccessFailedCount;
    }

    private static async Task<string> SearchPathAfterTenantUseAsync(string connectionString, bool noResetOnClose)
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 1, NoResetOnClose = noResetOnClose }.ConnectionString;
        await using var ds = NpgsqlDataSource.Create(cs);
        await using var sp = new ServiceCollection().AddSpikeSecurity(ds).BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant("a");
            await scope.ServiceProvider.GetRequiredService<PinotechrdTenantSecurityDbContext>().Users.CountAsync();
        }

        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SHOW search_path", conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> PhysicalConnectionsAsync(NpgsqlDataSource shared)
    {
        await using var cmd = shared.CreateCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND backend_type = 'client backend'");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
