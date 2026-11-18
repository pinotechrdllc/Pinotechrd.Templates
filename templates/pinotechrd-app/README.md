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
            .WithTags("System")
            .WithSummary("Gets the informational version of the running application.");
}
```

Each endpoint sets a unique name with `WithName` (it becomes the `operationId` of the API contract) and a tag for its feature with `WithTags`, so no operation falls back to the assembly name as its tag.

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

## API contract

`contract/openapi.v1.json` is the OpenAPI description of the API, frozen in the repository. Clients generate their types from this file, not from a running server, and any change to the API surface shows up as a diff in it. It lists every operation under `/api/v1` by its endpoint name (`WithName`, which becomes the `operationId`) and the problem details schemas, where `title`, `status`, `code` and `traceId` are required. Each operation declares the problem responses the pipeline can return for it: 500 always; 400 when it takes parameters or a body; 408, 413 and 415 when it takes a body; 401 and 403 when it requires authorization; 429 (with its `Retry-After` header) unless the endpoint opts out of rate limiting with `DisableRateLimiting()`. Unknown routes and wrong methods are not operations, so their 404 and 405 are not listed. An endpoint declares the errors it returns itself (404, 409, 422) with `ProducesProblem`, for example `.ProducesProblem(StatusCodes.Status404NotFound)`. Health probes are not part of it.

Regenerate it after changing an endpoint, a request or a response, and commit it together with the code change:

```sh
dotnet build src/MyApp.Api -p:UpdateContract=true
```

That build starts the application without a server, in the Production environment and with no configuration, to read its endpoints. Nothing registered at startup may depend on the environment or require a database or any other external service to describe the API. A normal build does not touch the file.

Different .NET SDK patch versions can emit slightly different schema text, so check the contract (the test that compares it with the served document) on one pinned SDK, in CI, and regenerate it with that same SDK. Outside Production the running API also serves the same document at `/openapi/v1.json`, for inspection.

## Logging and telemetry

`builder.AddTelemetry()` in `Program.cs` sets up both; the code is in `src/BuildingBlocks/MyApp.BuildingBlocks.Web/Telemetry/`.

### Logs

Logs are an event stream on standard output, one JSON object per line, in every environment (Serilog's compact format). The platform collects them (`docker logs`, Kubernetes, the cloud's log agent); the application writes no files and sends logs nowhere else.

```json
{"@t":"2026-11-18T06:15:09.99Z","@m":"HTTP GET /api/v1/boom responded 500 in 25.4461 ms","@i":"776c4006","@l":"Error","@tr":"c146d631ea7bb66213c14a1a9ae9d332","@sp":"acfd2052f6e05710","RequestRoute":"/api/v1/boom","RequestMethod":"GET","RequestPath":"/api/v1/boom","StatusCode":500,"Elapsed":25.4461,"SourceContext":"Serilog.AspNetCore.RequestLoggingMiddleware","Application":"MyApp.Api","Environment":"Production","Version":"1.0.0"}
```

`@t` is the time, `@m` the rendered message, `@l` the level (absent for Information), `@x` the exception, `@tr` and `@sp` the trace and span ids of the current activity. Every event carries `Application`, `Environment` and `Version`. `@tr` is the `traceId` of a problem response for the same request, so an error a client reports leads to its log lines and its trace.

Each request writes exactly one completion event when it ends, with `RequestMethod`, `RequestPath` (without the query string), `RequestRoute` (the route template, when an endpoint matched), `StatusCode` and `Elapsed` in milliseconds. Its level is Error for a 5xx or an exception, Information otherwise, 4xx included: a 4xx is the client's outcome and scanners produce them in bulk, so count them by `StatusCode` instead. Health probes write it at Verbose, which is off, so polling does not flood the logs. An unhandled exception is logged once, with its stack trace, by the exception handler; the completion event of that request is at Error. If the exception happens after the response has started (part of the body was sent), the exception handler logs it once, the connection is aborted so the client sees an incomplete response, and the completion event is at Error with the status that was actually sent and no exception.

To add the user or the tenant to the completion event (a later unit), call `IDiagnosticContext.Set("UserId", id)` from a middleware placed after authentication; the completion event wraps it and picks the property up. Only opaque identifiers, never names or emails.

The levels come from the standard `Logging:LogLevel` section: `Default` for every category, and a category name (such as `Microsoft.AspNetCore`) for every category that starts with it. Values are `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` and `None`; an invalid value stops the application at start. Provider sections (`Logging:Console`) and wildcards in category names are not read. Override them with environment variables:

```sh
Logging__LogLevel__Default=Debug
Logging__LogLevel__Microsoft.AspNetCore=Information   # names with dots work in Docker and Kubernetes, not in a POSIX shell's export
```

The levels and the OTLP switch below are read once, at start: a configuration source added later or a reload of `appsettings.json` does not change them. Restart the application to apply a change.

Some framework categories write a header value, the query string or the client address. They stay at `Warning` when `Default` or a parent such as `Microsoft.AspNetCore` is raised, and change only when the category itself is named (`TelemetryRegistration.PinnedLogCategories`):

| Category | What it writes below Warning |
| --- | --- |
| `Microsoft.AspNetCore.Hosting.Diagnostics` | "Request starting" (Information) with the query string. The completion event replaces it. |
| `Microsoft.AspNetCore.HttpOverrides` | "Unknown proxy" (Debug) with the client address and port; "Unparsable IP" (Debug) with the raw `X-Forwarded-For` value. |
| `Microsoft.AspNetCore.Cors` | "The request has an origin header" (Debug) and "Request origin ... does not have permission" (Information) with the `Origin` value; a refused preflight names the requested headers. |
| `Microsoft.AspNetCore.Server.Kestrel.BadRequests` | "bad request data" (Debug) quoting the rejected part of the request, such as a `Host` header value or the request line. |

Naming one of them, for example `Logging__LogLevel__Microsoft.AspNetCore.HttpOverrides=Debug` to debug a proxy setup, is a deliberate choice that writes those values to the logs; do it briefly and not where the logs are kept for long.

If the application cannot start (an invalid setting, for example) it writes one Fatal event with the exception in the same format and exits with code 1. A failure while the host starts (a hosted service or a setting validated at start) is written once, by the host, as "Hosting failed to start" at Error, and the exit code is 1. A shutdown signal that arrives while the host starts is not a failure: the exit code is 0 (the host still logs its "Hosting failed to start" event with the cancellation).

A test or another piece of code can receive the events by registering a Serilog `ILogEventSink` (or `ILogEventEnricher`) as a service; nothing in the building block changes.

### Secrets and personal data

No log event and no span attribute may contain a header value (`Authorization`, `Cookie`, `Set-Cookie` or any other), a query string, a request or response body or the client address: they carry tokens, session cookies and personal data. At the shipped levels, and with `Default` or `Microsoft.AspNetCore` raised to any level, none of them is logged or recorded; only naming a pinned category (above) writes the values listed for it. Outgoing calls made through `IHttpClientFactory` are logged with header values and query values redacted by the platform.

The raw request path is recorded: in the completion event (`RequestPath`), in the request scope of every framework event and in the `url.path` span attribute. It is kept because it finds the resource in an incident. Routes therefore carry opaque identifiers only, such as a UUID: never personal data (a national id, an email, a phone number, a name) and never a secret (tokens go in a header or in the body).

The one accepted exception to the rule is the unhandled-exception event: it holds the exception in full (message, inner exceptions, stack trace), and an exception message can quote the value that caused it. That is acceptable only because access to the logs is restricted and they are kept for a limited period; the deployment must provide both. Developers do not put personal data or secrets in the messages of exceptions they throw, and do not log a caught exception that wraps user input at Information or above. For the same reason the exception handler logs a request that could not be bound by its status only: the platform's message quotes the rejected value.

Code that logs follows the same rule: log identifiers, never credentials or personal data.

Clients can send trace context. An incoming `traceparent` chooses the trace id that appears in the logs and in the `traceId` of problem responses, and can turn sampling off for its request; incoming `baggage` is forwarded on outgoing HTTP calls. The reverse proxy should remove `traceparent`, `tracestate` and `baggage` from requests of public clients, and keep them only from trusted callers.

### Traces and metrics

OpenTelemetry records a server span per request, named after its route with `http.route` set, and a client span per outgoing `HttpClient` call. Health probes produce no span. No header (the user agent included) and no query string is recorded; a client span keeps the scheme, host, port and path of its target only. Metrics: the ASP.NET Core server metrics (`http.server.request.duration` among them), the HTTP client metrics and the .NET runtime metrics. The resource names the service with the application name (`service.name`), its version (`service.version`) and its environment (`deployment.environment.name`); `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` override them.

Nothing is exported until an OTLP endpoint is set; with none, the application starts, runs and stops without trying to send anything. The endpoint is not in `appsettings.json`: set it in the environment, for example to an OpenTelemetry Collector:

```sh
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317     # traces and metrics, gRPC
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf                  # with port 4318, when the collector only accepts HTTP
```

`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` or `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` alone turns on that signal only. The other standard `OTEL_EXPORTER_OTLP_*` variables (headers, timeout) apply as usual. An unreachable collector does not affect requests and logs nothing; the spans of that interval are lost. Stopping the application waits for the last export to give up, up to the exporter timeout (`OTEL_EXPORTER_OTLP_TIMEOUT`, 10 seconds by default); keep it below the orchestrator's termination grace period (30 seconds by default in Kubernetes). Logs are not sent over OTLP; they stay on standard output.

Nothing is written to disk. `Serilog.AspNetCore` brings the file and debug sinks as transitive packages; they are inert, because sinks are configured in code and never read from configuration. The OTLP exporter writes to disk only if `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY=disk` is set. Do not enable either.

### Adding a source or a meter

An `ActivitySource` or a `Meter` whose name starts with the project name followed by a dot (`MyApp.Sales`, for example) is collected with no registration:

```csharp
private static readonly ActivitySource Source = new("MyApp.Sales");
private static readonly Meter Meter = new("MyApp.Sales");
```

For any other name, a library's for example, register it once from the code that adds that library, before or after `AddTelemetry()`:

```csharp
services.AddTelemetrySource("Npgsql");
services.AddTelemetryMeter("Npgsql");
```

## Security settings

The API applies these protections to every request, in one fixed order set by `app.UseApiPipeline()` in `Program.cs` (the order and its reasons are in the XML documentation of that method):

1. **Forwarded headers**, only when a proxy is configured: the client address and scheme come from `X-Forwarded-For` and `X-Forwarded-Proto` sent by a trusted proxy.
2. **Security headers** on every response, errors included: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (the API serves no HTML) and `Cross-Origin-Resource-Policy: same-origin` (other sites cannot embed a response with an image or script tag; CORS requests are governed by the CORS policy, not by this header). `Cache-Control: no-store` is added to every response that does not set its own, so no browser or shared cache keeps user data or tokens; an endpoint that wants caching sets `Cache-Control` itself. This includes the OpenAPI document (served outside Production only, so it is never stale); the health probes set their own `no-store, no-cache`. `Strict-Transport-Security: max-age=31536000; includeSubDomains` is added only to HTTPS requests for a host that is not `localhost`, `*.localhost` or a loopback address, and never in the Development environment, so a browser does not pin HTTPS on a development machine. Kestrel does not send the `Server` header.
3. **CORS**, only when an origin is listed. Preflight requests are answered by the API itself. While CORS is on every response carries `Origin` exactly once in `Vary`, next to any value an endpoint set (the platform only writes it when more than one origin is listed), so a shared cache never serves a response made for one origin to another.
4. **Error handling and routing** (see [Errors](#errors)).
5. **Rate limiting**: a fixed window per client address. A rejected request answers 429 `rate_limited` as a problem document with a `Retry-After` header in seconds. The health probes are never limited.
6. **Authentication, then authorization**, each added by `UseApiPipeline()` only when its services are registered (`AddAuthentication(...)`, `AddAuthorization()`), so a project without them runs as before. No call in `Program.cs` is needed. Calling `UseAuthentication()` or `UseAuthorization()` again there is harmless but redundant: the second authentication reuses the result cached for the request and the second authorization evaluates the same policy again.

Authentication and authorization sit after routing, because authorization needs the matched endpoint, and inside the error handling, so an anonymous request to a secured endpoint answers 401 `unauthorized` and an authenticated one without permission 403 `forbidden`, both as problem documents with the security and CORS headers. Placing them there also stops `WebApplication` from adding them on its own in front of the whole pipeline, before routing, where authorization sees no endpoint and every secured endpoint fails with a 500. They run after the rate limiter, so a flood of requests is rejected before any token is validated. The global limit is therefore per client address, never per user; a later per-user or per-tenant limit needs the authenticated identity, so it must be a separate limiter placed after authentication, not a policy of this one. Operations that require authorization (`RequireAuthorization()` or `[Authorize]`, without `AllowAnonymous()`) declare 401 and 403 in the API contract; a fallback authorization policy is not endpoint metadata and does not add them.

The settings live in the `Http` section of `appsettings.json` and can be set per environment, for example with environment variables (`Http__Cors__AllowedOrigins__0=https://app.example.com`, one variable per list item). They are validated when the host starts: an invalid value stops it with an `OptionsValidationException` that names the setting.

| Setting | Default | Why this default |
| --- | --- | --- |
| `Http:Cors:AllowedOrigins` | empty: CORS off | Closed by default: with no origin listed no CORS header is written and only same-origin pages can call the API. Each entry is an origin exactly as a browser sends it, `scheme://host[:port]` with no path, no trailing slash and no default port (`https://app.example.com`, not `https://app.example.com:443`). Credentials (the refresh cookie) are allowed for listed origins, so `*` and any other wildcard are rejected, and an `http` origin is accepted only for `localhost`, a `*.localhost` name or a loopback address, or in the Development environment; anything else must be `https`. The Vite development server, `http://localhost:5173`, belongs in `appsettings.Development.json`, not in the settings of a deployed environment. |
| `Http:RateLimiting:PermitLimit` | `300` | Requests one client may send per window: about 5 per second on average, generous for a person using the application and low enough to slow down a script. Must be between 1 and 10,000 (no person or browser sends more in one window, so a higher value would switch the limiter off), and the limit divided by the window must not exceed 100 requests per second, for the same reason. |
| `Http:RateLimiting:Window` | `00:01:00` | Length of the fixed window. A minute smooths short bursts such as a page loading several resources. Must be between `00:00:01` and `01:00:00`: a rejected client may wait for the rest of the window, and a longer one turns a burst into a lockout. |
| `Http:ForwardedHeaders:KnownProxies` | empty | IP addresses of the reverse proxies whose forwarded headers are trusted, such as `10.0.0.5` or `fd00::5`. Empty, the headers are ignored, so a client cannot choose the address that the rate limiter and the logs see. Rejected: an IPv4 address not written as four plain decimal octets from 0 to 255 without leading zeros (the platform reads `010.0.0.5` as octal, 8.0.0.5, and `0x0A` as hexadecimal, so such a value would silently trust another host); an IPv4-mapped IPv6 address such as `::ffff:10.0.0.5` (write the IPv4 form, `10.0.0.5`, which is how the platform matches IPv4 clients); anything that is not an IP address. |
| `Http:ForwardedHeaders:KnownNetworks` | empty | Networks of trusted proxies in CIDR notation, such as `10.0.0.0/8`, for proxies without a fixed address (containers, Kubernetes). Rejected: host bits that are not zero (`10.0.0.1/8`: either a single proxy or a network was meant, and guessing widens the trust); IPv4 not written as four plain decimal octets (as for `KnownProxies`); an IPv4 network wider than /8 (the largest private block; wider spans networks no deployment owns, up to `0.0.0.0/0`); an IPv6 network wider than /32 (the largest block a registry allocates to one operator; wider spans unrelated operators, up to `::/0`); an IPv6 network that overlaps the IPv4-compatible range `::/96` or the IPv4-mapped range `::ffff:0:0/96` (a dual-stack listener reports every IPv4 client as `::ffff:a.b.c.d`, so such a network, `::/32` for example, would trust the whole IPv4 internet); an IPv6 network that overlaps Teredo, `2001::/32`, or 6to4, `2002::/16` (anyone can obtain an address there). Overlap is computed from the prefixes, so a network that contains, or lies inside, one of these ranges is rejected. |

Behind a reverse proxy, list the proxy (or its network) in `KnownProxies` or `KnownNetworks`; otherwise every request appears to come from the proxy and all clients share one rate limit. The proxy must overwrite or append to `X-Forwarded-For` (not pass the client's value through unchanged); the API walks the list from the right and stops at the first address that is not a trusted proxy, so values a client writes to the left are ignored.

`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (some hosting platforms set it) makes the platform add its own forwarded headers middleware in front of the pipeline and clear the trusted lists, which on its own would trust `X-Forwarded-For` from anyone. With the variable set and no proxy configured the API refuses to start and says so: configure `Http:ForwardedHeaders` or unset the variable. With proxies configured the API's lists are applied after the platform's setup and win, so only the listed proxies are trusted either way.

CORS details, fixed in `HttpSecurityRegistration` because a JSON API needs no more:

- Allowed methods: `GET`, `POST`, `PUT`, `PATCH`, `DELETE` (plus `HEAD` and `OPTIONS`, which need no listing).
- Allowed request headers: `Accept`, `Accept-Language`, `Authorization`, `Content-Type`, `If-Match`, `If-None-Match`.
- Headers a browser client may read besides the always-exposed ones: `Retry-After`, `ETag`, `Location`.
- Browsers may cache a preflight answer for 10 minutes.

The rate limit is partitioned by `HttpRateLimiting.ClientPartitionKey`: the client address after forwarded headers, with an IPv6 client counted by its /64 network so it cannot reset its limit by rotating addresses. A stricter limit for a group of endpoints (authentication, for example) is a named policy added with `services.Configure<RateLimiterOptions>(o => o.AddPolicy(...))`, partitioned with the same key and required with `.RequireRateLimiting(name)` (it runs before authentication, so it can key on the address but not on the user); it applies on top of the global limit and its rejections get the same 429 answer. An endpoint that must never be limited is marked with `.DisableRateLimiting()`, which also removes the 429 from its entry in the API contract.

Limits of this setup, stated plainly:

- `/health/live` and `/health/ready` are public (they allow anonymous access, so a fallback authorization policy never applies to them) and never rate limited, and `/health/ready` runs the readiness checks (later the database) on every request. The reverse proxy must not expose `/health/*` to the internet; only the orchestrator should reach them.
- When the server listens on a Unix socket or a named pipe there is no remote address, so every client shares one rate limit partition. That setup is not supported with this limiter: listen on TCP behind the proxy and list the proxy as above.
