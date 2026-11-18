using System.Data.Common;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pinotechrd.Security.Enums;
using Pinotechrd.Security.Extensions;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

public static partial class TenantSchemas
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    public static string Validate(string schema) =>
        SchemaName().IsMatch(schema) ? schema : throw new ArgumentException($"Invalid schema name '{schema}'.");
}

/// <summary>
/// One instance for the whole process. Before a connection opens it requires a current tenant (no tenant: the open
/// fails, nothing reaches the server); right after it opens it runs SET search_path to that tenant's schema only.
/// The reset when the connection goes back to the pool is Npgsql's own (DISCARD ALL, unless No Reset On Close is set).
/// </summary>
public sealed class TenantSearchPathInterceptor : DbConnectionInterceptor
{
    public const string NoTenantMessage = "No current tenant: the connection is not opened (no fallback to public).";

    private static string SetSql(DbContext? context) =>
        context is ITenantSchemaContext { TenantSchema: { } schema }
            ? $"SET search_path TO \"{TenantSchemas.Validate(schema)}\""
            : throw new InvalidOperationException(NoTenantMessage);

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        _ = SetSql(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _ = SetSql(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SetSql(eventData.Context);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SetSql(eventData.Context);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>The user acting outside HTTP (the console questions). Over HTTP the "id" claim of the token wins.</summary>
public sealed class CurrentUser
{
    public string? UserId { get; set; }
}

/// <summary>
/// Tenants are rows of catalog.tenants; only active ones exist for the middleware. It runs before a tenant is known,
/// so it cannot use the tenant-bound context (its open would fail): it uses the catalog context, same data source.
/// </summary>
public sealed class CatalogTenantStore(PinotechrdCatalogDbContext catalog) : ITenantStore
{
    public Task<bool> TenantExistsAsync(string tenantId) =>
        catalog.Tenants.AnyAsync(t => t.Identifier == tenantId && t.Status == TenantStatus.Active);
}

/// <summary>Role permissions come from the tenant's own tables: user_roles -> role_permissions -> security_permissions.</summary>
public sealed class TenantSecurityDataProvider(
    PinotechrdTenantSecurityDbContext db, TenantContext tenant, CurrentUser current, IHttpContextAccessor http)
    : ISecurityDataProvider
{
    public string? GetCurrentUserId() =>
        http.HttpContext?.User.FindFirstValue("id") ?? current.UserId;

    public string? GetCurrentTenantId() => tenant.TenantId;

    public async Task<IEnumerable<string>> GetUserRolesAsync(string userId)
    {
        var id = Guid.Parse(userId);
        return await (from ur in db.UserRoles
                      join r in db.Roles on ur.RoleId equals r.Id
                      where ur.UserId == id
                      select r.Name!).ToListAsync();
    }

    public async Task<IEnumerable<string>> GetUserPermissionsAsync(string userId)
    {
        var id = Guid.Parse(userId);
        return await (from ur in db.UserRoles
                      join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                      join p in db.SecurityPermissions on rp.PermissionId equals p.Id
                      where ur.UserId == id
                      select p.Code).ToListAsync();
    }
}

public static class SpikeRegistration
{
    /// <summary>Constant, obviously fake test signing key (48 characters). Never a real secret.</summary>
    public const string FakeSigningKey = "pinotechrd-spike-fake-signing-key-not-a-secret!!";

    public const string Issuer = "Pinotechrd.Spike";
    public const string Audience = "Pinotechrd.Spike.Client";

    /// <summary>The registration the template would make: one shared data source, the interceptor, the library.</summary>
    public static IServiceCollection AddSpikeSecurity(this IServiceCollection services, NpgsqlDataSource shared)
    {
        services.AddLogging();
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddSingleton<TenantSearchPathInterceptor>();
        services.AddDbContext<PinotechrdTenantSecurityDbContext>((sp, o) => o
            .UseNpgsql(shared)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantSearchPathInterceptor>()));
        services.AddDbContext<PinotechrdCatalogDbContext>(o => o
            .UseNpgsql(shared, n => n.MigrationsHistoryTable("__ef_migrations_catalog", PinotechrdSchemaMapping.CatalogSchema))
            .UseSnakeCaseNamingConvention());

        services.AddPinotechrdSecurity<PinotechrdTenantSecurityDbContext, AppUser, AppRole>(o =>
        {
            o.Jwt.Secret = FakeSigningKey;
            o.Jwt.Issuer = Issuer;
            o.Jwt.Audience = Audience;
            o.Tenancy.SkipPaths.Add("/health");
        });

        services.AddScoped<ITenantStore, CatalogTenantStore>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ISecurityDataProvider, TenantSecurityDataProvider>();
        return services;
    }
}
