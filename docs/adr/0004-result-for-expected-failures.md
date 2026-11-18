# ADR 0004. A result type for expected failures

## Context

A use case can fail in ways that are part of its contract: the input is invalid, the record does not exist, the
version is stale, the caller may not do it. It can also fail because of a defect or because the database is down.
The template needs one way to report the first group that the API turns into RFC 9457 problem details with a
stable `code` the UI translates.

The *Framework Design Guidelines* advise reporting failures with exceptions, not return codes. That advice is
written for reusable libraries, where the caller decides what a failure means. Inside an application, where the
handler owns that decision, it costs more than it gives.

## Decision

Expected failures are returned, not thrown. `MyApp.BuildingBlocks.Domain` provides `Error` (a stable `Code`, a
`Message` for developers, a `Kind` and optional per-field `Details`) and `Result` / `Result<T>`, which hold either
a success or one `Error`. Exceptions remain for defects and infrastructure faults only.

This departs from the guidelines on purpose:

- The failure is visible in the signature. A handler that returns `Result<T>` says it can fail; a thrown
  exception is invisible to the caller and to the reviewer.
- No pipeline is needed. Exceptions for control flow require a filter or middleware that catches them and
  guesses a status; a result is a plain value any caller can inspect.
- One place maps errors to HTTP. The web building block translates `ErrorKind` to a status and `Code` to the
  problem details `code`, so status codes are not decided in each handler.

## Consequences

- One model, not a mix. A use case never throws for an expected failure and never returns a `Result` for a defect.
  A custom exception type for a business rule is a review finding.
- `ErrorKind` is kept small: one value per distinct HTTP outcome. A new kind needs a new status to justify it.
- Error codes are part of the public contract. Changing one is a breaking change for the UI and other clients.
- Reading `Value` of a failure or `Error` of a success throws `InvalidOperationException`, and creating a
  failure with a null error throws `ArgumentNullException`: those are defects in the caller.
- Callers must check the result. Implicit conversions from a value and from an `Error` keep the success and
  failure paths short, so the check does not turn into boilerplate.

## Usage notes

C# does not apply user-defined conversions when the source expression has an interface type. A value typed as an
interface, such as `IReadOnlyList<T>`, is not converted implicitly: return `Result.Success(value)` instead.
