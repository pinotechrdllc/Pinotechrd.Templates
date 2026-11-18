# ADR 0001: Migrations per module inside one PostgreSQL schema per tenant

Status: accepted with conditions (spike `spikes/tenant-module-migrations`, phase 0b). Date: 2026-11-17.

## Context
Each tenant gets its own PostgreSQL schema. Inside it, every module (`Access`, `Sales`, `Billing`, ...) owns its tables (prefixed by module), its EF Core DbContext and its own migration history. We needed to know whether one migration set can serve every tenant schema, how the schema is selected at runtime, and what provisioning, upgrades and the per-deploy "nothing to do" pass cost.

## What was tested
- .NET 10, EF Core 10.0.15, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, EFCore.NamingConventions 10.0.1 (snake_case), Testcontainers.PostgreSql 4.17.0 with `postgres:17` (17.7).
- Three modules, two entities each, two real migrations each made with `dotnet ef migrations add` (initial; then a column, a unique index, or both). History tables `__ef_migrations_access|sales|billing`, unqualified.
- No schema anywhere in models or migrations (no `HasDefaultSchema`, no `ToTable(name, schema)`). The schema is chosen per connection in two ways: (1) `Search Path=<tenant>` in the connection string; (2) one shared connection string plus a `DbConnectionInterceptor` that runs `SET search_path TO "<tenant>"` on `ConnectionOpened`. The search path holds only the tenant schema, never `public`.
- 50 tenants per scenario, 3 modules per tenant, `Parallel.ForEachAsync` for parallelism, Npgsql pools cleared between scenarios (cold start, like a fresh migrator process). One unmeasured warm-up tenant first.

## Results
Machine: Windows 11, 8 logical CPUs, Docker Desktop (WSL2) on the same machine, container with `max_connections=400`. Numbers from the last of three runs; between runs they moved by about 20%. "Per tenant" is wall time / 50; p50 and p95 are single-tenant durations.

| Scenario | Selection | Par. | Total (s) | Per tenant (ms) | p50 (ms) | p95 (ms) | Server conns at end |
|---|---|---|---|---|---|---|---|
| a. provision, migration 1 | ConnectionString | 1 | 3.41 | 68.2 | 60.8 | 108.0 | 51 |
| b. provision, migration 1 | ConnectionString | 4 | 1.08 | 21.7 | 74.3 | 165.9 | 54 |
| b. provision, migration 1 | ConnectionString | 8 | 0.84 | 16.9 | 126.3 | 157.6 | 58 |
| a. provision, migration 1 | Interceptor | 1 | 2.55 | 50.9 | 49.8 | 56.9 | 9 |
| c. upgrade, migration 2 | ConnectionString | 1 | 2.14 | 42.8 | 43.2 | 50.3 | 58 |
| c. upgrade, migration 2 | ConnectionString | 4 | 0.65 | 13.0 | 47.7 | 63.7 | 58 |
| c. upgrade, migration 2 | ConnectionString | 8 | 0.54 | 10.8 | 74.9 | 113.3 | 58 |
| c. upgrade, migration 2 | Interceptor | 1 | 2.08 | 41.5 | 35.6 | 62.8 | 9 |
| d. no-op, MigrateAsync | ConnectionString | 1 | 1.63 | 32.6 | 31.7 | 37.7 | 58 |
| d. no-op, MigrateAsync | ConnectionString | 8 | 0.39 | 7.8 | 58.9 | 70.7 | 58 |
| d. no-op, MigrateAsync | Interceptor | 1 | 1.33 | 26.6 | 26.0 | 32.0 | 9 |
| d. no-op, pending check only | ConnectionString | 1 | 0.53 | 10.5 | 10.2 | 12.9 | 58 |
| d. no-op, pending check only | ConnectionString | 8 | 0.13 | 2.6 | 18.8 | 29.0 | 58 |

Checks (all PASS, exit code 0): each of the 200 tenant schemas has exactly the 9 expected tables; each has 3 history tables with both migrations of its module in order; migration 2 columns and indexes exist in every tenant (400/400 each); `public` has no tables; no other schema holds tables; the generated SQL scripts contain no schema qualifier; in both modes a row inserted through tenant A is invisible through tenant B; in both modes 20 alternating contexts for two tenants each saw only their own `current_schema()` and rows, sharing one cached model; migrating a tenant whose schema does not exist fails with `3F000: no schema has been selected to create in` and writes nothing to `public`; an empty tenant reports 0 applied and 2 pending migrations while 200 other schemas have history tables.

## Findings
- The design works. One migration set applies to any schema, and history per module per tenant works with unqualified table names.
- Cost is small. Provisioning is about 50-70 ms per tenant sequentially and about 17-22 ms with 4-8 in parallel. Going from 4 to 8 in parallel gained little on this machine. The no-op `MigrateAsync` costs about 27-33 ms per tenant (it takes the history lock and opens a transaction). Checking `GetPendingMigrationsAsync` first is about 3x cheaper (about 10 ms).
- `Search Path` in the connection string makes a separate Npgsql pool for each tenant: about one server connection stays open per tenant migrated (51-58 here, against 9 with the interceptor). With hundreds of tenants this will exceed `max_connections` (default 100).
- No model cache key per schema is needed. The schema is not in the model, so EF caches one model per context type and it is correct for every tenant.
- By design, Npgsql's history repository always returns `Exists() == true`, because of search_path. It then catches "undefined table" errors. Applied and pending results were still correct per tenant.
- EF surprises: EFCore.NamingConventions also snake-cases the history columns (`migration_id`, `product_version`). With `--namespace`, the first `migrations add` put the model snapshot in a folder built from the namespace (`TenantModuleMigrations/Modules/...`), so it had to be moved by hand.
- Not tested: two migrators running on the same tenant at the same time (Npgsql runs `LOCK TABLE` on the unqualified history table), PgBouncer, more than 50 tenants per pass, and the interceptor when the app hands EF an already-open connection.

## Decision
Keep the design, with the conditions below.

## Consequences for the template
- Models and migrations never name a schema: no `HasDefaultSchema`, no schema in `ToTable`, and no schema in `MigrationsHistoryTable`. Each module sets `MigrationsHistoryTable("__ef_migrations_<module>")`.
- The search path holds only the tenant schema. Never add `public` as a fallback, so a missing schema fails instead of leaking tables into `public`. Validate schema names (`^[a-z][a-z0-9_]{0,62}$`) before using them in SQL.
- At runtime, the API selects the schema with a connection interceptor (or per-tenant settings on one shared data source), not one connection string per tenant, so the pool count does not grow with tenants.
- The migrator is a separate command. It runs `CREATE SCHEMA IF NOT EXISTS`, then migrates each module in a fixed order, with parallelism across tenants capped at about 4. On deploy it calls `GetPendingMigrationsAsync` and runs `MigrateAsync` only where something is pending. It never runs at API start.
- Keep one naming convention forever: changing it renames the history columns and breaks existing history.
- No foreign keys or joins across module tables. Shared data goes through module contracts.
- Drop "model cache key per schema" from the plan. Keep a test that alternates two tenants in one process.
