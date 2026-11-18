using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Problems;

namespace MyApp.Api.Tests.Support;

/// <summary>Body of the fixture endpoint that is validated with DataAnnotations.</summary>
public sealed record FixtureItem(
    [property: Required, StringLength(5, MinimumLength = 2)] string? Name,
    [property: Range(1, 10)] int Quantity);

/// <summary>Nested object of <see cref="FixtureProfile"/>.</summary>
public sealed record FixtureAddress([property: Required] string? Street);

/// <summary>Body with a simple property, a nested object and a list, to check the names of validation fields.</summary>
public sealed record FixtureProfile(
    [property: Required] string? FirstName,
    FixtureAddress? HomeAddress,
    List<FixtureAddress>? Others);

/// <summary>Test-only routes under <c>/api/v1/problem-fixtures</c> that provoke each kind of error response.</summary>
internal sealed class ProblemFixtureEndpoint : IEndpoint
{
    public const string Secret = "TOP-SECRET-fixture-value-7f3a";

    public const string Base = EndpointRegistration.RoutePrefix + "/problem-fixtures";

    public static string CodeOf(ErrorKind kind) => "fixture_" + kind.ToString().ToLowerInvariant();

    public static string MessageOf(ErrorKind kind) => $"The fixture returned a {kind} error.";

    public static readonly IReadOnlyDictionary<string, string[]> ValidationDetails = new Dictionary<string, string[]>
    {
        ["name"] = ["Name is too short."],
        ["quantity"] = ["Quantity is out of range.", "Quantity must be even."],
    };

    public void MapEndpoint(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/problem-fixtures");
        group.MapGet("/throw", Throw);
        group.MapPost("/items", (FixtureItem item) => Results.Ok(item));
        group.MapGet("/count", (int count) => Results.Ok(count));
        group.MapPost("/profiles", (FixtureProfile profile) => Results.Ok(profile));
#pragma warning disable ASP0022 // The duplicate is the point: it makes routing throw an ambiguous match.
        group.MapGet("/duplicate", () => Results.Ok("first"));
        group.MapGet("/duplicate", () => Results.Ok("second"));
#pragma warning restore ASP0022
        group.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        group.MapGet("/bad-request/{code:int}", BadRequest);
        group.MapGet("/validation-keys", () => Error.Validation("fixture_keys", "Keys.", PascalKeys).ToProblem());
        group.MapGet("/errors/{kind}", (ErrorKind kind) => ErrorOf(kind).ToProblem());

        // Only here to show how the OpenAPI document treats responses that an endpoint declares itself.
        // No input: 404, 409 and 500 are documented, 400 is not.
        group.MapGet("/declared-errors", () => Results.Ok())
            .WithName("FixtureDeclaredErrors")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        // A body, with its own 400 that must be kept as it is.
        group.MapPost("/own-bad-request", (FixtureItem item) => Results.Ok(item))
            .WithName("FixtureOwnBadRequest")
            .Produces<FixtureItem>(StatusCodes.Status400BadRequest, "application/json");
        // No input, but it declares a 400 and a 413 itself, so they are kept although they are not implied.
        group.MapGet("/own-no-input", () => Results.Ok())
            .WithName("FixtureOwnNoInput")
            .Produces<FixtureItem>(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    public static readonly IReadOnlyDictionary<string, string[]> PascalKeys = new Dictionary<string, string[]>
    {
        ["Name"] = ["first"],
        ["name"] = ["second"],
        ["Lines[1].Quantity"] = ["third"],
    };

    private static IResult BadRequest(int code) => throw new BadHttpRequestException("fixture bad request", code);

    private static IResult Throw() => throw new InvalidOperationException(Secret);

    public static Error ErrorOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => Error.Validation(CodeOf(kind), MessageOf(kind), ValidationDetails),
        ErrorKind.Unauthorized => Error.Unauthorized(CodeOf(kind), MessageOf(kind)),
        ErrorKind.Forbidden => Error.Forbidden(CodeOf(kind), MessageOf(kind)),
        ErrorKind.NotFound => Error.NotFound(CodeOf(kind), MessageOf(kind)),
        ErrorKind.Conflict => Error.Conflict(CodeOf(kind), MessageOf(kind)),
        _ => Error.Failure(CodeOf(kind), MessageOf(kind)),
    };
}
