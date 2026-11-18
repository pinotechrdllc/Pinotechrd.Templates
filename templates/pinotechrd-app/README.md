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

## Errors

Error responses produced by the pipeline (an unknown route, a malformed request, a validation failure, an unhandled exception) and failures an endpoint returns through `ToProblem()` are RFC 9457 problem details documents, served as `application/problem+json` whatever the request's `Accept` header. An unhandled exception is logged, and the response is always a 500 `internal_error` with no exception details. A body an endpoint writes itself, such as `Results.BadRequest("text")` or `Results.Conflict(obj)`, is not converted: return every failure through `ToProblem()`.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "The name is too long.",
  "errors": { "name": ["The name is too long."], "lines[1].quantity": ["Must be positive."] },
  "code": "name_too_long",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736"
}
```

`code` is the contract that clients and translations key on. `errors` appears only on validation errors. `traceId` correlates the response with the logs. An endpoint returns a failed result with `ToProblem()`, which maps the `ErrorKind` to the status: `Validation` 400, `Unauthorized` 401, `Forbidden` 403, `NotFound` 404, `Conflict` 409, and `Failure` 422 Unprocessable Content when a business rule rejects the operation. A returned error is expected and never answers 500; 500 `internal_error` is reserved for exceptions. A body that is not valid JSON or does not match the expected shape answers 400 `invalid_json`; any other malformed request (a missing or unparsable route, query or header value, or a missing body) answers 400 `bad_request`.

```csharp
routes.MapGet("/things/{id}", Results<Ok<Thing>, ProblemHttpResult> (int id) =>
{
    Result<Thing> result = Find(id);
    return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
});
```

The keys of `errors` name the fields as they appear in the request JSON: each segment of the property path follows the API's JSON naming policy (camelCase by default), and dots and indexes are kept (`homeAddress.street`, `lines[1].quantity`). This applies to both sources: the built-in validation, which reports C# property paths, and the `Details` of a validation `Error`, so either form can be used when building one.

Request types are validated with DataAnnotations by the built-in minimal API validation (`AddValidation()`). It is generated at compile time for the request types of the endpoints mapped in the project that calls `AddValidation()`, wherever those types are declared (`MyApp.Contracts` included). A separate project that maps its own endpoints must call `AddValidation()` in its own registration code, or its request types are not validated; the calls add up.

Codes are string constants in classes whose names end in `ErrorCodes` (`CommonErrorCodes` and `HttpErrorCodes` to start). Every code is listed in `contract/problem-codes.v1.md`. Codes are only ever added, never renamed. `request_failed` is the generic fallback and may be replaced by a specific code for a status later.
