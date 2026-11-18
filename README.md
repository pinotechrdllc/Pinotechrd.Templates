# Pinotechrd.Templates

Project templates for Pinotechrd applications: a .NET API and a Vue frontend with security, multi-tenancy and i18n built in.

> **Work in progress.** The template is not usable yet. See [PLAN.md](PLAN.md) for the tasks and their status.

## Planned usage

```bash
dotnet new install Pinotechrd.Templates
dotnet new pinotechrd-app -n MyApp
```

Options that change the generated project today:

| Option | Values | Default |
| --- | --- | --- |
| `--architecture` | `clean`, `modular` | `clean` |
| `--module` | the name of the first module, a PascalCase C# identifier (used with `modular` only) | `Core` |

Planned options: they are accepted, but change nothing yet.

| Option | Values | Default |
| --- | --- | --- |
| `--profile` | `standalone`, `distributed` | `standalone` |
| `--deploy` | `compose`, `kubernetes` | `compose` |
| `--iac` | `none`, `terraform` | `none` |
| `--cloud` | `gcp`, `azure`, `aws` | none |

## License

Distributed under the [MIT License](LICENSE).
