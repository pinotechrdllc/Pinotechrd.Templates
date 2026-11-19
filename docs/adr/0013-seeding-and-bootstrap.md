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
6. The migrator's `bootstrap` command creates the operator tenant and its first administrator from the `Bootstrap`
   settings (`Slug`, `Name`, `AdminEmail`, `AdminPassword`). The whole flow runs under a session advisory lock of its
   own (`operator-bootstrap`), so concurrent runs take turns. Once the catalog is up to date: the plans; the operator's
   subscription (id `SeedIds.OperatorSubscription`, plan `OPERATOR` by code, inserted on no conflict); a check that the
   subscription holds no tenant of another slug and is unlinked or linked to this slug's tenant (otherwise exit 1 before
   anything is provisioned); the tenant, through `TenantProvisioner.ProvisionOperatorAsync`, the only way onto the
   operator subscription (`ITenantProvisioner`, and so the `provision` command, refuses it with
   `tenant_subscription_reserved`); the subscription linked to it by an update conditioned on `tenant_name` being empty or
   already the tenant's id (otherwise exit 1); then the tenant
   migrated again with `OperatorAdminSeeder` (an `ITenantSeeder` registered only for this command, acting only on the
   tenant of the `BootstrapTarget` singleton, under the tenant lock, in one transaction): the system role
   `Administrator` (id `SeedIds.AdministratorRole`) with every permission, the user created through Identity's
   `UserManager<AccessUser>` (`UserName` = email, email confirmed) when no user has that email, and the membership.
   An existing user is never changed and its password never reset. When the role already has members and none has
   that email, nothing is written and the command exits 1 (the message names neither email). Identity's refusals are
   reported by `IdentityError.Code` only.
7. Passwords (`AddAccessPasswordPolicy`, shared by `AccessModule` and the bootstrap): Identity V3 hashes (PBKDF2 with
   HMAC-SHA512), 210 000 iterations; 15 to 256 Unicode scalar values, valid UTF-16, not only white space, and no
   composition rule (it replaces the package's 8 characters and a digit), checked by `AccessPasswordValidator`, an
   `IPasswordValidator<AccessUser>` the policy registers; one account per email, the user name being the email, of
   Identity's default user name characters (set explicitly by the policy). The bootstrap checks its password and email
   at start (exit 2, before any write) with the policy's own `IsValidPassword` and `IsValidUserEmail`.
8. The administrator's password comes only from the environment (`Bootstrap__AdminPassword`) or user secrets. On the
   command line or in any `appsettings*` file it is refused at start (exit 2,
   `BootstrapOptions.PasswordSourceFailure`). It is checked at start only when present; when absent it is required
   only if the administrator must be created (exit 2, naming the setting). It and the email never appear in the report,
   the logs (Identity's log is silenced) or an error.

## Consequences

- No schema change: the seeding writes rows the existing migrations already allow.
- Seeding lengthens the time each tenant lock is held by a few statements per permission.
- Stale rows (a permission or a plan module no longer declared) stay until removed by hand; removing them is out of
  scope.
- Library gap: the package links a subscription to its tenant through `system_subscriptions.tenant_name` holding the
  tenant id, with no foreign key; the operator's subscription follows that convention.
- Deploy order: `migrate`, then `bootstrap` for the operator tenant and its first administrator. Running `bootstrap`
  again with the same email, even with another password, changes nothing; with another email it fails (exit 1) and
  changes nothing: a second administrator is added through the users endpoints, not the bootstrap. Concurrent runs end
  with one tenant, one subscription and one administrator; a run with another slug fails without provisioning.
- The operator subscription is reserved: no other tenant is ever provisioned on it.
- Out of scope: resetting the administrator's password, protecting the last administrator, breached-password checks.
