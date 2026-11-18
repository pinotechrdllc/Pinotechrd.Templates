# Pinotechrd.Templates build plan

Working plan for the `pinotechrd-app` template. A task is checked only when it is merged to `develop` and its checks pass. Each task is one pull request unless noted.

## Target

```
dotnet new pinotechrd-app -n MyApp --architecture clean | modular
                                   --profile      standalone | distributed
                                   --deploy       compose | kubernetes
                                   --iac          none | terraform
                                   --cloud        gcp | azure | aws
```

Defaults: `clean`, `standalone`, `compose`, no Terraform, no cloud.

| Option | What it decides |
| --- | --- |
| `--architecture clean` | One bounded context. Projects: Domain, Application, Contracts, Resources, Infrastructure, Api. |
| `--architecture modular` | Modular monolith. Each module is a bounded context with its own Domain, Application, Infrastructure, Endpoints and a public Contracts project; one Host composes them. `--modules A,B` names the initial modules. |
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
- Shared building blocks are separate projects (`BuildingBlocks.Domain`, `.Application`, `.Web`, later `.Infrastructure`) at the same path in both architectures ([ADR 0005](docs/adr/0005-shared-building-blocks.md)).
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

## Phase 1. Backend core

- [x] `clean` architecture: solution, the six projects and `Directory.Build.props` (warnings as errors, nullable, analyzers), central package management, `global.json`, `.editorconfig`.
- [x] Architecture test that enforces the dependency direction between layers.
- [ ] Supply chain in the generated project: package lock files with locked restore in CI, and an explicit decision on NuGet audit warnings as errors (review of the skeleton).
- [ ] `--architecture modular` must not silently generate the `clean` skeleton: select content by architecture in `template.json` (review of the skeleton).
- [x] Building blocks, part 1: domain base types, the `Result` error model and handler registration.
- [x] Building blocks, part 2: the web building block (with the API host task below).
- [ ] `modular` architecture: Host, module layout, composition of modules, the `Access` and `Sample` modules.
- [ ] Architecture tests for `modular`: a module sees only other modules' `Contracts`; no cross-module table access.
- [ ] `modular`: every project that maps endpoints calls `AddValidation()` itself (the built-in validation only covers the project that calls it), with a test that an invalid body is rejected in each module.
- [ ] Re-measure the cost of `modular` against `clean` with the real generated projects and update ADR 0003.
- [x] API host: automatic endpoint registration, URL versioning (`/api/v1`), health checks (live and ready).
- [x] Errors: ProblemDetails with stable codes, the code catalogue and the test that keeps it in sync.
- [x] OpenAPI contract generated at build, frozen in `contract/openapi.v1.json`, with the test that fails when it is stale.
- [ ] Cross-cutting: security headers, CORS, rate limiting, forwarded headers, options validated at start, no secrets in `appsettings.json`. CORS runs before the error handling middleware, and `rate_limited` (429) joins the code catalogue.
- [ ] Logging and telemetry: Serilog and OpenTelemetry.
- [ ] Integration test fixture with Testcontainers for PostgreSQL.
- [ ] Phase review against the quality bar (architecture, API, operability); findings added as tasks.

## Phase 2. Security and tenancy

- [ ] Wire `Pinotechrd.Security`: `AddPinotechrdSecurity`, `UseTenantResolution`, the DbContext on `PinotechrdSecurityDbContext`.
- [ ] Schema per tenant: schema resolver and a connection interceptor that sets `search_path` on one shared data source. No schema in the model or in the migrations, so no model cache key per schema (ADR 0001).
- [ ] Migrations: shared and per tenant, run by a separate migrator command, never at API start.
- [ ] Tenant provisioning: create the schema, migrate it and seed it.
- [ ] Refresh token store in the database, replacing the in-memory default.
- [ ] Auth endpoints: login, refresh, logout, change password. Refresh token in an `HttpOnly` cookie.
- [ ] Users, roles and permissions endpoints, with per-user allow and deny.
- [ ] Tenants endpoints: create, activate, suspend.
- [ ] Seeding: first tenant and bootstrap administrator from configuration.
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

- [ ] Dockerfiles for the API and the web.
- [ ] Compose deployment: PostgreSQL, API, web, reverse proxy, `.env.example`.
- [ ] Workflows of the generated project: CI, CodeQL, dependency review, deploy.
- [ ] `scripts/verify.ps1`, the same gate CI runs.
- [ ] Kubernetes manifests: deployments, probes, autoscaling, migrator job.
- [ ] Terraform for GCP: network, GKE, Cloud SQL, Memorystore, Artifact Registry, Secret Manager.
- [ ] Phase review against the quality bar (delivery, supply chain); findings added as tasks.

## Phase 9. Template productization

- [ ] `template.json` symbols for `profile`, `deploy`, `iac` and `cloud`, with conditional content.
- [ ] Harness matrix: `clean` and `modular`, `standalone` and `distributed`, `compose` and `kubernetes`.
- [ ] Documentation of the generated project: README, ADRs, `AGENTS.md`, `CLAUDE.md`.
- [ ] `scripts/new-module.ps1` to scaffold a feature (`clean`) or a whole module (`modular`) in the API and the web.
- [ ] Guide to move a `clean` project to `modular` when its domain needs it.
- [ ] Pack and publish to nuget.org with Trusted Publishing; changelog and versioning.

## Phase 10. Adoption

- [ ] Generate a pilot project and run it end to end.
- [ ] Feed back what the pilot shows into the template.

## Upstream work in Pinotechrd.Security

These block the tasks that reference them and are tracked in that repository.

- [ ] Permission cache that can be invalidated across instances (today it is in memory, fifteen minutes per node).
- [ ] Raise test coverage above the current 46% before the first stable release.
- [ ] Publish the stable `1.2.0` the template will reference.
