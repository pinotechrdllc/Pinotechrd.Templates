using MyApp.BuildingBlocks.Application.Tenancy;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The scoped implementation of <see cref="ICurrentTenant"/> and <see cref="ICurrentTenantInitializer"/>: one instance
/// per scope, set once. It holds no static or ambient state, so nothing reaches another scope.
/// </summary>
internal sealed class CurrentTenant : ICurrentTenant, ICurrentTenantInitializer
{
    private const string NotSet = "No tenant is set for this scope.";

    private readonly Lock _gate = new();
    private volatile Selected? _selected;

    public bool IsSet => _selected is not null;

    public Guid TenantId => (_selected ?? throw new InvalidOperationException(NotSet)).TenantId;

    public string SchemaName => (_selected ?? throw new InvalidOperationException(NotSet)).SchemaName;

    /// <summary>
    /// Whether the scope's tenant connections assume the tenant's database role (<see cref="TenantRoleName.For"/>).
    /// Always, except in a scope the tenant migrator set with <see cref="InitializeForMigration"/>: its connections keep
    /// the migrator's own login, which owns what the migrations create. <see langword="true"/> before a tenant is set.
    /// </summary>
    public bool AssumesTenantRole => _selected?.AssumesTenantRole ?? true;

    public void Initialize(Guid tenantId) => Select(tenantId, assumesTenantRole: true);

    /// <summary>
    /// Sets the tenant of a scope of the tenant migrator, whose connections do not assume the tenant's role. Internal:
    /// application code reaches only <see cref="ICurrentTenantInitializer"/>, so its connections always assume the role.
    /// </summary>
    internal void InitializeForMigration(Guid tenantId) => Select(tenantId, assumesTenantRole: false);

    private void Select(Guid tenantId, bool assumesTenantRole)
    {
        var schemaName = TenantSchemaName.For(tenantId);
        lock (_gate)
        {
            if (_selected is { } selected)
            {
                if (selected.TenantId == tenantId && selected.AssumesTenantRole == assumesTenantRole)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "This scope already works for another tenant; a scope is set once. Create a new scope for each tenant.");
            }

            _selected = new Selected(tenantId, schemaName, assumesTenantRole);
        }
    }

    private sealed record Selected(Guid TenantId, string SchemaName, bool AssumesTenantRole);
}
