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

## Measured on the generated projects (2026-11-18)

The sections above are the spike as it was written and are kept unchanged. This section replaces its table: the
numbers below come from the projects the template generates today, not from a miniature.

### Method

`spikes/architecture-cost/measure-generated.ps1 -Runs 3` produced every figure in this section. It packs the template
from a copy of the repository sources, installs it in an isolated hive (`--debug:custom-hive`) and generates
`Acme.Demo` in a temporary folder three times:

- `clean`: `dotnet new pinotechrd-app --name Acme.Demo`.
- `modular, 1 module`: the same with `--architecture modular --module Sales`.
- `modular, 2 modules`: the modular output with a second module, `Inventory`, copied from `Sales` by the script the
  way a developer would do it by hand today (see "A second module" below).

For each output it counts projects and files (bin and obj excluded) and times, in the Debug configuration and from
inside the output folder so its `global.json` selects the SDK:

- restore, after `dotnet build-server shutdown` and deleting every bin and obj folder. The NuGet package cache was
  warm, so this is not a first restore on a new machine;
- cold build, `dotnet build --no-restore` right after that restore, with the build servers still stopped;
- a build with no change, twice: right after the cold build, and again;
- the tests that do not need Docker, `dotnet test --no-build` with a filter that excludes the seven test classes in
  the PostgreSQL container collection (`OpenedConnectionSettingsTests`, `DataSourceBehaviorTests`,
  `DatabaseTelemetryTests`, `LiveProbeTests`, `ReadinessFailureModeTests`, `ReadinessTests`, `SpanErrorTests`);
- a build after a change inside the innermost business project that leaves its public surface unchanged (a comment
  appended to the Domain marker file: `DomainAssembly.cs` in `clean`, `SalesDomainAssembly.cs` in `modular`);
- a build after a change of the public surface of that project (a public class appended to the same file).

Each measure ran three times. The runs were interleaved (run 1 of every layout, then run 2, then run 3), so a change
in the machine load hits every layout. Times are wall-clock seconds, including the start of the `dotnet` command.

Machine: Intel Core i7-1165G7 at 2.80 GHz, 4 cores, 8 logical processors, 32 GB RAM, Windows 11 Pro 10.0.26300,
.NET SDK 10.0.402. This was a developer machine with other work running, including other builds: the CPU load sampled
before each run went from 25% to 96%. The spread of the timings below is mostly that load, not the layouts.

### Results

Size of the generated projects:

| Layout | Projects (production + tests) | C# files / lines | Project files / lines | Tests run without Docker |
| --- | --- | --- | --- | --- |
| clean | 10 + 3 | 148 / 14,485 | 16 / 328 | 771 |
| modular, 1 module | 10 + 3 | 156 / 15,384 | 16 / 347 | 905 |
| modular, 2 modules | 15 + 4 | 163 / 15,647 | 22 / 450 | 915 |

Project files are the `.csproj`, `.slnx`, `.props` and `.targets` files. Every test that ran passed in every run.

Times, in seconds, median of three runs with the range in brackets:

| Layout | Restore | Cold build | No change, first | No change | Tests | Body change in Domain | Public change in Domain |
| --- | --- | --- | --- | --- | --- | --- | --- |
| clean | 3.3 (3.1 to 5.2) | 28.5 (24.3 to 39.2) | 10.8 (9.1 to 11.9) | 8.4 (6.1 to 17.3) | 32.5 (30.8 to 34.6) | 8.1 (6.6 to 10.3) | 12.3 (10.0 to 13.8) |
| modular, 1 module | 3.9 (3.1 to 6.1) | 27.2 (25.0 to 30.6) | 8.1 (5.7 to 12.4) | 9.2 (4.5 to 11.8) | 38.6 (33.3 to 45.9) | 6.3 (4.5 to 11.2) | 9.3 (8.8 to 12.4) |
| modular, 2 modules | 3.1 (2.6 to 3.9) | 35.2 (25.3 to 40.9) | 11.5 (8.5 to 21.8) | 11.8 (6.4 to 15.1) | 43.0 (41.7 to 44.3) | 9.4 (6.5 to 15.0) | 10.9 (10.0 to 12.9) |

Ratio of the medians against `clean`:

| Layout | Restore | Cold build | No change, first | No change | Tests | Body change | Public change |
| --- | --- | --- | --- | --- | --- | --- | --- |
| modular, 1 module | 1.18 | 0.95 | 0.75 | 1.10 | 1.19 | 0.79 | 0.76 |
| modular, 2 modules | 0.94 | 1.23 | 1.06 | 1.41 | 1.32 | 1.16 | 0.89 |

### A second module

The copy of `Sales` into `Inventory` built without warnings and all its tests passed. It needed, besides copying the
module folder and its test project and renaming `Sales` to `Inventory` in their folder names, file names and text,
four edits outside the module:

1. the solution: a solution folder with the five module projects, and the test project;
2. the host project: a reference to the module's Endpoints project;
3. the host composition, `BusinessComposition.Modular.cs`: a `using` and one `AddModule<InventoryModule>()` line;
4. the README layout table: one row per new project. The first attempt missed this one, and the generated test
   `ReadmeLayoutTableCoversEveryProjectOnDisk` failed and named the six missing rows.

The name must be replaced only inside the copied module. The generated API tests contain `Sales` as literal test data
(for example `Acme.Modules.Sales.Domain` in the architecture rule tests), so a replacement across the whole solution
would change those tests. The future `new-module` script has to make the same four edits and keep the replacement
inside the module.

A second module added 5 production projects, 1 test project, 7 C# files (263 lines), 6 project files and 103 lines of
project files, and 10 tests.

### Measured facts

- `clean` and `modular` with one module have the same number of projects: 10 production and 3 test projects each.
  The spike's comparison of 8 against 17 set one bounded context against two modules, and its `clean` skeleton had
  no building-block projects.
- Each further module adds six projects (five production, one test), as the spike said.
- Absolute build times are about five times the spike's: a cold build takes 24 to 41 seconds, not 5 to 9. The
  generated projects carry packages, analyzers at the recommended level and code-style enforcement in the build,
  which the spike's empty projects did not.
- With one module, no build time of `modular` is measurably different from `clean`: every median is within 25% of
  `clean`'s and every range overlaps `clean`'s.
- With two modules, the medians of the cold build (1.23) and of the build with no change (1.41) are higher than
  `clean`'s, but the ranges overlap, so three runs on a loaded machine do not show a difference.
- The test run is the one time where the runs separate the layouts, and only with two modules: its median is 1.19
  times `clean`'s with one module (the ranges overlap) and 1.32 with two, where the range (41.7 to 44.3 s) does not
  overlap `clean`'s (30.8 to 34.6 s). The one-module project runs 134 more tests than `clean`; the second module
  added 10.
- Adding a module by hand works, takes four edits outside the module, and the generated tests catch a missed README
  row.

### What changed against the spike

- The project count no longer favours `clean` at the start. The Decision above says `clean` "has less than half the
  projects"; for a generated project with one module that is not true, they are equal. It is true only as modules are
  added: 13 against 19 with two modules.
- The spike's absolute seconds were far too low; its relative conclusion, that build time is not a reason to avoid
  `modular`, holds for one and two modules.
- Test time, which the spike did not measure, grows with `modular`.

### Sources

The spike cited no published source. The plan's quality bar cites M. Fowler, "MonolithFirst", for the starting
point: start with the simplest structure and split when the domain shows where.

### Judgement

The decision stands: both architectures, `clean` the default. Its reason changes. It no longer rests on a measured
cost at the start, because a one-module `modular` project costs the same as `clean` in projects and, within what
these runs can resolve, in build time. It rests on two things: the cost of `modular` appears per module (six
projects, four edits outside the module, longer test runs), and a single bounded context gets nothing from module
boundaries, which is the reason the plan cites from "MonolithFirst". These measurements give no reason to avoid
`modular` for a system that already has several business areas.

The numbers should be measured again on an idle machine, with more runs, before any statement about build time
differences smaller than about 40% is made, and again when modules carry persistence (Phase 2).
