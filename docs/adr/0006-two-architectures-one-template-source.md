# ADR 0006: Two architectures from one template source tree

Status: accepted (spike `spikes/template-layouts`). Date: 2026-11-18.

## Context
`--architecture clean | modular` must produce two different project layouts from one source tree. The plan: every file lives in one tree; `sources[].modifiers` with a `condition` on a computed symbol remove the files of the other layout; comment-style conditional regions are used only in the `.slnx`, one `.csproj` and `README.md`; a user-given module name replaces the `MyModule` placeholder in text, file names and folder names. These mechanisms were inferred from files shipped with the SDK and had not been seen working. A second question, for the next unit: what .NET 10 `AddValidation()` leaves observable when called from a class library.

## What was tested
- .NET SDK 10.0.402 (pinned through the repository `global.json`; outside the repo this machine picks 11.0.100), template engine of that SDK, isolated hive (`--debug:custom-hive`).
- A spike template with `sourceName` `MyApp`, a choice `architecture`, a text `module` (default `Core`, `replaces` and `fileRename` `MyModule`), a computed `modular`, a `regexMatch` symbol `moduleIsIdentifier`. Content: a `.slnx` listing both layouts, `MyApp.Domain` and `MyApp.Application` (clean only), `src/Modules/MyModule/MyApp.Modules.MyModule.Domain` (modular only), `MyApp.Api` (an exe whose csproj references either layout), a `README.md`, and `Startup.cs` with two partial methods implemented in `Startup.Clean.cs` and `Startup.Modular.cs`.
- `run.ps1` builds variants of the template on the fly: include-form modifiers, two solution files with `rename`, `isEnabled`/`isRequired` rules, and value forms. Every generated solution is built with `-warnaserror` and the Api is run.
- Sources: the dotnet/templating wiki (template.json reference, conditional processing, conditions, value forms, constraints) and the default glob list inside `Microsoft.TemplateEngine.Orchestrator.RunnableProjects.dll` of SDK 10.0.402.

## Results
1. PASS. `modifiers` with `condition` + `exclude`: clean output has 10 files and no `src/Modules`; modular has 8 and no clean-only project. The include form (top-level `include` of shared files plus per-layout `include` in modifiers) gives identical file lists.
2. PASS. `"type": "computed", "value": "(architecture == \"modular\")"` works in modifier conditions (`(modular)`, `(!modular)`) and in `#if (modular)` regions.
3. PASS. `.slnx` is processed by default with `<!--#if (modular) -->`, `<!--#else -->`, `<!--#endif -->` on their own lines; the solution lists only the right projects and keeps no marker. No `specialCustomOperations` needed. The fallback (two `.slnx`, one excluded, the other renamed by `"rename": { "MyApp.Clean.slnx": "MyApp.slnx" }` in the modifier) also works.
4. PASS. Same syntax in the `.csproj` around `ProjectReference` and in `README.md`; the right branch is kept and the markers are removed.
5. PASS. With `--module Sales`: `src/Modules/Sales/Acme.Demo.Modules.Sales.Domain/Acme.Demo.Modules.Sales.Domain.csproj` and `class SalesModule`; default gives `Core`/`CoreModule`. `replaces` + `fileRename` on the symbol renamed text, files and folders; nothing `sourceName`-style was needed.
6. PASS. All three outputs build with `-warnaserror`. Api prints `api,application+domain` (clean), `api,Sales`, `api,Core`: the call to the partial method with no body compiles away.
7. PASS. No `.template.config`, file lists exactly as expected, no `MyApp` or `MyModule` in names or content.
8. NOT SUPPORTED: rejecting a text value by pattern. Available: an invalid choice is rejected (exit 127); `isRequired: "(architecture == \"modular\")"` rejects modular without `--module` (exit 102, no files, default ignored); `isEnabled: false` does NOT reject the option, `--module Sales` with clean is accepted silently (exit 0); `regexMatch` gives a bool usable in conditions; derived symbols with value forms can normalise.
9. In a class library with `FrameworkReference Microsoft.AspNetCore.App` (plain `Microsoft.NET.Sdk`, no extra settings), `AddValidation()` with no validatable types still makes the generator emit a resolver and intercept the call. From outside: `IOptions<ValidationOptions>.Value.Resolvers` holds a `GeneratedValidatableInfoResolver` whose assembly is the library, plus `RuntimeValidatableParameterInfoResolver`; it answers false for every type. With an endpoint binding a record with `[Required]`/`[Range]`, the same resolver answers `TryGetValidatableTypeInfo(typeof(CreateOrder))` true. Without the call, no options are registered.

## Findings
- The online docs do not list `.slnx` among processed files, but the SDK's default globs include `**/*.slnx`, `**/*.*proj` and `**/*.md`, all with the XML comment syntax. The docs were out of date, not the engine.
- The raw source tree builds as is (markers are plain XML comments, each partial method has one body), so the template can be compiled before it is packed.
- `replaces` is a single pass: `--module MyApp` leaves `MyApp` in 9 places, and an invalid identifier such as `Sales-Orders` breaks the build (CS1514, CS0116). `Api`, `Domain`, `Modules` and lower-case `sales` built fine in this spike; lower-case names only break naming rules.
- Value forms: `titleCase` lower-cases inner capitals (`SalesOrders` -> `Salesorders`); `replace` `[^A-Za-z0-9]` then `firstUpperCase` keeps `SalesOrders`, fixes `sales` -> `Sales`, but gives `sales-orders` -> `Salesorders`. Neither is a full PascalCase converter.
- Validation: `IValidatableInfoResolver`, `ValidationOptions.Resolvers` and `TryGetValidatableTypeInfo` are `[Experimental("ASP0029")]`; reading them needs `NoWarn ASP0029`. Generated types are `file`-scoped with mangled names, so only their assembly is a stable fact. Two `AddValidation()` calls add the runtime resolver twice.
- Not tested: one folder per name for a list such as `--modules A,B`; the engine has no loop over a list, so this spike covers one module only.

## Decision
- File selection per layout: `sources[].modifiers` with `condition` on the computed `modular` and `exclude` lists. Exclude rather than include, so a new shared file is kept by default.
- `.slnx`, `.csproj`, `README.md`: `<!--#if (modular) -->` / `<!--#else -->` / `<!--#endif -->`, whole lines, default processing. Fallback kept on record: two solution files plus `rename` in the modifier.
- Module name: a text parameter with `replaces` and `fileRename` set to `MyModule`, made required for modular with `isRequired` on `architecture` if a default is not wanted.
- Code that differs per layout in a shared class: partial methods, body only in the layout-only file.

## Consequences for the template
- Conditional regions only in `.slnx`, `.csproj`/`.props` and `.md`; never in `.cs`. C# differences go through whole files plus partial methods.
- `MyModule` and `MyApp` must not appear inside any other word that should survive, and a module name must not contain them.
- The module name is not validated by the engine. Document "PascalCase C# identifier", and add a template test that generates a lower-case and a hyphenated name to keep the failure mode known, or normalise with a derived symbol (`replace` + `firstUpperCase`).
- `isEnabled` does not reject misplaced options; do not rely on it to make `--module` meaningless with clean.
- A list of modules needs another mechanism (post-action or one placeholder per slot); decide before implementing `--modules A,B`.
- Validation tests can assert "this assembly called AddValidation" by finding a resolver in `ValidationOptions.Resolvers` whose assembly is the module's, accepting ASP0029. Each generated resolver knows only the types of its own assembly (the `Probe.Empty` resolver did not know `CreateOrder`), so each module that maps endpoints must call `AddValidation()` itself.
