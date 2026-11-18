using MyApp.Api;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.OpenApi;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;
using MyApp.BuildingBlocks.Web.Telemetry;

// Read once the host is built, so a stop requested while it starts is not reported as a failure.
var stopping = CancellationToken.None;
try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddTelemetry();
    builder.Services.AddProblems();
    builder.Services.AddHttpSecurity();
    // Built-in validation is generated at compile time for the request types of the endpoints mapped in the project that
    // calls AddValidation(), wherever those types are declared (a contracts project included). A project of its own that maps
    // endpoints (such as a module's endpoints) must call AddValidation() in its own registration; the calls add up.
    builder.Services.AddValidation();
    builder.Services.AddApiEndpoints(ApiAssembly.Assembly);
    // The business code: in a modular application, each module with its services and its endpoints.
    builder.AddBusiness();
    builder.Services.AddHealth();
    builder.Services.AddDatabaseWithReadiness();
    builder.Services.AddOpenApiDocument();

    var app = builder.Build();
    stopping = app.Lifetime.ApplicationStopping;

    // Forwarded headers, request logging, security headers, CORS, error handling, routing and rate limiting, in that order.
    app.UseApiPipeline();
    app.MapHealth();
    app.MapApiEndpoints();
    app.MapOpenApiDocument();

    await app.RunAsync();
    return 0;
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // An invalid setting, for example: written as a structured event, and the process exits with a non-zero code.
    return BootstrapLogger.WriteFatal(exception, ApiAssembly.Assembly, stopping.IsCancellationRequested);
}

/// <summary>Entry point of the API, public so integration tests can host it.</summary>
public partial class Program;
