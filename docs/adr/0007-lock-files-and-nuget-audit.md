# ADR 0007: Package lock files and NuGet audit in the generated project

Status: accepted. Date: 2026-11-18.

## Context

The generated project pins every package version in `Directory.Packages.props` (central package management, with
transitive pinning on) and treats every warning as an error. Two supply-chain gaps were left: nothing recorded the
exact packages a restore resolved, so two restores of the same commit could use different transitive versions or
different package contents, and there was no stated policy for NuGet audit findings, which `TreatWarningsAsErrors`
turned into build errors everywhere, on a developer machine too, as soon as an advisory was published.

The questions: whether to ship lock files with the template or let the first restore write them; whether locked mode
can be switched on by the CI environment; whether a lock file written on Windows restores on Linux; and how audit
findings should behave locally and in CI.

## Decision

1. `Directory.Build.props` sets `RestorePackagesWithLockFile` for every project. The template ships no
   `packages.lock.json`; the first restore of a generated project writes one beside each project file, and the README
   tells the developer to commit them. The generated `.gitignore` does not ignore them.
2. `RestoreLockedMode` is `true` whenever `ContinuousIntegrationBuild` is `true`, which the same file already sets when
   `CI` or `TF_BUILD` is `true`. A CI build therefore restores in locked mode without any flag in the pipeline, and
   `-p:ContinuousIntegrationBuild=true` reproduces it locally.
3. NuGet audit is stated explicitly: `NuGetAudit` `true`, `NuGetAuditMode` `all`, `NuGetAuditLevel` `low`. These equal
   the .NET 10 defaults for `net10.0`; they are written down so the policy does not depend on a default. The audit
   codes NU1900 to NU1905 are listed in `WarningsNotAsErrors` outside CI, so locally they are warnings; in CI
   `TreatWarningsAsErrors` makes them errors.
4. This repository ignores `templates/**/packages.lock.json`, the harness deletes the lock files the in-place build
   writes, fails if the package contains a lock file, and for every built case asserts a lock file beside every
   project and runs `dotnet restore --locked-mode --force -p:ContinuousIntegrationBuild=true`.
5. This repository's package project gets no lock file and no audit settings: it has no package references (the
   package carries only template content), so its lock file would be empty; the repository's CI runs the harness with
   `CI=true`, so the policy is enforced there on the generated projects, and the harness forces CI mode for the
   locked restore when it runs on a developer machine.

## Measured facts

All on Windows 11 with SDK 10.0.402 (the one the generated `global.json` selects on this machine) unless stated, on
projects generated as `Acme.Demo`, clean and modular (`--module Sales`), 13 projects each. Linux runs used
`mcr.microsoft.com/dotnet/sdk:10.0`, which held SDK 10.0.404.

- First build. `dotnet build Acme.Demo.slnx -c Release -warnaserror` succeeds for both and leaves 13 lock files for
  13 projects. `dotnet restore Acme.Demo.slnx --locked-mode` then succeeds (exit 0); with `--force` as well.
- Version drift. Changing `Npgsql` from 10.0.3 to 10.0.2 in `Directory.Packages.props` and running
  `dotnet restore Acme.Demo.slnx --locked-mode` fails (exit 1) in both architectures with
  `error NU1004: The package reference Npgsql version has changed from [10.0.3, ) to [10.0.2, ).The packages lock file
  is inconsistent with the project dependencies so restore can't be run in locked mode.`, plus `NU1004` for each
  project that references the changed one and for the `CentralTransitive` entries. `CI=true dotnet build` with no
  flag fails the same way. With the version reverted the locked restore passes again.
- No lock file. `dotnet restore Acme.Demo.slnx --locked-mode` on a freshly generated project (no lock files) does not
  fail: it exits 0 and writes all 13 lock files. Locked mode therefore cannot be relied on to catch lock files that
  were never committed.
- Shipping lock files instead. In a scratch copy of this repository the in-place tree was restored (19 lock files),
  packed (the package held all 19) and generated. Result: the generated projects held none. The template engine's
  default `exclude` globs, read from `Microsoft.TemplateEngine.Orchestrator.RunnableProjects.dll` of SDK 10.0.402,
  include `**/*.lock.json` next to `**/*.filelist` and `**/*.user`. With `exclude` overridden in `template.json`, the
  lock files were generated: `myapp` was replaced in lower case (`acme.demo.domain`), but `mymodule` was not
  (`acme.demo.modules.mymodule.domain`), and the Api lock file listed the projects of both layouts (14 project entries),
  because the in-place Api references both. A locked restore of the modular output failed with `NU1004: A new project
  reference to Acme.Demo.Modules.Sales.Application was found`; the clean output passed while still listing the five
  module projects it does not have.
- Windows to Linux. The Windows lock files, copied without `bin`/`obj` into the container, pass
  `dotnet restore Acme.Demo.slnx --locked-mode` for both architectures (exit 0). Lock files written in the container
  from scratch differ byte for byte from the Windows ones in all 26 files, and not at all with
  `diff --strip-trailing-cr`: NuGet writes CRLF on Windows and LF on Linux, and nothing else differs. The locked restore
  on Linux left the CRLF files untouched. The generated `.gitattributes` has `* text=auto`, so git stores them with LF.
  The two SDK patch versions produced the same content, so no implicit SDK package reference is in these lock files.
- Runtime identifiers. The lock files have one target, `net10.0`; no project sets a runtime identifier. In the
  container, `dotnet restore src/Acme.Demo.Api --locked-mode -r linux-x64` fails with `NU1004: The project's runtime
  identifiers have changed from. Project's runtime identifiers: linux-x64, lock file's runtime identifiers .` After an
  unlocked restore with `-p:RuntimeIdentifiers=linux-x64` the lock file gains a `net10.0/linux-x64` target and the
  same locked restore passes.
- Audit, advisory. A throwaway copy of the clean project referenced `Newtonsoft.Json` 12.0.1 from the Domain project.
  `dotnet build src/Acme.Demo.Domain -c Release -warnaserror` (no `CI`) prints `warning NU1903: Package
  'Newtonsoft.Json' 12.0.1 has a known high severity vulnerability, https://github.com/advisories/GHSA-5crp-9r3c-p9vr`
  and succeeds: the project's `WarningsNotAsErrors` holds even under the command-line `-warnaserror`. With `CI=true`,
  or with `-p:ContinuousIntegrationBuild=true`, the same build fails with `error NU1903: Warning As Error: ...`.
- Audit, data not available. With an unreachable `auditSources` entry in the copy's `nuget.config`, a project with
  packages reports NU1900 and NU1905: before NU1905 was added to the list, the local build failed on
  `error NU1905: Warning As Error`; with the final settings it succeeds with both as warnings, and in CI mode both are
  errors. A project with no packages reports neither. The template's `nuget.config` defines no `auditSources`, in
  which case the documentation says the package sources are used and NU1905 is suppressed.
- The throwaway copy and all scratch output were deleted; nothing of these tests is in the template.

## Sources

Read for this decision (the Markdown sources of these pages, from the `NuGet/docs.microsoft.com-nuget` repository,
branch `main`, on 2026-11-18):

- https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files#locking-dependencies
  (`RestorePackagesWithLockFile`, `--locked-mode` and `RestoreLockedMode`, committing lock files of applications,
  the note that implicit SDK package references tie lock files to an SDK version, lock files and package pruning).
- https://learn.microsoft.com/nuget/concepts/auditing-packages (defaults: `NuGetAuditMode` `all` for `net10.0`,
  `NuGetAuditLevel` `low`, `NuGetAudit` `true`; audit sources and the NU1905 suppression; `WarningsNotAsErrors` with
  `TreatWarningsAsErrors`; the conditional `NuGetAuditCodes` example for CI; what to do for direct and transitive
  findings; `NuGetAuditSuppress`).
- https://learn.microsoft.com/nuget/consume-packages/central-package-management#transitive-pinning
- https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1004
- https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1905

## Judgement

- Not shipping lock files is forced by the facts above, not a preference: the engine drops them by default, and even
  forced through they carry the module placeholder and the superset of both layouts.
- Locked mode follows the CI variables rather than a flag in a pipeline file because the generated project has no
  pipeline yet and any CI that sets `CI` or `TF_BUILD` gets it. Because locked mode writes a missing lock file instead of
  failing, it is safe for a project whose lock files are not committed yet, and for the same reason it is not enough on
  its own: the pipeline that arrives in a later phase must fail when restore leaves the working tree changed.
- Advisories as errors in CI, warnings locally, is the trade-off the audit documentation describes: a developer is told
  but not blocked, and the shared gate refuses a known advisory. The cost is that a newly published advisory fails
  every pull request until someone raises the version or suppresses the advisory with a reason. A separate audit
  pipeline would avoid that, but would also let a vulnerable version merge; for a template whose purpose is a safe
  starting point, the gate wins. NU1900 and NU1905 are errors in CI for the same reason: an audit that did not run must
  not pass as clean.
- `global.json` keeps `rollForward` `latestFeature`. The documentation suggests `disable` when the SDK adds implicit
  package references; these projects have none (same lock files from 10.0.402 and 10.0.404).

## Consequences

- A developer commits the lock files with the first commit of a generated project and with every version change; the
  README says so and explains how to update a package and how to respond to an advisory.
- The CI workflow of the generated project (a later phase) needs no locked-mode flag, but must check that restore
  changed nothing (`git status --porcelain` empty), to catch lock files that are missing or stale.
- Publishing for a runtime identifier (a container image with `-r linux-x64`) in locked mode needs that identifier in
  `RuntimeIdentifiers` of the published project and of every project it references before the lock files are
  written: with the identifier only in the published project, the locked publish fails with NU1004 on each library
  (reproduced in the review of this change). The delivery phase decides it with the Dockerfile.
- `docker build` does not pass `CI` through, so a Dockerfile must copy the lock files and restore with
  `-p:ContinuousIntegrationBuild=true`; otherwise it restores unlocked with no error.
- Enabling trimming or native AOT later adds implicit SDK packages to the lock files; then the SDK version matters
  and `global.json` should be revisited.
- The template harness fails if a template package version gets an advisory, on CI and on a developer machine alike,
  so the template cannot release with a known-vulnerable dependency.
- The harness adds the lock-file assertions and the locked restore to each built case: 1.7 s per case on this
  machine; the whole run took 161 s against 160 s before the change.
