# ADR 0013: Seeding on migrate, and bootstrap

Status: accepted. Date: 2026-11-19.

## Context

A migrated tenant had empty `security_modules` and `security_permissions`, and the catalog had no plan, so the
security engine denied every permission: a module must be in the tenant's plan and its permissions must exist as rows
before a role can grant them. The permissions are declared in code (`PermissionCatalogue`); the rows must follow it
without a hand-written data migration per permission. The migrator's login is a member of no tenant role it can `SET`
(ADR 0012), and the connection guard refuses privileged logins, so seeding cannot run through the tenant role.

## Decision

1. Tenant data is seeded in migration mode inside `TenantMigrator`: after a tenant's modules are migrated, in the same
   scope (tenant set with `InitializeForMigration`, connections keep the migrator's login) and under the same tenant
   advisory lock, the migrator runs each `ITenantSeeder` (`SeedAsync(scopedServices, tenantId, ct)`) in registration
   order, checking the lock before each. A seeder's failure is that tenant's failure. Seeders are registered with
   `AddTenantSeeder<T>()`, shaped like `IModuleMigrations`. Not EF Core's `UseAsyncSeeding`, which knows no tenant.
2. Deterministic ids. Modules use the package's `SecurityModuleId.FromCode` (the id plans in the catalog name).
   Everything else uses `SeedIds`, UUIDv5 in the template's own fixed namespace: `Permission(code)`, `Plan(code)`,
   `PlanModule(planId, moduleCode)`, `AdministratorRole`, `OperatorSubscription`, `RolePermission(roleId, permissionId)`.
   Codes are upper-cased first. The package's `SecuritySeeder` is not used (random permission ids, no tenant column).
3. Additive only, in SQL: `INSERT ... ON CONFLICT DO NOTHING` through `ExecuteSqlAsync` with every value a parameter;
   the only identifiers in the text are fixed table names. Rows made by hand are matched by code. Plans are adopted by
   exact code. Modules and permissions must also carry the deterministic id of their code (plans link modules by
   `SecurityModuleId.FromCode`, so a module with another id would silently drop out of the tenant's plan): a row with
   the code but another id, or with the id but another code, fails the tenant with a message naming the code, and its
   seeding transaction is rolled back. Nothing is updated, deactivated, renamed or deleted. Concurrent runs end with one row each
   through the primary keys and the unique codes.
4. What `migrate` seeds:
   - in every tenant (`TenantSecuritySeeder`, one transaction): one module row per module of the catalogue (the first
     segment of its permissions; `ACCESS`), one permission row per permission (`ACCESS.USERS.READ`, name as declared,
     group the module), and, when the tenant has the system role `Administrator` (id `SeedIds.AdministratorRole`, never
     a role matched by name, which a tenant's user could rename), every permission granted to it. A permission removed
     from that role by hand comes back on the next run.
   - in the catalog (`CatalogPlanSeeder`, after the database migrator, once every shared schema succeeded; one report
     line, `Plans: N plan(s) and M plan module(s) added.`): the plans of `PlanComposition.For(catalogue)`, `OPERATOR`
     with every module and `STANDARD` with every module except `PlanComposition.OperatorOnlyModules` (`Platform`).
5. The Access module declares `Access.Users.Read`, `Access.Users.Manage`, `Access.Roles.Read`, `Access.Roles.Manage`
   (`AccessPermissions`); `AddAccessPermissions()` is called by `AccessModule` and by the migrator's composition, so
   both read the same catalogue.

## Consequences

- No schema change: the seeding writes rows the existing migrations already allow.
- Seeding lengthens the time each tenant lock is held by a few statements per permission.
- Stale rows (a permission or a plan module no longer declared) stay until removed by hand; removing them is out of
  scope.
- Library gap: the package links a subscription to its tenant through `system_subscriptions.tenant_name` holding the
  tenant id, with no foreign key; the operator's subscription follows that convention (bootstrap, next unit).
- Deploy order: `migrate`, then `bootstrap` for the operator tenant and its first administrator (next unit).
