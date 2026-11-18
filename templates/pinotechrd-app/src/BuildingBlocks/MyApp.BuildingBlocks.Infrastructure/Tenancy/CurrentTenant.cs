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

    public void Initialize(Guid tenantId)
    {
        var schemaName = TenantSchemaName.For(tenantId);
        lock (_gate)
        {
            if (_selected is { } selected)
            {
                if (selected.TenantId == tenantId)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "This scope already works for another tenant; a scope is set once. Create a new scope for each tenant.");
            }

            _selected = new Selected(tenantId, schemaName);
        }
    }

    private sealed record Selected(Guid TenantId, string SchemaName);
}
