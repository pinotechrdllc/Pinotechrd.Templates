using Pinotechrd.Security.Entities;

namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>A role of a tenant, stored in the tenant's <c>roles</c> table; its permissions are in <c>role_permissions</c>.</summary>
public sealed class AccessRole : BaseSecurityRole;
