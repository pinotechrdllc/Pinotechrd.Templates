using Pinotechrd.Security.Entities;

namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// A user of a tenant, stored in the tenant's <c>users</c> table by ASP.NET Core Identity through the security library.
/// Its <c>Id</c> is the opaque identifier the access token carries in the user claim (<c>Identity:UserIdClaimType</c>).
/// </summary>
public sealed class AccessUser : BaseSecurityUser;
