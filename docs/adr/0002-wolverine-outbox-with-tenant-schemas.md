# ADR 0002: Wolverine outbox with one PostgreSQL schema per tenant

Status: Accepted (based on spike `spikes/wolverine-tenant-outbox`, branch `spike/phase-0b`, 2026-11-17)

## Context

The template uses one PostgreSQL database, one schema per tenant, EF Core 10 and no message broker. We want the
Wolverine transactional outbox so that a business change and the messages it publishes commit or roll back together.
Wolverine's built-in multi-tenancy is designed around a separate database per tenant (or "conjoined" tenancy, a
`tenant_id` column in shared tables). We had to find out whether schema per tenant works and where the envelope tables go.

## Options considered

- **A. Shared outbox schema.** One Wolverine message store; envelope tables in a shared `wolverine` schema. The tenant id
  travels with the message. Business rows go to the tenant schema, envelopes to `wolverine`, in one transaction.
- **B. Outbox per tenant schema.** Envelope tables inside each tenant schema.

## What was tested

A .NET 10 console spike starts `postgres:17` with Testcontainers, creates schemas `tenant_a`, `tenant_b`, `tenant_c`
(tables `orders`, `order_audit`), and runs a use case "create order for tenant X": an EF Core insert in the tenant
schema plus `OrderCreated` published through `IDbContextOutbox` with `DeliveryOptions.TenantId`, in one transaction.
A handler on a durable local queue writes an `order_audit` row. Wolverine runs in `DurabilityMode.Solo`, Eager EF Core
transactions, `AutoApplyTransactions`, `UseDurableLocalQueues`, `RetryTimes(3)` on a simulated transient error.

## Results (real output of `dotnet run --project spikes/wolverine-tenant-outbox -c Release`)

| Check | Result |
|---|---|
| a. Rollback after publish: envelope was in the transaction, nothing stored or handled after rollback | PASS |
| b. Commit: envelope stored (seen from another connection before flush), handled exactly once | PASS |
| c. 240 messages, 3 tenants, 24 concurrent publishers: 0 rows in a wrong schema, 0 missing or duplicated, 0 tables in `public`, 0 handler tenant mismatches | PASS |
| d. Committed but not flushed, host stopped, new host on same database: delivered after 4.8 s and 6.3 s (two runs) in the right schema | PASS |
| e. Handler fails on attempt 1, succeeds on attempt 2, one audit row in the right schema | PASS |
| f. Tenant reaches the handler from the envelope; DbContext resolved for the right schema without handler code | PASS |
| B. Envelope tables per tenant schema | NOT RUN (see Findings) |

## Findings

- Packages: WolverineFx, WolverineFx.EntityFrameworkCore, WolverineFx.Postgresql, WolverineFx.RuntimeCompilation 6.64.2
  (latest stable; 7.0 is alpha), Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, Testcontainers.PostgreSql 4.17.0,
  .NET SDK per `global.json`. Wolverine 6.64.2 ships a `net10.0` target and depends on EF Core 10.0.4.
- Per the Wolverine 6 migration guide, Roslyn code generation moved to `WolverineFx.RuntimeCompilation`; without it
  `TypeLoadMode.Dynamic` fails at startup (the spike references it; the failure itself was not reproduced).
- The docs (wolverinefx.net, EF Core multi-tenancy and PostgreSQL multi-tenancy pages) describe database per tenant and
  conjoined tenancy only. They say nothing about schema per tenant.
- B is blocked by the library: every tenant store gets the same `EnvelopeStorageSchemaName` (source,
  `PostgresqlTenantedMessageStore`). Probe: `RegisterStaticTenants` with a per-tenant `search_path` started, but created
  all 9 Wolverine tables only in `wolverine`, none in tenant schemas. So "tenants" would share one set of tables anyway.
- The tenant travels as `Envelope.TenantId`. The incoming envelope table has no `tenant_id` column; the tenant is kept in
  the stored envelope and survived the restart in check d. The handler receives it as `JasperFx.MultiTenancy.TenantId`.
- Wolverine treats a DbContext as multi-tenanted when an `IDbContextBuilder<T>` is registered and then calls
  `BuildAndEnrollAsync(MessageContext)` per message. The spike registers its own builder that picks a per-tenant
  `NpgsqlDataSource` (`search_path` = tenant schema). This interface is in `Wolverine.EntityFrameworkCore.Internals`:
  public but not a documented extension point, so a Wolverine upgrade can break it.
- The EF model has no schema; tables resolve through `search_path`. `BuildForMain` points at a schema that does not exist,
  so a write without a tenant fails instead of landing in `public`.
- Not tested: `Balanced` durability mode with several nodes, RabbitMQ, inbox "handled" marking in the same transaction as
  the handler's writes, PgBouncer. Expected (untested): with RabbitMQ the outbox still stores outgoing envelopes in
  `wolverine` and the tenant id travels as a message header; with several instances, recovery of a dead node's
  envelopes depends on node reassignment and takes longer than the 5 to 6 s seen in Solo mode. `search_path` on the
  connection is unsafe behind PgBouncer in transaction pooling mode.

## Decision

Use layout A: one Wolverine message store, envelope tables in the shared `wolverine` schema of the application database.

## Consequences for the template

- Envelope tables live only in `wolverine`; never create Wolverine tables in tenant schemas or in `public`.
- The tenant travels only as the Wolverine tenant id: publish with `DeliveryOptions { TenantId = ... }` (or
  `InvokeForTenantAsync`); cascaded messages inherit it. Handlers read it by taking a `TenantId` parameter.
- Handlers take the tenant DbContext as a parameter and never build it. One `IDbContextBuilder<T>` per DbContext
  maps the tenant id to a per-tenant `NpgsqlDataSource` from an allow-list of tenants; unknown tenants throw.
- Outside handlers, use `IDbContextOutbox.Enroll(db)` with a DbContext from the same builder, and
  `SaveChangesAndFlushMessagesAsync`.
- Pin the Wolverine version, and keep an integration test that covers checks a to f, because the builder relies on an
  internal namespace.
- Reference `WolverineFx.RuntimeCompilation`, or pre-generate code and use `TypeLoadMode.Static`.

## Open point against ADR 0001

The spike gave each tenant its own `NpgsqlDataSource` with the schema in the connection string. ADR 0001 measured
that this opens one connection pool per tenant and rules it out for the API. The template must build the handler's
DbContext on the one shared data source and set `search_path` with the interceptor of ADR 0001. That combination
was not tested here; it is a task in `PLAN.md` (phase 4) and has to pass checks a to f before it is relied on.
