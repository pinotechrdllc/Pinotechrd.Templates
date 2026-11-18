// Spike: one PostgreSQL schema per tenant, three modules per schema, each with its own
// DbContext, tables and migration history. Measures provisioning, upgrade and no-op passes,
// then verifies isolation. Throwaway measurement code.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using TenantModuleMigrations;
using TenantModuleMigrations.Modules.Access;
using TenantModuleMigrations.Modules.Billing;
using TenantModuleMigrations.Modules.Sales;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const int TenantCount = 50;

var startSw = Stopwatch.StartNew();
await using var pg = new PostgreSqlBuilder("postgres:17")
    .WithCommand("-c", "max_connections=400") // see the connection counts in the results
    .Build();
await pg.StartAsync();
var baseCs = pg.GetConnectionString();
await using var admin = NpgsqlDataSource.Create(baseCs);
Console.WriteLine($"PostgreSQL 17 container started in {startSw.Elapsed.TotalSeconds:F1} s");
Console.WriteLine($"Server: {await Scalar<string>("SHOW server_version")}, " +
    $"{Environment.ProcessorCount} logical CPUs, {Environment.OSVersion}, .NET {Environment.Version}");

Module[] modules =
[
    new("access", (s, m) => AccessDbContext.Create(baseCs, s, m), AccessDbContext.HistoryTable, ["access_users", "access_roles"]),
    new("sales", (s, m) => SalesDbContext.Create(baseCs, s, m), SalesDbContext.HistoryTable, ["sales_orders", "sales_customers"]),
    new("billing", (s, m) => BillingDbContext.Create(baseCs, s, m), BillingDbContext.HistoryTable, ["billing_invoices", "billing_payments"]),
];

string[] Tenants(string prefix) => Enumerable.Range(1, TenantCount).Select(i => $"t_{prefix}_{i:D2}").ToArray();
var seq = Tenants("seq");
var p4 = Tenants("p4");
var p8 = Tenants("p8");
var ic = Tenants("ic");
var allTenants = seq.Concat(p4).Concat(p8).Concat(ic).ToArray();

// Warm-up (JIT, EF model building, first physical connections), not measured.
foreach (var mode in Enum.GetValues<SchemaMode>())
{
    await MigrateTenant("t_warmup", mode, createSchema: true, firstOnly: false);
    await Exec("DROP SCHEMA t_warmup CASCADE");
}
NpgsqlConnection.ClearAllPools();

var rows = new List<Row>
{
    await Run("a. provision, migration 1", seq, 1, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, true, true)),
    await Run("b. provision, migration 1", p4, 4, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, true, true)),
    await Run("b. provision, migration 1", p8, 8, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, true, true)),
    await Run("a. provision, migration 1", ic, 1, SchemaMode.Interceptor, s => MigrateTenant(s, SchemaMode.Interceptor, true, true)),
    await Run("c. upgrade, migration 2", seq, 1, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, false, false)),
    await Run("c. upgrade, migration 2", p4, 4, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, false, false)),
    await Run("c. upgrade, migration 2", p8, 8, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, false, false)),
    await Run("c. upgrade, migration 2", ic, 1, SchemaMode.Interceptor, s => MigrateTenant(s, SchemaMode.Interceptor, false, false)),
    await Run("d. no-op, MigrateAsync", seq, 1, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, false, false)),
    await Run("d. no-op, MigrateAsync", p8, 8, SchemaMode.ConnectionString, s => MigrateTenant(s, SchemaMode.ConnectionString, false, false)),
    await Run("d. no-op, MigrateAsync", ic, 1, SchemaMode.Interceptor, s => MigrateTenant(s, SchemaMode.Interceptor, false, false)),
    await Run("d. no-op, pending check only", seq, 1, SchemaMode.ConnectionString, s => PendingOnly(s, SchemaMode.ConnectionString)),
    await Run("d. no-op, pending check only", p8, 8, SchemaMode.ConnectionString, s => PendingOnly(s, SchemaMode.ConnectionString)),
};

Console.WriteLine();
Console.WriteLine($"## Results ({TenantCount} tenants x 3 modules per row)");
Console.WriteLine();
Console.WriteLine("| Scenario | Schema selection | Parallelism | Total (s) | Per tenant, wall / N (ms) | Tenant mean (ms) | p50 | p95 | max | Server connections at end |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
foreach (var r in rows)
{
    Console.WriteLine($"| {r.Name} | {r.Mode} | {r.Dop} | {r.Total.TotalSeconds:F2} | {r.Total.TotalMilliseconds / TenantCount:F1} | " +
        $"{r.Times.Average():F1} | {Pct(r.Times, 50):F1} | {Pct(r.Times, 95):F1} | {r.Times.Max():F1} | {r.Connections} |");
}

// ---------------- Checks ----------------
var checks = new List<(string Name, bool Pass, string Detail)>();
async Task Check(string name, Func<Task<(bool, string)>> body)
{
    try
    {
        var (pass, detail) = await body();
        checks.Add((name, pass, detail));
    }
    catch (Exception ex)
    {
        checks.Add((name, false, $"{ex.GetType().Name}: {ex.Message}"));
    }
}

var expectedTables = modules.SelectMany(m => m.Tables.Append(m.HistoryTable)).Order().ToArray();
var expectedHistory = modules.ToDictionary(m => m.Name, m =>
{
    using var ctx = m.Create("t_any", SchemaMode.ConnectionString);
    return ctx.Database.GetMigrations().ToArray();
});

await Check($"Each of the {allTenants.Length} tenant schemas has exactly the 9 expected tables", async () =>
{
    var bad = new List<string>();
    foreach (var s in allTenants)
    {
        var tables = await List($"SELECT table_name FROM information_schema.tables WHERE table_schema = '{s}' ORDER BY table_name");
        if (!tables.Order().SequenceEqual(expectedTables)) bad.Add($"{s}: [{string.Join(",", tables)}]");
    }
    return (bad.Count == 0, bad.Count == 0 ? $"{expectedTables.Length} tables each" : string.Join("; ", bad.Take(3)));
});

await Check("Each tenant has 3 history tables with both migrations of its module, in order", async () =>
{
    var bad = new List<string>();
    foreach (var s in allTenants)
    foreach (var m in modules)
    {
        var ids = await List($"SELECT migration_id FROM \"{s}\".\"{m.HistoryTable}\" ORDER BY migration_id");
        if (!ids.SequenceEqual(expectedHistory[m.Name])) bad.Add($"{s}.{m.HistoryTable}: [{string.Join(",", ids)}]");
    }
    return (bad.Count == 0, bad.Count == 0 ? "2 rows per history table" : string.Join("; ", bad.Take(3)));
});

await Check("Migration 2 objects exist in every tenant (display_name, due_on, two indexes)", async () =>
{
    var cols = await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema LIKE 't\\_%' " +
        "AND ((table_name = 'access_users' AND column_name = 'display_name') OR (table_name = 'billing_invoices' AND column_name = 'due_on'))");
    var idx = await Scalar<long>("SELECT count(*) FROM pg_indexes WHERE schemaname LIKE 't\\_%' " +
        "AND indexname IN ('ix_sales_orders_number', 'ix_billing_payments_invoice_id')");
    var expected = allTenants.Length * 2;
    return (cols == expected && idx == expected, $"columns {cols}/{expected}, indexes {idx}/{expected}");
});

await Check("The public schema has none of the module or history tables", async () =>
{
    var tables = await List("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'");
    return (tables.Count == 0, tables.Count == 0 ? "public is empty" : string.Join(",", tables));
});

await Check("No schema other than the tenant schemas holds tables", async () =>
{
    var schemas = await List("SELECT DISTINCT table_schema FROM information_schema.tables " +
        "WHERE table_schema NOT IN ('pg_catalog', 'information_schema')");
    var extra = schemas.Except(allTenants).ToList();
    return (extra.Count == 0 && schemas.Count == allTenants.Length, $"{schemas.Count} schemas with tables, extra: [{string.Join(",", extra)}]");
});

await Check("Generated migration SQL has no schema qualifier (same script for every tenant)", async () =>
{
    var bad = new List<string>();
    foreach (var m in modules)
    {
        await using var ctx = m.Create("t_seq_01", SchemaMode.ConnectionString);
        var script = ctx.GetService<IMigrator>().GenerateScript();
        if (script.Contains("t_seq_01") || script.Contains("public") || script.Contains("design_time") || script.Contains($".{m.HistoryTable}") || script.Contains($".\"{m.HistoryTable}\""))
            bad.Add(m.Name);
    }
    return (bad.Count == 0, bad.Count == 0 ? "3 scripts checked" : $"qualified: {string.Join(",", bad)}");
});

foreach (var mode in Enum.GetValues<SchemaMode>())
{
    var (a, b) = mode == SchemaMode.ConnectionString ? (seq[0], seq[1]) : (ic[0], ic[1]);
    await Check($"[{mode}] Row inserted through tenant A is not visible through tenant B", async () =>
    {
        await using (var ctxA = AccessDbContext.Create(baseCs, a, mode))
        {
            ctxA.Users.Add(new User { Email = $"only-in-{a}@example.test" });
            await ctxA.SaveChangesAsync();
        }
        await using var readA = AccessDbContext.Create(baseCs, a, mode);
        await using var readB = AccessDbContext.Create(baseCs, b, mode);
        var inA = await readA.Users.CountAsync(u => u.Email == $"only-in-{a}@example.test");
        var inB = await readB.Users.CountAsync(u => u.Email == $"only-in-{a}@example.test");
        var direct = await Scalar<long>($"SELECT count(*) FROM \"{a}\".access_users WHERE email = 'only-in-{a}@example.test'");
        return (inA == 1 && inB == 0 && direct == 1, $"visible in A: {inA}, in B: {inB}, rows in {a} table: {direct}");
    });

    await Check($"[{mode}] Alternating contexts for two tenants in one process never cross schemas", async () =>
    {
        var problems = new List<string>();
        var sameModel = true;
        for (var i = 0; i < 10; i++)
        foreach (var s in new[] { a, b })
        {
            await using var ctx = SalesDbContext.Create(baseCs, s, mode);
            ctx.Customers.Add(new Customer { Name = s });
            await ctx.SaveChangesAsync();
            var current = await ctx.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync();
            var names = await ctx.Customers.Select(c => c.Name).Distinct().ToListAsync();
            if (current != s || names.Count != 1 || names[0] != s) problems.Add($"{s}: current_schema={current}, names=[{string.Join(",", names)}]");
            await using var other = SalesDbContext.Create(baseCs, s == a ? b : a, mode);
            sameModel &= ReferenceEquals(ctx.Model, other.Model);
        }
        return (problems.Count == 0, problems.Count == 0
            ? $"20 alternating contexts, each saw only its own rows; one shared cached model: {sameModel}"
            : string.Join("; ", problems.Take(3)));
    });
}

await Check("Migrating a tenant whose schema does not exist fails and writes nothing to public", async () =>
{
    string outcome;
    try
    {
        await MigrateTenant("t_missing", SchemaMode.ConnectionString, createSchema: false, firstOnly: false);
        outcome = "migrate succeeded (unexpected)";
    }
    catch (Exception ex)
    {
        outcome = $"failed as expected: {(ex.InnerException ?? ex).Message.Split('\n')[0]}";
    }
    var publicTables = await Scalar<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'");
    return (outcome.StartsWith("failed") && publicTables == 0, $"{outcome}; public tables: {publicTables}");
});

// Npgsql's history repository returns Exists() == true always (by design, because of search_path) and
// relies on catching 42P01 instead. So an empty tenant must report zero applied migrations, not borrow another tenant's.
await Check("An empty tenant schema reports 0 applied migrations while 200 other schemas have history tables", async () =>
{
    await Exec("CREATE SCHEMA t_empty");
    await using var ctx = AccessDbContext.Create(baseCs, "t_empty", SchemaMode.ConnectionString);
    var exists = await ctx.GetService<IHistoryRepository>().ExistsAsync();
    var applied = (await ctx.Database.GetAppliedMigrationsAsync()).Count();
    var pending = (await ctx.Database.GetPendingMigrationsAsync()).Count();
    await Exec("DROP SCHEMA t_empty");
    return (applied == 0 && pending == 2, $"applied: {applied}, pending: {pending}; IHistoryRepository.ExistsAsync() returned {exists}");
});

Console.WriteLine();
Console.WriteLine("## Checks");
Console.WriteLine();
foreach (var c in checks) Console.WriteLine($"- {(c.Pass ? "PASS" : "FAIL")}: {c.Name} ({c.Detail})");
var allPass = checks.All(c => c.Pass);
Console.WriteLine();
Console.WriteLine(allPass ? "All checks passed." : "Some checks FAILED.");
return allPass ? 0 : 1;

// ---------------- Helpers ----------------
async Task MigrateTenant(string schema, SchemaMode mode, bool createSchema, bool firstOnly)
{
    if (createSchema) await Exec($"CREATE SCHEMA IF NOT EXISTS \"{Tenancy.Validate(schema)}\"");
    foreach (var m in modules)
    {
        await using var ctx = m.Create(schema, mode);
        var target = firstOnly ? ctx.Database.GetMigrations().First() : null;
        await ctx.GetService<IMigrator>().MigrateAsync(target);
    }
}

async Task PendingOnly(string schema, SchemaMode mode)
{
    foreach (var m in modules)
    {
        await using var ctx = m.Create(schema, mode);
        if ((await ctx.Database.GetPendingMigrationsAsync()).Any()) throw new InvalidOperationException($"{schema}.{m.Name} has pending migrations");
    }
}

async Task<Row> Run(string name, string[] schemas, int dop, SchemaMode mode, Func<string, Task> work)
{
    var times = new ConcurrentBag<double>();
    var sw = Stopwatch.StartNew();
    await Parallel.ForEachAsync(schemas, new ParallelOptions { MaxDegreeOfParallelism = dop }, async (s, _) =>
    {
        var t = Stopwatch.StartNew();
        await work(s);
        times.Add(t.Elapsed.TotalMilliseconds);
    });
    sw.Stop();
    var connections = await Scalar<long>("SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend'");
    NpgsqlConnection.ClearAllPools(); // each scenario starts cold, like a fresh migrator process
    Console.WriteLine($"{name} [{mode}, x{dop}]: {sw.Elapsed.TotalSeconds:F2} s");
    return new Row(name, mode, dop, sw.Elapsed, times.ToArray(), connections);
}

static double Pct(double[] values, int p)
{
    var sorted = values.Order().ToArray();
    return sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
}

async Task Exec(string sql)
{
    await using var cmd = admin.CreateCommand(sql);
    await cmd.ExecuteNonQueryAsync();
}

async Task<T> Scalar<T>(string sql)
{
    await using var cmd = admin.CreateCommand(sql);
    return (T)(await cmd.ExecuteScalarAsync())!;
}

async Task<List<string>> List(string sql)
{
    var result = new List<string>();
    await using var cmd = admin.CreateCommand(sql);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) result.Add(reader.GetString(0));
    return result;
}

record Module(string Name, Func<string, SchemaMode, DbContext> Create, string HistoryTable, string[] Tables);

record Row(string Name, SchemaMode Mode, int Dop, TimeSpan Total, double[] Times, long Connections);
