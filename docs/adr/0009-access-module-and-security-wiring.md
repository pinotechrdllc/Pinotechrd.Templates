# ADR 0009: The Access module and how the security library is wired

Status: accepted. Date: 2026-11-18.

## Context

ADR 0008 showed that `Pinotechrd.Security` works on tenant schemas selected by `search_path`. Version
`1.2.0-preview.3` closes the gaps that forced a model override there: `UsePinotechrdSecurity` takes a
`TenantSchemaMode.SearchPath` mode with no schema on the tenant tables, a catalog schema of our choice, a switch that
excludes the catalog tables from the context's migrations, and a switch that leaves the binding of every connection to
the application. Its JWT settings are validated with `ValidateOnStart` instead of inside the registration call.

Three things in the library still disagree with the template:

- Claim types. `PinotechrdAuthManager` issues the user id as `id`, never as `sub`, while the template reads the user
  from `Identity:UserIdClaimType` (`sub`, RFC 7519). The library exempts only its tenant claim from inbound mapping, so
  the platform's handler would also rename `sub`. The library takes its tenant claim type once, when it is registered.
- The tenant. The library keeps its own scoped `TenantContext`, which its engine, its authorization handler and its
  login compare with the token; its tenant resolution middleware also accepts a header for anonymous requests.
- Permissions. Its engine needs an `ISecurityDataProvider` for the user, the tenant and the role permissions.

The template needs one place that decides each of these, in both architectures, before any login endpoint exists.

## Decision

1. One module, `Access`, in both architectures, with the five projects of any module under `src/Modules/Access`. The host
   adds it in the shared `BusinessComposition.cs`, before the clean layers or the other modules; a clean project keeps
   it (`template.json` excludes only the stamp module). `--module Access` is reserved. Only its Infrastructure and
   Endpoints projects reference the library; its Domain and Contracts use only the base class library.
2. Persistence. `AccessDbContext` derives from `PinotechrdSecurityDbContext<AccessUser, AccessRole>` and is registered
   with `AddTenantDbContext<AccessDbContext>("Access", ...)` and `UsePinotechrdSecurity` in `SearchPath` mode, catalog
   schema `catalog`, catalog excluded from migrations, and `ApplicationTakesResponsibilityForSearchPath`: the template's
   connection guard binds every connection, on the shared data source, with history table `__ef_migrations_access`. The
   library's `search_path` interceptors are not used. `AccessSchemaProvider` returns the current tenant's schema and
   throws without one; its value never reaches SQL.
3. Claim types have one source, `ApiIdentityOptions` (`Identity`). The module binds it when it registers, gives
   `TenantIdClaimType` to the library's `Tenancy.ClaimType`, and turns `MapInboundClaims` off on the bearer handler.
   `AccessIdentityValidator` (the library's `IIdentityValidator<TUser>` hook) adds the claim `UserIdClaimType` with the
   user's id to every token, so tokens carry `sub`; the library's `id` stays in the token and is never read. A start
   check (`IValidateOptions<ApiIdentityOptions>`) stops the host when the bound value differs from the library's tenant
   claim type, naming both. Rejected: reading `id` as the user claim, because it is not the standard subject claim and a
   later library version could drop it.
4. Permissions. `SecurityEnginePermissionChecker` is the checker of the module `Access`; `Access.Users.Read` is asked as
   `CanAccessAsync("Access", "Users.Read")`. `AccessSecurityDataProvider` takes the user and the tenant from
   `ICurrentUser` and `ICurrentTenant` (lower-case `D` GUIDs) and reads the role permissions in one query (users, user
   roles, role permissions, permission code), active permissions of active modules only, none for an inactive user. The
   per-user overrides follow the same rules: the library's `DbSecurityStore.GetUserOverridesAsync` filters by user id
   only, so an allow would still grant to a deactivated user or on a deactivated permission or module. The module wraps
   the library's `ISecurityStore` registration (its cache included) in `AccessSecurityStore`, which reads the overrides
   in one query on `AccessDbContext` (active user, active permission, active module) and passes every other call through.
   Denies are filtered the same way; one dropped is moot, because what it would remove is never granted by the role path
   either. The `permission` claims of a token are never read.
5. The tenant bridge. `SecurityTenantContextStep` copies `ICurrentTenant` into the library's `TenantContext` at the new
   order `AuthenticatedRequestStepOrder.SecurityContext` (-150), after the tenant step. `UseTenantResolution` is not used.
6. Settings. The JWT settings are in `Access:Jwt`, bound lazily into `PinotechrdSecurityOptions` after the library's
   registration; issuer and audience in `appsettings.json`, the secret only in user secrets or the environment. When
   `ModuleContext.IsGeneratingApiDocument`, a random throw-away key, issuer and audience let the build-time contract
   generation start with no configuration; the contract does not change.
7. The library's authorization registrations stay: its policy provider derives from the default one and its handler acts
   only on its own requirement type, which no template endpoint uses.

## Consequences

- Every host, test hosts included, needs `Access:Jwt:Secret` (at least 32 bytes) or it does not start.
- `AddPinotechrdSecurity` also registers ASP.NET Core Identity, its cookie schemes, `IHttpContextAccessor` and Data
  Protection. The default authenticate and challenge schemes are `Bearer`, and forbid falls back to challenge, so 401 and
  403 need no setting; Data Protection key storage is a separate task.
- The library caches subscription and module answers for 15 minutes per process (`TenantCacheStore`): a plan change can
  take that long on each instance. Role and per-user changes are read on every check.
- A deactivated user loses every permission on the next request, but the access token is a stateless JWT: it still
  authenticates `AuthenticatedOnly()` endpoints until it expires (`Access:Jwt:ExpiryMinutes`).
- The migrator of the tenant persistence refuses a model that names a schema; the Access model names `catalog` for its
  excluded catalog tables, so the Access migrations and the catalog migrator entry must handle this when they are built.
- The Migrator will carry ASP.NET Core through `Access.Infrastructure`, because the library does.
- Out of scope here: login, refresh, logout and change-password endpoints; the refresh store in the database; the tenant
  selection step; migrations, provisioning and seeding; a database role per tenant.

## Library gaps (continuing ADR 0008)

11. No `sub`. `Core/PinotechrdAuthManager.cs` (`BuildAuthenticatedResultAsync`) issues `id`, `name`, the tenant claim,
    the email and one `permission` per permission, and no `sub`; `TenantClaimMapping` exempts only the tenant claim from
    inbound mapping. Change: issue the user id as `sub` and keep it unmapped.
12. Permissions in tokens. Every token carries the user's permissions at login, which grow the token and go stale until
    it expires. Change: make them opt-in.
13. ASP.NET Core in persistence. The context, the stores and the engine live in the same package as the JWT and HTTP
    code, so a migrator that only needs the model carries ASP.NET Core. Change: split the model and stores into a
    package without ASP.NET Core.
