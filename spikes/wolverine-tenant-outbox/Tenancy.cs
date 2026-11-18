using System.Collections.Concurrent;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Runtime;

namespace WolverineTenantOutbox;

// ---------- Business model (identical EF model for every tenant; the schema comes from the connection) ----------

public class Order
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class OrderAudit
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string TenantId { get; set; } = "";
    public string HandlerSchema { get; set; } = "";
    public DateTimeOffset HandledAt { get; set; }
}

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderAudit> Audits => Set<OrderAudit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // No schema here on purpose: tables resolve through the tenant connection's search_path.
        // Wolverine's envelope tables are mapped (by WolverineModelCustomizer) with an explicit "wolverine" schema.
        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        b.Entity<OrderAudit>(e =>
        {
            e.ToTable("order_audit");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.HandlerSchema).HasColumnName("handler_schema");
            e.Property(x => x.HandledAt).HasColumnName("handled_at");
        });
    }
}

// ---------- Messages and handler ----------

/// <param name="ExpectedTenant">Test oracle only. The handler takes its tenant from the envelope, not from here.</param>
public record OrderCreated(Guid OrderId, string ExpectedTenant, bool FailFirstAttempt);

public class FirstAttemptFailure() : Exception("Simulated transient failure on first attempt");

public static class OrderCreatedHandler
{
    // The handler never builds its DbContext: Wolverine's EF Core middleware asks IDbContextBuilder<OrdersDbContext>
    // for one, passing the MessageContext whose TenantId came from the envelope, and wraps it in a transaction.
    public static async Task Handle(OrderCreated message, TenantId tenantId, OrdersDbContext db, CancellationToken ct)
    {
        var schema = await db.Database.SqlQuery<string>($"select current_schema() as \"Value\"").SingleAsync(ct);
        var attempt = Probe.Record(message, tenantId.Value, schema);

        if (message.FailFirstAttempt && attempt == 1) throw new FirstAttemptFailure();

        db.Audits.Add(new OrderAudit
        {
            Id = Guid.NewGuid(),
            OrderId = message.OrderId,
            TenantId = tenantId.Value,
            HandlerSchema = schema,
            HandledAt = DateTimeOffset.UtcNow
        });
        // SaveChanges + commit are added by Wolverine's transactional middleware.
    }
}

/// <summary>In-process observations made inside the handler.</summary>
public static class Probe
{
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();
    public static readonly ConcurrentBag<string> Problems = new();
    public static int Invocations;

    public static int Record(OrderCreated m, string? envelopeTenant, string schema)
    {
        Interlocked.Increment(ref Invocations);
        if (envelopeTenant != m.ExpectedTenant)
            Problems.Add($"order {m.OrderId}: envelope tenant '{envelopeTenant}' != expected '{m.ExpectedTenant}'");
        if (schema != m.ExpectedTenant)
            Problems.Add($"order {m.OrderId}: DbContext schema '{schema}' != expected '{m.ExpectedTenant}'");
        return Attempts.AddOrUpdate(m.OrderId, 1, (_, n) => n + 1);
    }
}

// ---------- Tenant -> schema plumbing ----------

/// <summary>One NpgsqlDataSource per tenant, each pinned to the tenant schema through search_path.</summary>
public sealed class TenantSchemas : IAsyncDisposable
{
    public static readonly string[] TenantIds = ["tenant_a", "tenant_b", "tenant_c"];
    private const string NoTenantSchema = "no_tenant_selected"; // never created: any write without a tenant fails loudly

    private readonly Dictionary<string, NpgsqlDataSource> _sources;
    public NpgsqlDataSource NoTenant { get; }

    public TenantSchemas(string connectionString)
    {
        // Tenant id == schema name, taken only from this fixed allow-list (never from user input).
        _sources = TenantIds.ToDictionary(t => t, t => Build(connectionString, t));
        NoTenant = Build(connectionString, NoTenantSchema);
    }

    private static NpgsqlDataSource Build(string cs, string schema) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema }.ConnectionString);

    public NpgsqlDataSource For(string? tenantId) =>
        tenantId != null && _sources.TryGetValue(tenantId, out var ds)
            ? ds
            : throw new ArgumentException($"Unknown tenant '{tenantId}'");

    public async ValueTask DisposeAsync()
    {
        foreach (var ds in _sources.Values) await ds.DisposeAsync();
        await NoTenant.DisposeAsync();
    }
}

/// <summary>
/// Plugs schema-per-tenant into Wolverine's EF Core codegen. Wolverine treats a DbContext as "multi-tenanted" when an
/// IDbContextBuilder&lt;T&gt; is registered, and then builds it per message through BuildAndEnrollAsync.
/// NOTE: IDbContextBuilder lives in Wolverine.EntityFrameworkCore.Internals (public, but not a documented extension point).
/// </summary>
public sealed class SchemaPerTenantDbContextBuilder(IServiceProvider services, TenantSchemas schemas)
    : IDbContextBuilder<OrdersDbContext>
{
    public Type DbContextType => typeof(OrdersDbContext);
    public DatabaseCardinality Cardinality => DatabaseCardinality.Single;

    private DbContextOptions<OrdersDbContext> OptionsFor(NpgsqlDataSource dataSource)
    {
        var b = new DbContextOptionsBuilder<OrdersDbContext>();
        b.UseApplicationServiceProvider(services);
        // Same two lines Wolverine's own tenanted builder uses: maps the envelope tables into the model.
        b.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        b.ReplaceService<IModelCacheKeyFactory, WolverineModelCacheKeyFactory>();
        b.UseNpgsql(dataSource);
        return b.Options;
    }

    public OrdersDbContext Create(string tenantId) => new(OptionsFor(schemas.For(tenantId)));

    public ValueTask<OrdersDbContext> BuildAsync(string tenantId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Create(tenantId));

    public async ValueTask<OrdersDbContext> BuildAndEnrollAsync(MessageContext messaging, CancellationToken cancellationToken)
    {
        var db = Create(messaging.TenantId ?? throw new InvalidOperationException("Message has no tenant id"));
        await messaging.EnlistInOutboxAsync(new EfCoreEnvelopeTransaction(db, messaging));
        return db;
    }

    public ValueTask<OrdersDbContext> BuildAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("OrdersDbContext requires a tenant");

    // Used by Wolverine only to inspect the model; points at a schema that does not exist.
    public DbContext BuildForMain() => new OrdersDbContext(BuildOptionsForMain());
    public DbContextOptions<OrdersDbContext> BuildOptionsForMain() => OptionsFor(schemas.NoTenant);

    public Task ApplyAllChangesToDatabasesAsync() => Task.CompletedTask; // the spike creates its own tables
    public Task EnsureAllDatabasesAreCreatedAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<DbContext>> FindAllAsync() =>
        Task.FromResult<IReadOnlyList<DbContext>>(TenantSchemas.TenantIds.Select(t => (DbContext)Create(t)).ToList());
}
