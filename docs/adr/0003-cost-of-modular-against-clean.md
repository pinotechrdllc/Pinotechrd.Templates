# ADR 0003. Cost of the modular architecture against clean

## Context

The template offers two backend architectures: `clean` (one set of layers) and `modular` (a modular monolith with
clean architecture inside each module). The recommendation to keep `clean` as the default rested on judgment, not
on a measurement. This spike measures what `modular` costs for a minimal project.

## What was tested

`spikes/architecture-cost/measure.ps1` scaffolds both skeletons with real project references and one type per
project, then measures the build of each. No business code, no packages beyond the project templates.

- `clean`: Domain, Contracts, Resources, Application, Infrastructure, Api and two test projects.
- `modular`: three building-block projects, a Host, an architecture test project and, for each module, Contracts,
  Domain, Application, Infrastructure, Endpoints and a test project.

Each build was repeated five times after a warm-up; the table shows the median.

## Results

Windows 11, 8 logical CPUs, .NET SDK 10.0.402, 2026-11-18.

| Architecture | Projects | Cold build (s) | No change (s) | After a domain change (s) |
| --- | --- | --- | --- | --- |
| clean | 8 | 5.2 to 6.0 | 2.6 to 5.2 | 2.6 to 4.5 |
| modular, 2 modules | 17 | 6.7 | 3.7 | 3.5 |
| modular, 4 modules | 29 | 9.0 | 5.1 | 5.3 |

The `clean` row is a range because it was measured in both runs and the two disagreed; the machine was not idle.
Treat every number as accurate to about two seconds.

## Findings

- Project count grows by six per module: 17 projects with two modules, 29 with four, against 8 for `clean`.
- Build time grows with it, but slowly: a cold build goes from about 5 or 6 seconds to about 7 with two modules
  and 9 with four. With two modules the incremental build is not measurably slower than `clean`; with four it is
  roughly twice as slow, about 5 seconds.
- Adding an endpoint touches the same number of files in both: a contract, a use case and an endpoint. The
  difference is where they live, not how many there are.
- The real extra cost of `modular` is structural and was not measured in seconds: a new module is six projects
  plus its registration in the Host, and a call from one module to another needs an interface in the callee's
  Contracts project. Done by hand, both are slow and easy to get wrong.
- Not measured: builds with real code and packages, test run time, IDE load time, and how the cost grows past
  four modules.

## Decision

Keep both architectures. Keep `clean` as the default: it has less than half the projects and nothing in this
measurement argues for paying the modular structure before the domain needs it. The build-time cost of `modular`
is small enough that it is not a reason to avoid it when a system has several business areas.

## Consequences for the template

- `scripts/new-module.ps1` is required for `modular`, not optional: creating six projects and wiring them by hand
  is the main cost this spike found.
- The guide to move a `clean` project to `modular` stays in the plan, so starting with `clean` is not a dead end.
- Re-measure with the real generated projects once phase 1 is done, and replace this table with those numbers.
