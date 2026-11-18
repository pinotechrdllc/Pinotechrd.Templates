# Fixture tests of the module endpoint

`scripts/test-template.ps1` copies every `*.cs` file under this folder into the `tests/` folder of the generated modular
project, keeping the relative path and replacing the placeholders `MyApp` (the project name) and `MyModule` (the module
name) in paths and content. This folder is not processed by the template engine and is never packed.

- `MyApp.Api.Tests/` goes to `tests/<Name>.Api.Tests/`: tests that host the real API with `ApiHostFactory`
  (`MyApp.Api.Tests.Support`). The module test project does not reference the API host, so the tests of the routing,
  tagging and validation of `CreateWidgetEndpoint` belong here.
- `MyApp.Modules.MyModule.Tests/` goes to `tests/<Name>.Modules.<Module>.Tests/`, for tests that need only the module.

Put every fixture test in a namespace that ends in `.Fixtures` (for example `MyApp.Api.Tests.Fixtures`): after the
contract is regenerated the harness runs the API tests by filter, and it fails when no test of a `.Fixtures` namespace
ran. It also fails with "no fixture test" while this folder holds no `*.cs` file.
