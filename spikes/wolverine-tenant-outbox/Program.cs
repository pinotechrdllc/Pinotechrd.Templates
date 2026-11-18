// Spike: Wolverine transactional outbox + EF Core 10 + one PostgreSQL database with one schema per tenant.
// Run from the repo root: dotnet run --project spikes/wolverine-tenant-outbox -c Release
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using WolverineTenantOutbox;

const string EnvelopeSchema = "wolverine";
var tenants = TenantSchemas.TenantIds;
var results = new List<(string Id, string Name, string Status, string Detail)>();

void Report(string id, string name, bool pass, string detail)
{
    results.Add((id, name, pass ? "PASS" : "FAIL", detail));
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {id} {name}: {detail}");
}

async Task Run(string id, string name, Func<Task<(bool, string)>> check)
{
    try
    {
        var (pass, detail) = await check();
        Report(id, name, pass, detail);
    }
    catch (Exception e)
    {
        Report(id, name, false, $"exception: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
    }
}

Console.WriteLine("Starting postgres:17 with Testcontainers...");
await using var pg = new PostgreSqlBuilder("postgres:17").Build();
await pg.StartAsync();
var cs = pg.GetConnectionString();

// ---------- Business schemas, created by the spike ----------
foreach (var t in tenants)
{
    await Exec(cs, $"""
        create schema {t};
        create table {t}.orders (id uuid primary key, tenant_id text not null, created_at timestamptz not null);
        create table {t}.order_audit (id uuid primary key, order_id uuid not null, tenant_id text not null,
                                      handler_schema text not null, handled_at timestamptz not null);
        """);
}

await using var schemas = new TenantSchemas(cs);
var host = await StartHost(cs, schemas);

var envelopeTables = await Strings(cs,
    $"select table_name from information_schema.tables where table_schema = '{EnvelopeSchema}' order by 1");
Console.WriteLine($"Wolverine tables in '{EnvelopeSchema}': {string.Join(", ", envelopeTables)}");

// ---------- a. rollback after publish ----------
await Run("a", "Atomicity on rollback", async () =>
{
    var before = await EnvelopeCount(cs);
    var orderId = Guid.NewGuid();
    long seenInsideTx = -1;
    try
    {
        await CreateOrder(host, "tenant_a", orderId, failFirst: false, async (db, _) =>
        {
            // Counted on the use case's own connection: proves the envelope row is written inside its transaction.
#pragma warning disable EF1003 // constant SQL built from fixed identifiers, no input
            seenInsideTx = await db.Database
                .SqlQueryRaw<long>("select " + EnvelopeSqlCountFor("wolverine") + " as \"Value\"").SingleAsync();
#pragma warning restore EF1003
            throw new InvalidOperationException("simulated business failure after publish");
        });
    }
    catch (InvalidOperationException) { /* expected: the use case failed and its transaction rolled back */ }

    await Task.Delay(TimeSpan.FromSeconds(3));
    var after = await EnvelopeCount(cs);
    var orders = await Long(cs, $"select count(*) from tenant_a.orders where id = '{orderId}'");
    var audits = await AuditCountAllSchemas(cs, orderId);
    var handled = Probe.Attempts.ContainsKey(orderId);
    var pass = seenInsideTx == before + 1 && after == before && orders == 0 && audits == 0 && !handled;
    return (pass, $"envelope written inside tx={seenInsideTx - before}, envelopes after rollback delta={after - before}, " +
                  $"order rows={orders}, audit rows={audits}, handler ran={handled}");
});

// ---------- b. commit -> stored and delivered exactly once ----------
await Run("b", "Atomicity on commit, exactly once", async () =>
{
    var before = await EnvelopeCount(cs);
    var orderId = Guid.NewGuid();
    long storedAfterCommit = -1;
    await CreateOrder(host, "tenant_b", orderId, failFirst: false, async (db, outbox) =>
    {
        await db.SaveChangesAsync();
        await db.Database.CommitTransactionAsync();
        storedAfterCommit = await EnvelopeCount(cs) - before; // seen from another connection, before flush
        await ((MessageContext)outbox).FlushOutgoingMessagesAsync();
    });

    var delivered = await WaitUntil(async () => await AuditCountAllSchemas(cs, orderId) > 0, TimeSpan.FromSeconds(30));
    await Task.Delay(TimeSpan.FromSeconds(3)); // give a duplicate the chance to show up
    var ownAudits = await Long(cs, $"select count(*) from tenant_b.order_audit where order_id = '{orderId}'");
    var allAudits = await AuditCountAllSchemas(cs, orderId);
    var attempts = Probe.Attempts.GetValueOrDefault(orderId);
    var pass = storedAfterCommit == 1 && delivered && ownAudits == 1 && allAudits == 1 && attempts == 1;
    return (pass, $"envelope stored after commit={storedAfterCommit}, delivered={delivered}, audit rows in tenant_b={ownAudits}, " +
                  $"in all schemas={allAudits}, handler invocations={attempts}");
});

// ---------- c. tenant isolation under concurrency ----------
await Run("c", "Tenant isolation (240 msgs, 3 tenants, concurrent)", async () =>
{
    var problemsBefore = Probe.Problems.Count;
    var work = Enumerable.Range(0, 240).Select(i => (Tenant: tenants[i % 3], OrderId: Guid.NewGuid())).ToList();
    var sw = Stopwatch.StartNew();
    await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 24 }, async (w, _) =>
        await CreateOrder(host, w.Tenant, w.OrderId, failFirst: false,
            (db, outbox) => outbox.SaveChangesAndFlushMessagesAsync())); // the documented one-call path
    var published = sw.Elapsed;

    var ids = work.Select(w => w.OrderId).ToHashSet();
    var done = await WaitUntil(async () => (await AuditedOrderIds(cs)).Values.Sum(s => s.Count(ids.Contains)) >= ids.Count,
        TimeSpan.FromSeconds(120));
    await Task.Delay(TimeSpan.FromSeconds(2));
    var handledIn = sw.Elapsed;

    var audited = await AuditedOrderIds(cs);
    var wrongSchema = work.Count(w => tenants.Any(t => t != w.Tenant && audited[t].Contains(w.OrderId)));
    var missing = work.Count(w => audited[w.Tenant].Count(x => x == w.OrderId) != 1);
    long orphanAudits = 0, wrongTenantColumn = 0, wrongHandlerSchema = 0;
    foreach (var t in tenants)
    {
        orphanAudits += await Long(cs, $"select count(*) from {t}.order_audit a where not exists (select 1 from {t}.orders o where o.id = a.order_id)");
        wrongTenantColumn += await Long(cs, $"select count(*) from {t}.order_audit where tenant_id <> '{t}'");
        wrongHandlerSchema += await Long(cs, $"select count(*) from {t}.order_audit where handler_schema <> '{t}'");
    }
    var publicTables = await Strings(cs, "select table_name from information_schema.tables where table_schema = 'public'");
    var problems = Probe.Problems.Count - problemsBefore;
    var pass = done && wrongSchema == 0 && missing == 0 && orphanAudits == 0 && wrongTenantColumn == 0 &&
               wrongHandlerSchema == 0 && publicTables.Count == 0 && problems == 0;
    return (pass, $"published 240 in {published.TotalSeconds:F1}s, all handled in {handledIn.TotalSeconds:F1}s={done}; " +
                  $"in wrong schema={wrongSchema}, missing or duplicated={missing}, audit without own order={orphanAudits}, " +
                  $"wrong tenant column={wrongTenantColumn}, wrong handler schema={wrongHandlerSchema}, " +
                  $"tables in public={publicTables.Count}, handler mismatches={problems}");
});

// ---------- e. retry ----------
await Run("e", "Retry keeps the tenant", async () =>
{
    var orderId = Guid.NewGuid();
    await CreateOrder(host, "tenant_c", orderId, failFirst: true, (db, outbox) => outbox.SaveChangesAndFlushMessagesAsync());
    var delivered = await WaitUntil(async () => await AuditCountAllSchemas(cs, orderId) > 0, TimeSpan.FromSeconds(30));
    await Task.Delay(TimeSpan.FromSeconds(2));
    var own = await Long(cs, $"select count(*) from tenant_c.order_audit where order_id = '{orderId}' and handler_schema = 'tenant_c'");
    var all = await AuditCountAllSchemas(cs, orderId);
    var attempts = Probe.Attempts.GetValueOrDefault(orderId);
    var pass = delivered && attempts == 2 && own == 1 && all == 1;
    return (pass, $"attempts={attempts} (first one threw), audit rows in tenant_c={own}, in all schemas={all}");
});

// ---------- f. how the tenant reaches the handler ----------
var incomingColumns = await Strings(cs,
    $"select column_name from information_schema.columns where table_schema = '{EnvelopeSchema}' and table_name = 'wolverine_incoming_envelopes' order by ordinal_position");
await Run("f", "Tenant reaches handler; DbContext resolved without handler code", () =>
{
    var pass = Probe.Invocations > 0 && Probe.Problems.IsEmpty;
    return Task.FromResult((pass,
        $"handler invocations={Probe.Invocations}, mismatches={Probe.Problems.Count}. Tenant travels as Envelope.TenantId " +
        $"(set with DeliveryOptions.TenantId, injected as JasperFx TenantId); DbContext built by IDbContextBuilder<OrdersDbContext> " +
        $"from MessageContext.TenantId; incoming table has tenant_id column={incomingColumns.Contains("tenant_id")}"));
});

// ---------- d. durability across a host restart ----------
await Run("d", "Durability across host restart", async () =>
{
    var orderId = Guid.NewGuid();
    long stored = -1;
    var before = await EnvelopeCount(cs);
    await CreateOrder(host, "tenant_a", orderId, failFirst: false, async (db, _) =>
    {
        await db.SaveChangesAsync();
        await db.Database.CommitTransactionAsync();
        stored = await EnvelopeCount(cs) - before;
        // No flush: simulates the process dying after commit, before the in-memory hand-off to the local queue.
    });
    await host.StopAsync();
    host.Dispose();
    var handledBeforeStop = Probe.Attempts.ContainsKey(orderId);

    var sw = Stopwatch.StartNew();
    host = await StartHost(cs, schemas);
    var delivered = await WaitUntil(async () => await AuditCountAllSchemas(cs, orderId) > 0, TimeSpan.FromSeconds(120));
    var recoveredIn = sw.Elapsed;
    await Task.Delay(TimeSpan.FromSeconds(2));
    var own = await Long(cs, $"select count(*) from tenant_a.order_audit where order_id = '{orderId}'");
    var all = await AuditCountAllSchemas(cs, orderId);
    var pass = stored == 1 && !handledBeforeStop && delivered && own == 1 && all == 1;
    return (pass, $"stored before stop={stored}, handled before stop={handledBeforeStop}, delivered by new host={delivered} " +
                  $"after {recoveredIn.TotalSeconds:F1}s, audit rows in tenant_a={own}, in all schemas={all}");
});

await host.StopAsync();
host.Dispose();

// ---------- Layout B probe: envelope tables per tenant schema ----------
var probeB = await ProbeLayoutB(cs);
results.Add(("B", "Layout B: envelope tables inside each tenant schema", "NOT RUN", probeB));
Console.WriteLine($"[NOT RUN] B Layout B: {probeB}");

// ---------- Summary ----------
Console.WriteLine();
Console.WriteLine("## Spike summary: Wolverine outbox with schema per tenant (layout A: shared `wolverine` schema)");
Console.WriteLine();
Console.WriteLine("| Check | Result | Detail |");
Console.WriteLine("|---|---|---|");
foreach (var r in results) Console.WriteLine($"| {r.Id}. {r.Name} | {r.Status} | {r.Detail.Replace("|", "/")} |");
var failed = results.Count(r => r.Status == "FAIL");
Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks that could be run passed." : $"{failed} check(s) failed.");
if (!Probe.Problems.IsEmpty) foreach (var p in Probe.Problems.Take(10)) Console.WriteLine($"  problem: {p}");
return failed == 0 ? 0 : 1;

// =====================================================================================================

static async Task<IHost> StartHost(string connectionString, TenantSchemas schemas)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddSingleton(schemas);
    builder.Services.AddSingleton<SchemaPerTenantDbContextBuilder>();
    builder.Services.AddSingleton<IDbContextBuilder<OrdersDbContext>>(s => s.GetRequiredService<SchemaPerTenantDbContextBuilder>());
    builder.Services.AddSingleton<IDbContextBuilder>(s => s.GetRequiredService<SchemaPerTenantDbContextBuilder>());

    builder.UseWolverine(opts =>
    {
        // Layout A: ONE message store, its envelope tables in the shared "wolverine" schema of the same database.
        opts.PersistMessagesWithPostgresql(connectionString, EnvelopeSchema);
        opts.UseEntityFrameworkCoreTransactions();
        opts.Policies.AutoApplyTransactions();
        opts.Policies.UseDurableLocalQueues();
        opts.OnException<FirstAttemptFailure>().RetryTimes(3);
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.AutoBuildMessageStorageOnStartup = JasperFx.AutoCreate.CreateOrUpdate;
    });

    var host = builder.Build();
    await host.StartAsync();
    return host;
}

// The use case "create order for tenant X": business insert in the tenant schema + OrderCreated through the outbox,
// in one transaction. `finish` decides how it ends (commit+flush, commit only, or throw -> rollback).
static async Task CreateOrder(IHost host, string tenant, Guid orderId, bool failFirst,
    Func<OrdersDbContext, IDbContextOutbox, Task> finish)
{
    using var scope = host.Services.CreateScope();
    await using var db = scope.ServiceProvider.GetRequiredService<SchemaPerTenantDbContextBuilder>().Create(tenant);
    var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
    outbox.Enroll(db);

    await db.Database.BeginTransactionAsync();
    db.Orders.Add(new Order { Id = orderId, TenantId = tenant, CreatedAt = DateTimeOffset.UtcNow });
    await outbox.PublishAsync(new OrderCreated(orderId, tenant, failFirst), new DeliveryOptions { TenantId = tenant });
    await db.SaveChangesAsync();
    await finish(db, outbox); // disposing an uncommitted transaction rolls it back
}

static async Task<string> ProbeLayoutB(string adminCs)
{
    // A separate database so the probe cannot disturb layout A. Tenants are registered through Wolverine's built-in
    // multi-tenancy, each with a connection string whose search_path is its own schema.
    await Exec(adminCs, "create database probe_b");
    var probeCs = new NpgsqlConnectionStringBuilder(adminCs) { Database = "probe_b" }.ConnectionString;
    foreach (var t in TenantSchemas.TenantIds) await Exec(probeCs, $"create schema {t}");

    string startup;
    try
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(probeCs, EnvelopeSchema)
                .RegisterStaticTenants(x =>
                {
                    foreach (var t in TenantSchemas.TenantIds)
                        x.Register(t, new NpgsqlConnectionStringBuilder(probeCs) { SearchPath = t }.ConnectionString);
                });
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.AutoBuildMessageStorageOnStartup = JasperFx.AutoCreate.CreateOrUpdate;
        });
        using var h = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await h.StartAsync(cts.Token);
        await h.StopAsync();
        startup = "host started";
    }
    catch (Exception e)
    {
        startup = $"host failed: {e.GetType().Name}: {e.Message.Split('\n')[0]}";
    }

    var where = await Strings(probeCs,
        "select table_schema || ':' || count(*) from information_schema.tables where table_name like 'wolverine%' group by table_schema order by 1");
    return $"Wolverine 6.64.2 gives every tenant store the same envelope schema name (one SchemaName per message store), " +
           $"so per-tenant-schema envelope tables are not configurable. Probe with RegisterStaticTenants + per-tenant search_path: " +
           $"{startup}; wolverine tables per schema = [{string.Join(", ", where)}]";
}

// ---------- tiny SQL helpers (spike-only; values are internal constants or Guids, never user input) ----------

static string EnvelopeSqlCountFor(string s) =>
    $"((select count(*) from {s}.wolverine_incoming_envelopes) + (select count(*) from {s}.wolverine_outgoing_envelopes) + (select count(*) from {s}.wolverine_dead_letters))";

static async Task Exec(string cs, string sql)
{
    await using var c = new NpgsqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<long> Long(string cs, string sql)
{
    await using var c = new NpgsqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

static async Task<List<string>> Strings(string cs, string sql)
{
    await using var c = new NpgsqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<string>();
    while (await r.ReadAsync()) list.Add(r.GetString(0));
    return list;
}

static Task<long> EnvelopeCount(string cs) => Long(cs, $"select {EnvelopeSqlCountFor("wolverine")}");

static async Task<long> AuditCountAllSchemas(string cs, Guid orderId)
{
    long total = 0;
    foreach (var t in TenantSchemas.TenantIds)
        total += await Long(cs, $"select count(*) from {t}.order_audit where order_id = '{orderId}'");
    return total;
}

static async Task<Dictionary<string, List<Guid>>> AuditedOrderIds(string cs)
{
    var map = new Dictionary<string, List<Guid>>();
    foreach (var t in TenantSchemas.TenantIds)
        map[t] = (await Strings(cs, $"select order_id::text from {t}.order_audit")).Select(Guid.Parse).ToList();
    return map;
}

static async Task<bool> WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        if (await condition()) return true;
        await Task.Delay(200);
    }
    return await condition();
}
