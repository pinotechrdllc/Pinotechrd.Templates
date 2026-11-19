# ADR 0011: Tenant provisioning

Status: accepted. Date: 2026-11-19.

## Context

ADR 0010 built the migrator: it migrates the catalog and every tenant listed in `catalog.tenants`, but nothing creates a
tenant. A tenant needs a catalog row (its slug, name, subscription and status) and a schema migrated to the current
version. Both must agree after a crash, a failed migration or two concurrent requests for the same slug, and nothing a
caller types may reach SQL text or name a schema.

## Decision

1. A port in `Access.Application/Tenants`: `ITenantProvisioner.ProvisionAsync(ProvisionTenantRequest(Slug, Name,
   SubscriptionId))` returns a `Result<ProvisionedTenant(TenantId, Slug, AlreadyProvisioned)>`. Expected failures are
   results (ADR 0004), with the codes of `TenantErrorCodes` in `contract/problem-codes.v1.md`. The Application project
   references neither the security library nor EF Core.
2. The implementation, `TenantProvisioner` in `Access.Infrastructure/Persistence/Catalog`, works on `CatalogDbContext`
   and `ITenantMigrator`, registered by `AddAccessCatalog()`. The API does not call it yet; the tenants endpoints will.
3. Input is checked before any I/O and refused, never normalized: a slug matches
   `^(?!.*--)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z` (culture-invariant; no doubled hyphen), a name is 1 to 200
   characters, not blank, without control or format characters, line or paragraph separators, or lone surrogates
   (Unicode Cc, Cf, Zl, Zp, Cs; so a name of only invisible characters is blank), and the subscription id is not the
   empty GUID. `ProvisionTenantRequest.SlugPattern` is the exact pattern the validation uses. No message quotes a
   rejected value.
4. Row before schema, as the record of intent: no schema exists without its row. The provisioner sets the id
   (`Guid.CreateVersion7()`), the identifier (the slug, as given: `Tenant.Create` is not used because it rewrites the
   identifier), status `Provisioning` and `db_schema = TenantSchemaName.For(id)`. The schema name comes only from the id,
   never from the slug or the `db_schema` column.
5. Concurrency rests on database constraints, no new lock. The insert that loses on the unique index
   `ix_tenants_identifier` (SQLSTATE 23505, checked by constraint name) reads the winner's row. Every later change is an
   `ExecuteUpdate` conditioned on `status = Provisioning`: recording the start (started-at), then, after the tenant
   migrator returns, recording a failure or marking the row active. A late failure cannot overwrite a success, and a
   tenant suspended in the meantime is never activated. `provisioning_attempts` counts the attempts that settled an
   outcome on a provisioning row: it is incremented in the same conditioned update that records the failure or activates
   the row, so a request whose update finds the row no longer provisioning (another request activated it while this one
   waited on the tenant lock) counts nothing and reports the tenant as already provisioned, or the refusal of its new
   status; a run interrupted or cancelled before that update is not counted. Of two concurrent requests that both reach
   the activation, the one whose update lands first reports "provisioned"; it may be the one that waited and applied no
   migration. Schema creation and migrations are serialized by the tenant migrator's per-tenant lock,
   so each migration is applied once. Provisioning does not take the global migration lock: the tenant lock already
   keeps it apart from a concurrent `migrate` of that tenant.
6. Idempotency key: the slug. The same slug, name and subscription is the same request: an active tenant is reported as
   already provisioned, a tenant left provisioning (failed or crashed run) is resumed and completed. Another name or
   subscription is `tenant_slug_taken`. A suspended or cancelled tenant is refused (`tenant_suspended`,
   `tenant_cancelled`) and left unchanged. The subscription must exist (`tenant_subscription_not_found`); whether it is
   active is not checked here. No slug is reserved.
7. A failed migration leaves the row provisioning with `provisioning_error` set to the SQLSTATE of the server error, or
   the exception's type name: never a message, which can name the host, the user or a value (the rule of
   `MigratorHost`'s report).
8. Command: `MyApp.Migrator provision --Provision:Slug=acme --Provision:Name=Acme --Provision:SubscriptionId=<id>`. A
   first argument that is not a setting is a command, stripped before the configuration reads the rest; an unknown one
   exits 2. A misplaced command never becomes a full migration: `provision` (any case) anywhere but first, as a setting
   key (`provision=x`, `--provision`), any other argument that is not a setting (a positional, a single-dash switch, a
   last `--Key` without a value), and any `Provision:*` or `Provision__*` setting without the command (command line,
   environment or any other source) exit 2 without quoting the argument. The `Provision` settings are validated at start (exit 2). Before provisioning, the command checks that the
   catalog has no pending migration and otherwise exits 1, asking to run `migrate` first. Exit codes stay 0, 1, 2 and 130;
   without a command the migrator behaves as before.

## Consequences

- One provision holds the catalog connection and, while it migrates, the tenant migrator's two. The tenants endpoints
  will need a limit on concurrent provisions so they cannot drain the API's pool.
- Until the API refuses tenants that are provisioning or suspended (a PLAN task), a token for such a tenant still
  reaches its schema.
- Out of scope: the per-tenant `NOLOGIN` role (it will be created under `ITenantMigrator.MigrateAsync`), seeding, the
  tenants endpoints, activate and suspend, `migrate` skipping provisioning tenants, deprovisioning, a `--tenant`
  filter, reserved slugs, renaming.
