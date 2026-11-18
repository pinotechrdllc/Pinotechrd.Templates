# Pinotechrd.Templates build plan

Working plan for the `pinotechrd-app` template. A task is checked only when it is merged to `develop` and its checks pass. Each task is one pull request unless noted.

## Target

```
dotnet new pinotechrd-app -n MyApp --profile standalone | distributed
                                   --deploy  compose | kubernetes
                                   --iac     none | terraform
                                   --cloud   gcp | azure | aws
```

Defaults: `standalone`, `compose`, no Terraform, no cloud.

## Decisions already made

- Security comes from the `Pinotechrd.Security` NuGet package: JWT with refresh tokens and one database schema per tenant. The template copies no authentication or permission code.
- i18n in the core and in the UI, English and Spanish from the start. The API returns a stable error `code`; the UI translates it.
- Backend layers: Domain, Application, Contracts, Resources, Infrastructure, Api. Minimal APIs, one file per use case.
- Frontend: Vue 3.5, Vite, TypeScript strict, PrimeVue 4, Pinia, vue-i18n, zod, modules per feature.
- Messaging and outbox with Wolverine. Level 2 cache with Redis in the `distributed` profile.
- First cloud provider for Terraform: GCP. Azure and AWS come later behind the same `--cloud` option.
- The generated project targets .NET 10, the LTS release and the one `Pinotechrd.Security` targets.
- Every profile: access token in memory and refresh token in an `HttpOnly` cookie, refresh tokens stored in the database, migrations as a separate deploy step, background work safe for several instances.

## Decisions to confirm

- [ ] License of this repository and of the generated project (`MIT` is assumed, as in `Pinotechrd.Security`).
- [ ] Whether the generated project includes PWA support by default or behind an option.

## Quality bar

The template must carry the best code we can justify, not the code we happen to have. Each rule below names the source it comes from. A design choice that departs from its source is recorded in an ADR that says why.

| Area | Rule the template follows | Source |
| --- | --- | --- |
| Architecture | Dependencies point inward; the domain knows nothing about frameworks or the database. | R. C. Martin, *Clean Architecture*; A. Cockburn, "Hexagonal Architecture" |
| Domain model | Entities protect their own invariants; no public setters, no anemic model. | E. Evans, *Domain-Driven Design*; V. Vernon, *Implementing Domain-Driven Design* |
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
- [ ] Packaging project `Pinotechrd.Templates.csproj` (`PackageType` Template) and the `templates/pinotechrd-app` folder with a minimal `template.json`.
- [ ] Template test harness: install the template, generate a project, build it and run its tests.
- [ ] CI for this repository: harness, CodeQL, dependency review, pinned actions, Dependabot. Add the checks to the branch protection.

## Phase 1. Backend core

- [ ] Solution, the six projects and `Directory.Build.props` (warnings as errors, nullable, analyzers), central package management, `global.json`, `.editorconfig`.
- [ ] Architecture test that enforces the dependency direction between layers.
- [ ] API host: automatic endpoint registration, URL versioning (`/api/v1`), health checks (live and ready).
- [ ] Errors: ProblemDetails with stable codes, the code catalogue and the test that keeps it in sync.
- [ ] OpenAPI contract generated at build, frozen in `contract/openapi.v1.json`, with the test that fails when it is stale.
- [ ] Cross-cutting: security headers, CORS, rate limiting, forwarded headers, options validated at start, no secrets in `appsettings.json`.
- [ ] Logging and telemetry: Serilog and OpenTelemetry.
- [ ] Integration test fixture with Testcontainers for PostgreSQL.
- [ ] Phase review against the quality bar (architecture, API, operability); findings added as tasks.

## Phase 2. Security and tenancy

- [ ] Wire `Pinotechrd.Security`: `AddPinotechrdSecurity`, `UseTenantResolution`, the DbContext on `PinotechrdSecurityDbContext`.
- [ ] Schema per tenant: schema resolver, connection interceptor, model cache key per schema.
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

- [ ] Wolverine with the transactional outbox on PostgreSQL, no broker.
- [ ] Cache abstraction with tenant-aware keys, in-memory implementation.
- [ ] Background jobs that are safe when more than one instance runs.
- [ ] Phase review against the quality bar (messaging, data, resilience); findings added as tasks.

## Phase 5. Sample feature in the API

- [ ] `Sample` CRUD: paging, validation, optimistic concurrency, a published event, permissions, tests. It is the model to copy for new features.
- [ ] Phase review against the quality bar (domain model, testing); findings added as tasks.

## Phase 6. Frontend

- [ ] Scaffold: Vite, TypeScript strict, ESLint with no warnings allowed, Prettier, Vitest.
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
- [ ] Harness matrix: `standalone` and `distributed`, `compose` and `kubernetes`.
- [ ] Documentation of the generated project: README, ADRs, `AGENTS.md`, `CLAUDE.md`.
- [ ] `scripts/new-module.ps1` to scaffold a feature in the API and the web.
- [ ] Pack and publish to nuget.org with Trusted Publishing; changelog and versioning.

## Phase 10. Adoption

- [ ] Generate a pilot project and run it end to end.
- [ ] Feed back what the pilot shows into the template.

## Upstream work in Pinotechrd.Security

These block the tasks that reference them and are tracked in that repository.

- [ ] Permission cache that can be invalidated across instances (today it is in memory, fifteen minutes per node).
- [ ] Raise test coverage above the current 46% before the first stable release.
- [ ] Publish the stable `1.2.0` the template will reference.
