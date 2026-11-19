# Pinotechrd.Templates build plan

Working plan for the `pinotechrd-app` template. A task is checked only when it is merged to `develop` and its checks pass. Each task is one pull request unless noted.

## Target

```
dotnet new pinotechrd-app -n MyApp --architecture clean | modular
                                   --module       <Name>   (modular only)
                                   --profile      standalone | distributed
                                   --deploy       compose | kubernetes
                                   --iac          none | terraform
                                   --cloud        gcp | azure | aws
```

Defaults: `clean`, module `Core`, `standalone`, `compose`, no Terraform, no cloud.

| Option | What it decides |
| --- | --- |
| `--architecture clean` | One bounded context. Projects: Domain, Application, Contracts, Resources, Infrastructure, Api. |
| `--architecture modular` | Modular monolith. Each module is a bounded context with its own Domain, Application, Infrastructure, Endpoints and a public Contracts project; one Host composes them. `--module <Name>` names the one module generated (a PascalCase C# identifier, default `Core`); further modules are added later with a script. The template engine cannot generate one module per name of a list (ADR 0006). |
| `--profile` | How it runs: one instance or several. Independent of the architecture. |

Both architectures share the same building blocks (tenancy, errors, i18n, outbox, cache, telemetry), the same frontend and the same delivery files. Only the layout of the business code differs.

## Decisions already made

- Security comes from the `Pinotechrd.Security` NuGet package: JWT with refresh tokens and one database schema per tenant. The template copies no authentication or permission code.
- i18n in the core and in the UI, English and Spanish from the start. The API returns a stable error `code`; the UI translates it.
- Two backend architectures behind `--architecture`: `clean` (one set of layers) and `modular` (modular monolith with clean architecture inside each module). Minimal APIs, one file per use case, in both.
- Modular rules: a module references only the `Contracts` of another module; each module owns its tables, its DbContext and its migrations; no foreign keys or joins across modules; modules talk through contract interfaces or integration events.
- Frontend: Vue 3.5, Vite, TypeScript strict, PrimeVue 4, Pinia, vue-i18n, zod, modules per feature.
- Messaging and outbox with Wolverine. Level 2 cache with Redis in the `distributed` profile.
- First cloud provider for Terraform: GCP. Azure and AWS come later behind the same `--cloud` option.
- MIT license for this repository and for the generated project.
- PWA support is included by default.
- Shared building blocks are separate projects (`BuildingBlocks.Domain`, `.Application`, `.Infrastructure`, `.Web`) at the same path in both architectures ([ADR 0005](docs/adr/0005-shared-building-blocks.md)).
- One error model: a `Result` for every expected failure, exceptions only for defects ([ADR 0004](docs/adr/0004-result-for-expected-failures.md)). Use cases are plain `*Handler` classes with `HandleAsync`.
- The generated project targets .NET 10, the LTS release and the one `Pinotechrd.Security` targets.
- Every profile: access token in memory and refresh token in an `HttpOnly` cookie, refresh tokens stored in the database, migrations as a separate deploy step, background work safe for several instances.

## Quality bar

The template must carry the best code we can justify, not the code we happen to have. Each rule below names the source it comes from. A design choice that departs from its source is recorded in an ADR that says why.

| Area | Rule the template follows | Source |
| --- | --- | --- |
| Architecture | Dependencies point inward; the domain knows nothing about frameworks or the database. | R. C. Martin, *Clean Architecture*; A. Cockburn, "Hexagonal Architecture" |
| Domain model | Entities protect their own invariants; no public setters, no anemic model. | E. Evans, *Domain-Driven Design*; V. Vernon, *Implementing Domain-Driven Design* |
| Modular monolith | Modules are bounded contexts with enforced boundaries and their own data; the database is not the integration layer. | M. Richards and N. Ford, *Fundamentals of Software Architecture* (2nd ed.); S. Brown, "The Missing Chapter" in *Clean Architecture*; K. Grzybek, "Modular Monolith" series |
| Starting point | Start with the simplest structure and split when the domain shows where. | M. Fowler, "MonolithFirst" |
| Module design | Deep modules with small interfaces; complexity is hidden, not spread. | J. Ousterhout, *A Philosophy of Software Design* |
| Code hygiene | Small functions, intention-revealing names, refactor before adding. | M. Fowler, *Refactoring*; R. C. Martin, *Clean Code* |
| HTTP API | Standard status codes, conditional requests with ETag, errors as problem details. | RFC 9110; RFC 9457 |
| Tokens | Short-lived access tokens, rotated refresh tokens, no tokens in browser storage. | RFC 9700 (OAuth 2.0 Security BCP); RFC 8725 (JWT BCP); IETF "OAuth 2.0 for Browser-Based Applications" |
| Application security | Every control maps to a verification requirement; deny by default. | OWASP ASVS; OWASP Top 10; OWASP Cheat Sheet Series |
| Multi-tenancy | Tenant identity comes only from a verified token; isolation is tested, not assumed. | Microsoft Azure Architecture Center, multitenancy guidance; AWS Well-Architected SaaS Lens |
| Messaging | State change and message are committed together (transactional outbox); handlers are idempotent. | G. Hohpe and B. Woolf, *Enterprise Integration Patterns*; C. Richardson, *Microservices Patterns* |
| Data and consistency | Explicit choices on concurrency, retries and idempotency. | M. Kleppmann, *Designing Data-Intensive Applications* |
| Resilience | Timeouts on every outbound call, circuit breakers, bulkheads, bounded queues. | M. Nygard, *Release It!* |
| Operability | Configuration in the environment, stateless processes, logs as event streams. | *The Twelve-Factor App*; Google, *Site Reliability Engineering* |
| Observability | Traces, metrics and logs with standard names. | OpenTelemetry semantic conventions |
| Testing | Tests verify behavior through public interfaces; integration tests use the real database; mocks only at system boundaries. | V. Khorikov, *Unit Testing Principles, Practices, and Patterns*; S. Freeman and N. Pryce, *Growing Object-Oriented Software, Guided by Tests* |
| Delivery | Small changes, trunk kept releasable, every change through the same automated gate. | N. Forsgren, J. Humble and G. Kim, *Accelerate* |
| Supply chain | Pinned actions, dependency review, provenance for published packages. | SLSA; OpenSSF Scorecard |
| Frontend structure | Views are containers, components are presentational; composition over inheritance. | Vue.js Style Guide; B. Frost, *Atomic Design* |
| Accessibility | Every screen passes automated checks and keyboard navigation. | WCAG 2.2 |
| .NET conventions | Naming, nullability and API shape follow the platform guidelines. | K. Cwalina et al., *Framework Design Guidelines*; Microsoft .NET and ASP.NET Core documentation |

How the bar is enforced:

- Each pull request states which rule it implements and gets an independent review before merge.
- Rules that can be checked by a machine are checked by one: analyzers, architecture tests, contract tests, lint, accessibility tests.
- Each phase ends with a review of its code against the rules of its area; findings become tasks in this plan.

## Phase 0. Repository foundation

- [x] Create the repository, `develop` as default branch, `main` for releases.
- [x] Branch protection: pull request required, no force push, no deletion.
- [x] Packaging project `Pinotechrd.Templates.csproj` (`PackageType` Template) and the `templates/pinotechrd-app` folder with a minimal `template.json`.
- [x] Template test harness: install the template, generate a project, build it and run its tests.
- [x] CI for this repository: harness, CodeQL, dependency review, pinned actions, Dependabot. Add the checks to the branch protection.

## Phase 0b. Spikes

Short experiments that test decisions with no outside evidence. Each one ends with a written result in `docs/adr/`; a failed spike changes the decision before anything is built on it.

- [x] Migrations per module and per tenant: three modules and fifty tenant schemas. Measure the time and confirm a migration history per module works inside a tenant schema (tables prefixed by module). Result: the design works, see [ADR 0001](docs/adr/0001-tenant-module-migrations.md).
- [x] Wolverine outbox with one schema per tenant: confirm messages are stored and delivered in the right tenant and never cross tenants. Result: works with the envelope tables in one shared schema, see [ADR 0002](docs/adr/0002-wolverine-outbox-with-tenant-schemas.md).
- [x] Cost of `modular` against `clean` for a minimal project: number of projects, build time and steps to add one endpoint. Result: small build cost, `clean` stays the default, see [ADR 0003](docs/adr/0003-cost-of-modular-against-clean.md).

- [x] Two architectures from one template source: file exclusion by architecture, conditional regions in the solution, a project file and the README, and a module name that renames files and folders. Result: all of it works as planned, see [ADR 0006](docs/adr/0006-two-architectures-one-template-source.md).

## Phase 1. Backend core

- [x] `clean` architecture: solution, the six projects and `Directory.Build.props` (warnings as errors, nullable, analyzers), central package management, `global.json`, `.editorconfig`.
- [x] Architecture test that enforces the dependency direction between layers.
- [x] Supply chain in the generated project: package lock files with locked restore in CI, and an explicit decision on NuGet audit warnings as errors (review of the skeleton). Result: every project writes a lock file on its first restore (the template ships none), CI builds restore in locked mode, and NuGet audit findings are warnings locally and errors in CI, see [ADR 0007](docs/adr/0007-lock-files-and-nuget-audit.md).
- [x] `--architecture` selects content: `clean` keeps the layer projects, `modular` keeps the Host and the building blocks. The database plumbing is the shared building block `BuildingBlocks.Infrastructure`.
- [x] Building blocks, part 1: domain base types, the `Result` error model and handler registration.
- [x] Building blocks, part 2: the web building block (with the API host task below).
- [x] `modular` architecture: Host, module layout (`--module <Name>`, five projects per module), composition of modules through `IModule`, routes under `/api/v1/{module}`.
- [x] Architecture tests for `modular`: a module sees only other modules' `Contracts`; layer rules inside a module; the Host composes exactly the modules on disk.
- [ ] Architecture tests for `modular`: no cross-module table access (needs persistence, Phase 2).
- [x] `modular`: every project that maps endpoints calls `AddValidation()` itself (the built-in validation only covers the project that calls it), with a test that an invalid body is rejected in each module.
- [x] Re-measure the cost of `modular` against `clean` with the real generated projects and update ADR 0003. Result: with one module both have 13 projects and no measurable build-time difference; each further module adds six projects and four edits outside the module; `clean` stays the default, see [ADR 0003](docs/adr/0003-cost-of-modular-against-clean.md).
- [x] API host: automatic endpoint registration, URL versioning (`/api/v1`), health checks (live and ready).
- [x] Errors: ProblemDetails with stable codes, the code catalogue and the test that keeps it in sync.
- [x] OpenAPI contract generated at build, frozen in `contract/openapi.v1.json`, with the test that fails when it is stale.
- [x] Cross-cutting: security headers, CORS, rate limiting, forwarded headers trusted only from configured proxies, settings validated at start, no secrets in `appsettings.json`. The pipeline order, authentication included, is fixed inside the web building block.
- [x] Logging and telemetry: Serilog and OpenTelemetry. No header, query string, body or client address in logs or traces; routes carry opaque identifiers only.
- [x] PostgreSQL connection that fails fast when it is not configured, the readiness check, and the integration test fixture with Testcontainers. The tests need Docker.
- [x] Phase review against the quality bar (architecture, API, operability); findings added as tasks.
- [ ] Inbound request timeout (`AddRequestTimeouts`) and an explicit request body size limit, documented in the README.
- [ ] One style for settings validation (today three: DataAnnotations with `IValidatableObject`, `IValidateOptions` classes, an eager exception in the log levels), recorded in an ADR or the README.
- [ ] Move `UseApiPipeline` out of the security registration into its own class (it composes problems, telemetry and auth); one receiver for registration methods (`IHostApplicationBuilder` or `IServiceCollection`); call `AddHealth()` once.
- [ ] Trim public surface used only by tests: `UseProblems`, the two-argument `BootstrapLogger.WriteFatal`, the CORS constants.
- [ ] Test that runs the API as a real process with the README's local settings (launch profile, user secrets, local PostgreSQL) and probes live, ready, version and the OpenAPI document.
- [ ] Replace wall-clock bounds in the start-up and readiness tests, and the busy-wait in the telemetry capture.
- [ ] `BuildingBlocks.Tests` covers only Domain and Application: add tests for the Infrastructure and Web building blocks.
- [ ] List the tests that read source text and state the policy (no new ones).

## Phase 2. Security and tenancy

- [x] Spike: `Pinotechrd.Security` on tenant schemas selected by `search_path`. Result: works with a model override in the template, an interceptor on one shared data source and a refresh store that consumes tokens atomically; ten library gaps recorded, see [ADR 0008](docs/adr/0008-security-library-with-tenant-schemas.md).
- [ ] One shared test host base for the API tests (the three factories will each need the JWT settings).
- [x] Web authorization building block: deny by default; every endpoint declares exactly one of `RequirePermission`, `AuthenticatedOnly` or `AllowAnonymous`, checked at start; per-module permission checkers; user and tenant read from authenticated claims only; Bearer scheme in the contract.
- [x] An ordered extension point after authentication in the pipeline, for tenant resolution, the per-user limiter and the user and tenant on the log event.
- [x] A public helper for "running under build-time contract generation" (today private in the database composition), so security settings validated at start do not break the contract generation.
- [x] The security DbContext uses the one shared data source, not a data source of its own.
- [x] Wire `Pinotechrd.Security` (`1.2.0-preview.3`) in an `Access` module present in both architectures: `AddPinotechrdSecurity`, the DbContext on `PinotechrdSecurityDbContext` in `search_path` mode on the tenant persistence, the permission checker of `Access` on the library's engine, the request's tenant copied into the library's context; `UseTenantResolution` is not used. See [ADR 0009](docs/adr/0009-access-module-and-security-wiring.md).
- [x] The `search_path` interceptor tolerates the tenant-less readiness probe, and the tracing filter that matches the text `SELECT 1` no longer hides application queries.
- [x] Schema per tenant: schema resolver and a connection interceptor that sets `search_path` on one shared data source. No schema in the model or in the migrations, so no model cache key per schema (ADR 0001). With PgBouncer in transaction pooling mode the schema must be set with `SET LOCAL` inside the transaction, or session pooling used.
- [x] A migration contract outside the web building block, so a migrator does not pull in ASP.NET Core.
- [x] Shared (catalog) migrations: a registration and a migrator entry point for a context that is not tenant-scoped, run once before the tenants under a global lock (shape described in the review of the tenant persistence unit). See [ADR 0010](docs/adr/0010-migrator-and-catalog-migrations.md).
- [x] Migrations: shared and per tenant, run by a separate migrator command, never at API start. `src/MyApp.Migrator`, with the initial Access and catalog migrations (ADR 0010).
- [x] Tenant provisioning: the catalog row, then the schema created and migrated, idempotent by slug and safe under concurrency; `MyApp.Migrator provision`. Seeding stays a task of its own. See [ADR 0011](docs/adr/0011-tenant-provisioning.md).
- [x] A database role per tenant, built with tenant provisioning (decided 2026-11-18 by the project owner on my recommendation). Provisioning creates a `NOLOGIN` role that owns or is granted only its tenant's schema and `USAGE` on `extensions`; the connection guard assumes that role in the same statement that binds the schema and verifies it with `current_user`; the application's login keeps no direct rights on tenant schemas; the migrator holds the administration rights. Why: review measured that with one role owning every schema, schema-qualified SQL reads another tenant and a table written to `extensions` by one tenant is read by another; the role makes PostgreSQL refuse both. Limit, stated in the README: it stops mistakes, not hostile SQL (the shared login can reset the role); the defence against that is having no SQL injection, and only a login per tenant (rejected for cost: one server connection per tenant) would be a hard boundary. See [ADR 0012](docs/adr/0012-role-per-tenant.md).
- [ ] Refresh token store in the database, replacing the in-memory default.
- [ ] One OpenAPI contract per architecture before a module ships endpoints: in place the Host composes the clean layers and the stamp module, so a contract generated there matches neither layout.
- [x] One source of truth for claim types: `Pinotechrd.Security` maps the inbound `sub` claim to `ClaimTypes.NameIdentifier`, while the template reads `sub` (`Identity:UserIdClaimType`) and `tenant_id`; the Access unit makes the token handler and the template read the same claim types. Done: `Identity` decides both, tokens carry `sub`, inbound mapping is off, and a start check refuses a tenant claim type the library does not use (ADR 0009).
- [ ] Data Protection keys: persisted in shared storage and encrypted at rest before the real scheme or the library's two-factor tokens use them (on Linux, keys without an encryptor are stored in clear and logged as a warning at start).
- [ ] Purge refresh tokens: a background job, safe on several instances, deletes families past their absolute expiry or revoked longer than a grace period (every refresh adds a row; nothing deletes them yet).
- [ ] Library: check lockout before consuming the refresh token in `RefreshAsync`, so a lockout caused by someone else's failed sign-ins does not end live sessions (review of U6b, 2026-11-19).
- [ ] Library: count every failed sign-in under concurrency. `CheckPasswordSignInAsync` loses `AccessFailedAsync` updates on `ConcurrencyFailure` (30 parallel wrong passwords left the count at 4, never locked); retry or increment atomically, as the template's change-password does (review of U6c, 2026-11-19).
- [ ] Make the advisory-lock helper one shared building block: `MigratorBootstrap` carries a copy of the internal `AdvisoryLock` try-lock.
- [x] Auth endpoints: login, refresh, logout, change password. Refresh token in an `HttpOnly` cookie. A stricter named rate-limit policy for these endpoints; a per-user or per-tenant limit is a separate limiter placed after authorization.
- [ ] The `Access` module (now in both architectures, ADR 0009): home of the users, roles and permissions endpoints.
- [ ] Users, roles and permissions endpoints, with per-user allow and deny.
- [x] Add the user and tenant (opaque identifiers only) to the request log event and to the trace, after authentication.
- [ ] The API refuses a tenant that is provisioning, suspended or cancelled (status read from the catalog after authentication), so a token for such a tenant never reaches its schema (ADR 0011).
- [ ] Tenants endpoints: create, activate, suspend.
- [x] Seeding: first tenant and bootstrap administrator from configuration. Done: `migrate` seeds plans, modules and permissions; `MyApp.Migrator bootstrap` creates the operator tenant and its administrator, the password from the environment or user secrets only. See [ADR 0013](docs/adr/0013-seeding-and-bootstrap.md).
- [ ] Tests: one tenant cannot read another tenant's data; the tenant schema is set on every connection; every endpoint declares a permission.
- [ ] Phase review against the quality bar (tokens, application security, multi-tenancy); findings added as tasks.

## Phase 3. i18n in the core

- [ ] `Resources` project with `Messages.resx` (English) and `Messages.es.resx` (Spanish).
- [ ] Request culture from `Accept-Language` and from the user's saved preference.
- [ ] Localized error and validation messages.
- [ ] Email abstraction with templates in both languages.
- [ ] Test: both languages have the same keys.
- [ ] Phase review against the quality bar (code hygiene, testing); findings added as tasks.

## Phase 4. Messaging, cache and background work

- [ ] Wolverine with the transactional outbox on PostgreSQL, no broker: envelope tables in the shared `wolverine` schema, tenant carried as the Wolverine tenant id, version pinned (ADR 0002).
- [ ] Handler DbContext built on the one shared data source with the `search_path` interceptor, not one data source per tenant. Must pass the outbox checks of ADR 0002 (atomicity, isolation, durability, retry).
- [ ] Integration tests that keep those outbox checks in the generated project, because the DbContext builder relies on an internal Wolverine namespace.
- [ ] Cache abstraction with tenant-aware keys, in-memory implementation.
- [ ] Background jobs that are safe when more than one instance runs.
- [ ] Phase review against the quality bar (messaging, data, resilience); findings added as tasks.

## Phase 5. Sample feature in the API

- [ ] `modular`: the `Sample` module, home of the sample feature.
- [ ] `Sample` CRUD: paging, validation, optimistic concurrency, a published event, permissions, tests. It is the model to copy for new features.
- [ ] Phase review against the quality bar (domain model, testing); findings added as tasks.

## Phase 6. Frontend

- [ ] Scaffold: Vite, TypeScript strict, ESLint with no warnings allowed, Prettier, Vitest.
- [ ] PWA: installable app, update prompt, offline view.
- [ ] PrimeVue theme, layout shell (top bar, sidebar, language switcher), not-found and forbidden views.
- [ ] i18n plugin with `en` and `es`, catalogues per module loaded by route, key parity test.
- [ ] API client typed from the OpenAPI contract, with the generation script.
- [ ] Auth module: login, logout, change password, access token in memory, silent refresh and one retry on 401.
- [ ] Router guard that denies by default, permission per route, `useCan` and a directive for elements.
- [ ] Shared form layer with zod and translated messages.
- [ ] Shared widgets: table, toolbar, paginator, confirm dialog. Date, number and money formatting by language.
- [ ] `access` module: users, roles, permission matrix.
- [ ] `tenants` and `settings` modules.
- [ ] `sample` module that matches the API sample.
- [ ] End-to-end tests with Playwright, including accessibility checks.
- [ ] Phase review against the quality bar (frontend structure, accessibility); findings added as tasks.

## Phase 7. Distributed profile

- [ ] Redis as level 2 cache with invalidation between instances.
- [ ] Wolverine with RabbitMQ.
- [ ] Wolverine durability with several nodes (balanced mode) and a test that a handled message is marked in the same transaction as the handler's writes; neither was covered by the spike.
- [ ] Permission cache invalidated across instances (needs the upstream change below).
- [ ] Compose file with several API replicas and a test that runs against two instances.
- [ ] Phase review against the quality bar (resilience, data, operability); findings added as tasks.

## Phase 8. Delivery

- [ ] Dockerfiles for the API and the web. The API image copies the lock files and restores with `-p:ContinuousIntegrationBuild=true` (ADR 0007); `RuntimeIdentifiers` set for every project if it publishes for a runtime.
- [ ] Compose deployment: PostgreSQL, API, web, reverse proxy, `.env.example`. The reverse proxy does not expose `/health/*` to the internet (the readiness probe is public, unlimited and runs the dependency checks). It also strips `traceparent`, `tracestate` and `baggage` from public requests.
- [ ] Workflows of the generated project: CI, CodeQL, dependency review, deploy. CI fails when restore leaves the working tree changed (`git status --porcelain` not empty), because locked mode writes a missing lock file instead of failing (ADR 0007).
- [ ] `scripts/verify.ps1`, the same gate CI runs.
- [ ] Kubernetes manifests: deployments, probes, autoscaling, migrator job.
- [ ] Terraform for GCP: network, GKE, Cloud SQL, Memorystore, Artifact Registry, Secret Manager.
- [ ] Phase review against the quality bar (delivery, supply chain); findings added as tasks.

## Phase 9. Template productization

- [ ] `template.json` symbols for `profile`, `deploy`, `iac` and `cloud`, with conditional content.
- [ ] Harness matrix: `clean` and `modular`, `standalone` and `distributed`, `compose` and `kubernetes`.
- [ ] Documentation of the generated project: README and ADRs (the agent files are Phase 9b).
- [ ] `scripts/new-module.ps1` to scaffold a feature (`clean`) or a whole module (`modular`) in the API and the web.
- [ ] Guide to move a `clean` project to `modular` when its domain needs it.
- [ ] Pack and publish to nuget.org with Trusted Publishing; changelog and versioning.

## Phase 9b. Agent harness of the generated project

Tool-neutral base with a Claude Code layer on top. Researched on 2026-11-18; each item says its basis: E (study with data), V (vendor documentation), J (judgement). Most studies are preprints.

- [ ] ADR: the agent harness and its sources, with what could not be verified.
- [ ] `AGENTS.md` under 100 lines: the verify command, conventions and non-obvious rules; no repository overview (E: context files did not raise success and cost over 20% more; overviews did not help. V: read by more than twenty tools).
- [ ] `CLAUDE.md` that imports `AGENTS.md` and adds only Claude-specific notes (V: when `CLAUDE.md` exists, `AGENTS.md` is not read on its own).
- [ ] One verify script (`scripts/verify.ps1` and `verify.sh`) called by the hook, CI, a git pre-push hook and people (J).
- [ ] `.claude/agents`: a read-only `code-reviewer` and a `test-writer` that never edits production code; a `security-reviewer` on demand (E, weak: tests written after faulty code catch about 14% of faults against 25% for independent ones; a separate evaluator helps on hard tasks). No architecture-validator agent: the rules are architecture tests (J; no study compares the two).
- [ ] `.claude/skills`: add an endpoint, add a module, add a migration, regenerate the contract; release and migration not invocable by the model (V for the format; J for the set).
- [ ] `.claude/settings.json`: deny reads of `.env` and secrets; a Stop hook that runs verify with a loop guard; a PreToolUse block on the frozen contract and the lock files; commands that work on Windows and Linux (V, J).
- [ ] Path-scoped rules for backend, frontend and tests (V).
- [ ] What the template never ships: MCP servers or their auto-approval, auto-allowed shell commands, environment overrides, third-party skills or plugins, hooks that fetch remote scripts (E: published attacks through repository-supplied configuration).
- [ ] Optional agent review workflow on pull requests, capped in turns, not on forks, told to skip what the deterministic checks already cover (V; E: about a third of automated review comments are accepted).
- [ ] Harness: the generated project contains the harness files, the hook commands run on both platforms, and no forbidden setting is present.

## Phase 10. Adoption

- [ ] Generate a pilot project and run it end to end.
- [ ] Feed back what the pilot shows into the template.

## Upstream work in Pinotechrd.Security

These block the tasks that reference them and are tracked in that repository.

- [ ] Permission cache that can be invalidated across instances (today it is in memory, fifteen minutes per node).
- [ ] Raise test coverage above the current 46% before the first stable release.
- [ ] Publish the stable `1.2.0` the template will reference.
