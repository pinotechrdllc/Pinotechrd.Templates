# ADR 0010: The migrator command and the catalog migrations

Status: accepted. Date: 2026-11-19.

## Context

ADR 0001 decided that migrations run in a separate command, never at API start, with tenants migrated in parallel
(about 4) and `GetPendingMigrationsAsync` checked first. The tenant persistence unit built `ITenantMigrator`, which
migrates tenant schemas under a per-tenant advisory lock, but nothing migrated the shared `catalog` schema, nothing
listed the tenants, and no command ran either. ADR 0009 left two open points: the tenant migrator refuses any model that
names a schema, and `AccessDbContext` names `catalog` on the security library's catalog tables (excluded from its
migrations); and the migrator carries ASP.NET Core through `Access.Infrastructure` because the library does.

## Decision

1. A plain `CatalogDbContext` in `Access.Infrastructure/Persistence/Catalog` (the only project, with Endpoints, that may
   reference the library) maps the library's five catalog entities with the same table settings as
   `PinotechrdSecurityDbContext` (`ToTable`, lengths, precision, unique indexes, relations), ignores
   `SubscriptionPlanModule.Module` (modules live in tenant schemas: matched by id, no foreign key), and sets
   `HasDefaultSchema("catalog")`. History table `catalog.__ef_migrations_catalog`. It is not derived from the library's
   context, which would bring the Identity tables into the catalog. A test compares every column of every excluded
   catalog table of the `AccessDbContext` model with the migrated catalog (store type and nullability), so the two
   mappings cannot drift silently.
2. The tenant migrator's schema check skips tables with `ITable.IsExcludedFromMigrations`: such a table is read by the
   tenant context but created by another context. Every other check stays (default schema, migrated tables, views,
   functions, procedures, sequences, extensions). Consequence: an excluded table may name any schema; that is the point.
   In exchange, a migrated tenant table may hold no foreign key to an excluded table or to any table that names a
   schema: it would tie every tenant to the catalog or to another tenant's schema.
3. A building-block seam in `BuildingBlocks.Infrastructure/Migrations`, shaped like the tenant one:
   `AddSharedDbContext<TContext>(schema)` (same data source and its checks, snake_case, no tenant guard, history
   `<schema>.__ef_migrations_<schema>`; `public`, `pg_*`, `extensions` and `tenant_*` refused) registers an
   `ISharedMigrations`; `ITenantDirectory` is the port that lists tenant ids; `AddDatabaseMigrator()` registers
   `IDatabaseMigrator` and the tenant migrator. The database migrator refuses a shared model with an object outside its
   schema: tables (except excluded ones), views, functions, procedures, sequences, and PostgreSQL extensions, enums,
   ranges and collations.
4. One global advisory lock, `DatabaseMigrator.GlobalLockKey` (`database-migration`), taken with the same try-and-wait
   loop on a connection of its own as the tenant locks (moved, unchanged, into `Tenancy/AdvisoryLock.cs` and used by
   both migrators). Run order: take the lock; migrate the shared contexts in registration order; read every tenant id
   before any tenant starts; then `ITenantMigrator.MigrateAsync(ids, parallelism)`. A failed shared context stops the run
   before any tenant. A tenant id with no schema (the empty GUID) is a failure of that tenant alone, reported with its
   id; the other tenants still run. The lock connection is the one the tenant migrator leaves free in the pool, so a
   pooled connection string with a `Maximum Pool Size` below 3 is an invalid setting, refused when the migrator starts.
5. `AccessPersistenceRegistration`: `AddAccessPersistence()` (the schema provider and `AddTenantDbContext<AccessDbContext>`),
   called by `AccessModule`, so the API and the migrator build the same tenant context; `AddAccessCatalog()` (the catalog
   context and `CatalogTenantDirectory`: every `catalog.tenants` row, by id), called by the migrator only for now.
6. `src/MyApp.Migrator`: `Program.cs` is one line calling `MigratorHost.RunAsync(args, report, cancellationToken)`,
   which builds a generic host (command line, environment, user secrets in Development, appsettings), validates the
   connection string and `Migrator:MaxDegreeOfParallelism` (default 4, at least 1) at start, and writes one line for the
   catalog, one per tenant and a summary, never a connection string. Exit codes: 0 success, 1 a migration failed, 2 an
   invalid setting, 130 cancelled (Ctrl+C, a stop signal or the caller's token: reported as cancelled, never an
   unhandled exception; what finished stays applied and the locks are released as on any other end). `MigratorComposition.cs` declares the partial hooks `AddLayers` and `AddModules`, like the API's
   `BusinessComposition.cs`; no implementation yet, so `template.json` is unchanged.
7. Design-time factories for both contexts (the Access one gets a schema provider that throws: in `SearchPath` mode the
   library never calls it). Initial migrations made with `dotnet ef` (pinned in `.config/dotnet-tools.json`, the
   migrator as startup project, no `--namespace`): `InitialAccess` in `Persistence/Migrations`, `InitialCatalog` in
   `Persistence/Catalog/Migrations`. An `.editorconfig` marks those folders as generated code, so the analyzers of
   hand-written code (file-scoped namespaces, unused usings) do not fail the build on tool output.
8. Before `InitialAccess` ships, `AccessDbContext.OnModelCreating` adds, on top of the library's model and without
   changing the library: foreign keys `role_permissions.role_id` to `roles` and `user_permissions.user_id` to `users`,
   both cascading, and unique indexes on `(role_id, permission_id)` and `(user_id, permission_id)`; every table stays
   without a schema, and the library's `tenant_id` column stays. The library writes those tables only by replacing a
   role's or a user's whole set in one save (old rows removed, distinct new rows added), which the unique indexes allow
   because EF Core orders a delete before an insert of the same key values.

## Consequences

- Generated migrations are now a contract: `InitialAccess` holds no catalog DDL and no foreign key to the catalog, and a
  change to either model needs a new migration (a test checks `HasPendingModelChanges` for both contexts).
- The API never resolves `IDatabaseMigrator` or `ITenantMigrator` and creates no schema at start.
- The migrator references neither the web building block nor an endpoints project, but still carries ASP.NET Core
  through the library (ADR 0009, gap 13).
- Out of scope: provisioning, a role per tenant, seeding, a `--tenant` filter, the refresh store, Data Protection keys,
  migrator telemetry, the `AddModules` implementation for the stamp module, catalog access from the API.
