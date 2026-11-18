# ADR 0005. Shared building blocks as separate projects

## Context

Both architectures need the same base code: entity and aggregate base types, the error model, handler
registration, and later the web and infrastructure plumbing. In `modular` this code cannot live in a module,
because every module needs it and modules must not depend on each other's internals. In `clean` it could live in
the existing layers.

## Decision

The base code lives in building-block projects under `src/BuildingBlocks/`, with the same names and the same path
in both architectures: `MyApp.BuildingBlocks.Domain`, `MyApp.BuildingBlocks.Application`, and later
`MyApp.BuildingBlocks.Web` and an infrastructure one. Each business layer references the building block of its
own layer: Domain references the domain building block, Application the application one.

Reasons:

- One code path. The template maintains one copy of each base type, tested once, instead of a `clean` copy and a
  `modular` copy that drift apart.
- Moving from `clean` to `modular` does not touch the base code; only the business projects move.
- Extraction to NuGet packages later is mechanical: each building block is already a project with its own
  references and no knowledge of the application.

## Rejected alternative

Base types inside the `clean` layers (`Entity` in `MyApp.Domain`, handler registration in `MyApp.Application`).
It saves projects in `clean`, but `modular` would need a different home for the same code, the two architectures
would stop sharing one implementation, and the guide to move from `clean` to `modular` would have to relocate it.

## Consequences

- `clean` gets three more production projects once the web building block lands, plus their test project. ADR
  0003 measured the build cost of project count as small; the re-measurement planned there includes them.
- A building block never references a business assembly, of any layer or module. It depends only on the base
  class library, on other building blocks of an inner layer and on framework packages its layer allows. The
  architecture tests enforce this.
- A building block holds only code that every module or layer needs today. Code used by one feature stays in that
  feature.
