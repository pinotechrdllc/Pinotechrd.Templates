# Problem codes, v1

Error responses produced by the pipeline, and failures endpoints return through `ToProblem()`, are RFC 9457
problem details documents (`application/problem+json`) with a machine-readable `code`. Clients and UI
translations key on the `code`, so it is a public contract:

- Codes are snake_case and unique across the whole API.
- Codes are stable: add new ones, never rename or remove one.
- This file lists exactly the constants of every `*ErrorCodes` class. Add a line here in the same change that
  declares a code.
- `request_failed` is the generic fallback. A status that gets it today may get a specific code later, so clients
  must not treat it as the final code for a given status.

An `Error` returned by a use case carries its own code; its kind sets the status (`Validation` 400, `Unauthorized`
401, `Forbidden` 403, `NotFound` 404, `Conflict` 409, `Failure` 422 when a business rule rejects the operation).
An error is expected, so it never answers 500: that status is reserved for defects (exceptions).

| Code | HTTP status | Meaning | Declared in |
| --- | --- | --- | --- |
| `validation_failed` | 400 | The input is invalid; `errors` lists the messages per field. | `CommonErrorCodes` |
| `not_found` | 404 | The requested resource, or route, does not exist. | `CommonErrorCodes` |
| `conflict` | 409 | The request conflicts with the current state, such as a duplicate or a stale version. | `CommonErrorCodes` |
| `unauthorized` | 401 | The caller is not authenticated, or the credentials are not valid. | `CommonErrorCodes` |
| `forbidden` | 403 | The caller is authenticated but not allowed to perform the operation. | `CommonErrorCodes` |
| `internal_error` | 500 | The server failed unexpectedly (a defect); no detail of the cause is returned. | `CommonErrorCodes` |
| `invalid_json` | 400 | The request body is not valid JSON or does not match the expected shape. | `HttpErrorCodes` |
| `bad_request` | 400 | A route, query or header value is missing or cannot be parsed, or a required body is missing. | `HttpErrorCodes` |
| `method_not_allowed` | 405 | The route exists but does not accept the HTTP method. | `HttpErrorCodes` |
| `request_timeout` | 408 | The request body was sent too slowly, below the server's minimum data rate. | `HttpErrorCodes` |
| `payload_too_large` | 413 | The request body is larger than the server accepts. | `HttpErrorCodes` |
| `unsupported_media_type` | 415 | The request body has a content type the endpoint does not accept. | `HttpErrorCodes` |
| `rate_limited` | 429 | The client sent more requests than its rate limit allows; `Retry-After` gives the seconds to wait. | `HttpErrorCodes` |
| `request_failed` | any other | An error status the pipeline produces without a more specific code (fallback). | `HttpErrorCodes` |
| `tenant_slug_taken` | 409 | The slug belongs to a tenant with another name or subscription. | `TenantErrorCodes` |
| `tenant_subscription_not_found` | 404 | The subscription the tenant would belong to does not exist. | `TenantErrorCodes` |
| `tenant_suspended` | 422 | The tenant is suspended; it is not provisioned until it is activated. | `TenantErrorCodes` |
| `tenant_cancelled` | 422 | The tenant is cancelled and is never provisioned again. | `TenantErrorCodes` |
| `tenant_provisioning_failed` | 422 | Creating or migrating the tenant's schema failed; the tenant stays provisioning and a new request resumes it. | `TenantErrorCodes` |
