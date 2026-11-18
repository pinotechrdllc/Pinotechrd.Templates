# Spike: Pinotechrd.Security on one PostgreSQL schema per tenant

Throwaway code. It answers, on a real PostgreSQL, whether the published `Pinotechrd.Security` and `Pinotechrd.Security.Abstractions` `1.2.0-preview.2` packages work with the schema-per-tenant design of [ADR 0001](../../docs/adr/0001-tenant-module-migrations.md): no schema in the model or the migrations, and `search_path` set per connection on one shared data source. The result and the decision are in [ADR 0008](../../docs/adr/0008-security-library-with-tenant-schemas.md).

The spike is not part of the template package and the harness does not build it. Each project pins its own package versions; nothing is inherited from the template.

## How to run

Needs Docker and the .NET 10 SDK.

```powershell
./run.ps1
# or
dotnet run --project Spike/Pinotechrd.Spikes.SecuritySchemaPerTenant.csproj
```

Testcontainers starts `postgres:17.7` and removes it at the end. Q5 also builds `OpenApiProbe` twice (about a minute). The output has one detail line per check (`ok` or `FAIL`) and per observed fact, then a `## Results` section with one line per question. The exit code is 1 when a gating question fails, 0 otherwise. Q6 only documents behaviour and does not gate.

## Layout

| Path | What it is |
| --- | --- |
| `Spike/Model.cs` | The override: `PinotechrdTenantSecurityDbContext` and `PinotechrdCatalogDbContext` derive from the library's context and remap it (`PinotechrdSchemaMapping.Apply`). Also the unmodified library context, used only for contrast. |
| `Spike/Tenancy.cs` | `TenantSearchPathInterceptor`, the registration the template would make (`AddSpikeSecurity`), the tenant store on the catalog context and the security data provider. |
| `Spike/RefreshTokenStores.cs` | Two database refresh-token stores in `catalog.refresh_tokens`: atomic (`UPDATE ... RETURNING` in `ValidateAsync`) and naive (`SELECT`). |
| `Spike/Q*.cs` | One file per question; `Seed.cs` writes the test data. |
| `OpenApiProbe/` | A minimal web project that generates its OpenAPI document at build time with no configuration, as the template does. |

## Setup

- Catalog schema `catalog`: plan `BASIC` with module `SALES` only; active subscriptions and tenants `a` and `b`; `refresh_tokens`.
- Tenant schemas `tenant_a` and `tenant_b`, created from one generated script with no schema in it. Each has modules `SALES` and `REPORTS` with the same ids in both schemas, permissions `SALES.VIEW`, `SALES.EDIT`, `REPORTS.VIEW`, a role `clerk` granted all three, and user `pinotechrd.user` in that role. Tenant `a` only: a per-user deny of `SALES.EDIT` and a user `pinotechrd.only-a`.
- One `NpgsqlDataSource` with `Max Pool Size=4` for everything, so physical connections are reused across tenants.
- The signing key is a constant, obviously fake test value; the passwords are test values for the throwaway container.

## What each question checks

| Question | Gating | PASS when |
| --- | --- | --- |
| Q1. Model | yes | Tenant entities have no schema; the 5 catalog entities are in `catalog` and excluded from tenant migrations; no foreign key crosses catalog and tenant; tenants `a` and `b` get the same model instance; the tenant script names no schema; one script applied through the interceptor gives both tenants the same tables and `public` stays empty. |
| Q2. Behaviour per tenant | yes | 40 sequential and 80 parallel alternating calls each see only their own tenant (`current_schema()`, `UserManager`, `SecurityEngine.CanAccessAsync`, `DbSecurityStore`); lockout counters are per tenant; a row of `a` is invisible from `b`; with no tenant the open fails; a pooled connection comes back with the default `search_path`. |
| Q3. Refresh | yes | With the atomic store, exactly one of two concurrent `RefreshAsync` calls succeeds in 50 of 50 pairs. The naive store and the library's in-memory default are measured for contrast. |
| Q4. HTTP | yes | Anonymous gets 401 and authenticated-without-permission gets 403, with no redirect (or a minimal setting makes it so). |
| Q5. Start-up | yes | The eager validation is recorded, and a guarded registration lets the build-time OpenAPI document be generated. |
| Q6. Tenant resolution | no | Documents what `UseTenantResolution` reads and answers (library gap L2). |

## Results

Run on 2026-11-18: Windows 11, Docker Desktop (WSL2), PostgreSQL 17.7, .NET 10.0.14, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, EF Core 10.0.15. Exit code 0.

| Question | Result | Observed |
| --- | --- | --- |
| Q1 | PASS | Overriding `OnModelCreating` works. The library maps the catalog to a hard-coded `public` and every tenant table to `ISchemaProvider.GetSchemaName()`. Used as documented, with EF's default cache key, the model of the first tenant is reused for the next (tenant `b` saw `users` in `tenant_a`). With the library's `ModelSchemaCacheKeyFactory`, which `AddPinotechrdSecurity` does not register, there is one model per tenant. With the override: 11 tenant tables with no schema, 5 catalog tables in `catalog` and excluded from the tenant script, the plan module to security module key gone after ignoring its two navigations, one model instance for both tenants, and a tenant script that names no schema. About 60 lines of code. |
| Q2 | PASS | All 120 alternating calls (sequential and parallel on 4 connections) saw only their own tenant, including the catalog x tenant join of `DbSecurityStore`. Two failed logins in `a`: counter 2 in `a`, 0 in `b`. After five, `a` answers `Account locked out` and `b` signs in. Login permissions: `a` = `SALES.VIEW` (deny applied, `REPORTS` not in plan), `b` = `SALES.EDIT,SALES.VIEW`. With no tenant: `InvalidOperationException` before any SQL. A connection back from the pool has `search_path = "$user", public`; with `No Reset On Close=true` it keeps `tenant_a`. Also: `CanAccessAsync("Sales","View")` is false while `("SALES","VIEW")` is true; `SecuritySeeder.SeedModuleAsync` fails with `23503` (permission inserted without its module id). |
| Q3 | PASS | Atomic store: exactly one success in 50 of 50 pairs. Naive store: both succeeded in 50 of 50. Library's default `InMemoryRefreshTokenStore`: both succeeded in 50 of 50. A token of tenant `a` refreshed while the request's tenant is `b` fails with `User not found` and is consumed. With no tenant the refresh throws after the token is consumed. |
| Q4 | PASS | Default authenticate, challenge and forbid schemes are all `Bearer`; the Identity cookie schemes are registered but not defaults. Anonymous: 401 with `WWW-Authenticate: Bearer`. Bearer without the permission: 403. No redirect, no extra setting. An `/api` request with no tenant gets 400 from the tenant middleware before authorization runs. |
| Q5 | PASS | `AddPinotechrdSecurity` throws `InvalidOperationException` from inside the call: `PinotechrdSecurity requires a non-empty JWT secret.` (no delegate, null, whitespace) and `JWT secret must be at least 32 characters.` It validates no connection string. `MinimumSecretLength` can be lowered: 8 characters are accepted and signing then fails with `IDX10653`. Build-time OpenAPI generation with no configuration: exit 1 with that exception. With a random key registered only under `GetDocument.Insider`: exit 0 and the document is written. |
| Q6 | PASS (not gating) | Reads, in order: claim `tenant_id` (only when authenticated), header `TenantId`, JSON body `tenantId`, query `tenantId`. No path segment. Missing on an `/api` path: `400`, no content type, `TenantId is required.` Unknown: `400`, `Invalid TenantId.` Missing elsewhere: passes with no tenant. `SkipPaths` `/health` also skips `/healthy-admin`. With `UseTenantResolution` before `UseAuthentication`, a bearer of `a` plus header `b` resolves `b`. |

## Not tested

PgBouncer; real migration files and their history table (the spike runs the SQL that EF's model differ and SQL generator produce, the same pair `migrations add` and `migrations script` use; ADR 0001 already covers the history per schema); several API instances; the two-factor and password-reset flows; an application that hands EF an already-open connection.
