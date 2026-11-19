# ADR 0012: A database role per tenant

Status: accepted. Date: 2026-11-19.

## Context

Tenant isolation rested on `search_path` while one role owned every schema. Review measured that schema-qualified SQL
from tenant A's context read tenant B's rows, and that a table created in the shared `extensions` schema through one
tenant was read through another. The project owner decided (2026-11-18) that PostgreSQL itself must refuse both.

## Decision

1. Each tenant has a `NOLOGIN` role named `t<database oid>_<id as 32 hex digits>` (`TenantRoleName.For`), from the
   database's `pg_database.oid` and the id only, never the slug; at most 44 characters. Roles belong to the cluster,
   so the oid scopes them to one database: a second database, or a second migrator login on another database, never
   meets the first one's roles, and a login of one database cannot reach another's catalog through a shared role.
   The migrator reads the oid once per run; the guard computes the name in its bind statement from the oid it reads
   there. It differs from the schema name, so `"$user"` in the default path never resolves to the schema. Names reach
   SQL only after their pattern check (`^t[1-9][0-9]{0,9}_([0-9a-f]{32}|shared)$`), quoted. On every run, for each
   tenant role and the shared role (whoever created it, a concurrent run included), the migrator stops, naming the role
   and the row, when the role has `LOGIN`, `SUPERUSER`, `BYPASSRLS`, `CREATEROLE`, `CREATEDB` or `REPLICATION`, or a
   `pg_auth_members` row other than: the creator's row (`current_user` `WITH ADMIN`, granted by the bootstrap
   superuser, as PostgreSQL 16 records it), and rows without `ADMIN` granted by `current_user` (or by the bootstrap
   superuser when `current_user` is a superuser) that the migrator establishes: tenant role in the shared role, the
   application login in a tenant role `WITH INHERIT FALSE, SET TRUE`, a tenant role of the same database in the shared
   role. A role precreated by another login, which keeps `ADMIN`, is never adopted.
2. Grants, not ownership. The migrator's login owns schemas and tables. Under the tenant's lock, on every run, it
   creates the role if missing (a concurrent creation, SQLSTATE 42710, is tolerated) and grants it
   `USAGE` on its schema (no `CREATE`), DML on all its tables and `USAGE, SELECT` on its sequences, the same through
   `ALTER DEFAULT PRIVILEGES` for what later migrations create, and membership in the database's shared role.
3. `t<database oid>_shared` (`TenantRoleName.SharedFor`), a group role granted once per run under the global
   extensions lock (concurrent grants on one table collide on its ACL): `USAGE` on `extensions`, and `USAGE` plus `SELECT` on the tables tenant models exclude from their
   migrations (the catalog tables `AccessDbContext` reads), when they exist. Read only.
4. The application login is a member of each tenant role `WITH INHERIT FALSE, SET TRUE` (PostgreSQL 16 or later; the
   migrator checks the version). `TenantMigratorOptions.ApplicationRole`, bound from `Migrator:ApplicationRole`, names
   that login; empty means the migrator's own login (one-login development).
5. The connection guard's single bind statement, evaluated left to right, checks the login's session defaults, sets
   the role with `set_config('role', ..., false)` only when the role exists and the login may set it, sets
   `search_path` (tenant schema, `extensions`, then `pg_temp` last so a temporary table cannot shadow a tenant table),
   then reads `current_user`, the role's right on `extensions` and `current_schema()`. It refuses, always, a login
   that inherits the tenant role (`pg_has_role(session_user, role, 'USAGE')`), and a superuser or `BYPASSRLS` login
   unless `Tenancy:AllowPrivilegedLogin` (`TenancyOptions`, default false; true in `appsettings.Development.json`).
   A missing role says "run the migrator"; a missing membership says "set Migrator:ApplicationRole and run the migrator". The open
   fails closed and the connection is closed.
6. The migrator's tenant scope is set with the internal `CurrentTenant.InitializeForMigration`: its connections keep
   the migrator's login (role `none`). Every other `ICurrentTenant` assumes the role.
7. Pool reset (`DISCARD ALL`, or `SET SESSION AUTHORIZATION DEFAULT` and `RESET ALL`) clears the role; `No Reset On
   Close` and application SQL that changes the role stay refused.

## Consequences

- Deploy order: run the migrator before an API version that assumes roles; until then tenants created before roles
  fail to open. Migrating again changes nothing.
- Compatibility: `ApplicationRole` used to receive only `USAGE` on `extensions`; that grant now goes to
  the database's shared role, and the named login receives the tenant role memberships instead. A login granted `USAGE` on
  `extensions` by an earlier version keeps it; revoke it by hand if wanted.
- It stops mistakes, not hostile SQL: the login can `RESET ROLE` or set another tenant's role. Only a login per tenant
  would be a hard boundary, rejected for cost (one server connection per tenant).
- Tenant roles can write their module history tables, and `pg_namespace` still lists every schema.
- Every tenant role reads all rows of the shared catalog tables the tenant contexts map: schema-qualified SQL from one
  tenant reads the other tenants' tenant, plan and subscription rows.
- A restored or copied database gets a new oid, so its role names change: run `migrate` after a restore. The old
  oid's roles are orphaned and can be dropped.
- The migrator login that created the roles holds `ADMIN` on them and granted their memberships. Rotating to another
  login means a superuser drops the roles and the new login creates them again; keep one migrator login per database.
- Tenant code cannot write catalog tables. If a tenant context needs to, that is a new grant, decided by the owner.
- Out of scope: dropping roles (deprovisioning), a login per tenant, RLS, hiding `pg_namespace`, revoking writes on
  history tables, PgBouncer.
