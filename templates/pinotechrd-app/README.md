# MyApp

MyApp is a .NET 10 API organized in clean architecture layers: dependencies point inward, so the domain knows nothing about frameworks or the database. It was generated from the `pinotechrd-app` template.

## Project layout

| Project | Responsibility |
| --- | --- |
| `src/MyApp.Domain` | Entities, value objects and business rules. No references to other projects or packages. |
| `src/MyApp.Contracts` | Request and response DTOs shared with clients. |
| `src/MyApp.Resources` | Localized messages. |
| `src/MyApp.Application` | Use cases. Depends on Domain, Contracts and Resources. |
| `src/MyApp.Infrastructure` | Database, messaging and other external systems. Depends on Application. |
| `src/MyApp.Api` | HTTP host and endpoints. Composes the other projects. |
| `tests/MyApp.Domain.Tests` | Unit tests for the domain. |
| `tests/MyApp.Api.Tests` | Integration tests that host the API in memory. |

## Build and test

```sh
dotnet build MyApp.slnx
dotnet test MyApp.slnx
```

Run the API with `dotnet run --project src/MyApp.Api`.

## Adding an endpoint

Each use case is one file in `src/MyApp.Api/Endpoints/` with a public class that implements `IEndpoint`. It is found at startup and mapped under `/api/v1`, so routes never repeat the version prefix:

```csharp
public sealed class GetVersionEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapGet("/version", () => TypedResults.Ok(new VersionResponse(version)))
            .WithName("GetVersion")
            .WithSummary("Gets the informational version of the running application.");
}
```

Health probes live outside the API group: `/health/live` and `/health/ready` (checks tagged `HealthRegistration.ReadyTag`).
