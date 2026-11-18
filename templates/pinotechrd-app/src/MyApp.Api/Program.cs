using MyApp.Api;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.OpenApi;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblems();
builder.Services.AddHttpSecurity();
// Built-in validation is generated at compile time for the request types of the endpoints mapped in the project that
// calls AddValidation(), wherever those types are declared (MyApp.Contracts included). A project of its own that maps
// endpoints (such as a module's endpoints) must call AddValidation() in its own registration; the calls add up.
builder.Services.AddValidation();
builder.Services.AddApiEndpoints(ApiAssembly.Assembly);
builder.Services.AddHealth();
builder.Services.AddOpenApiDocument();

var app = builder.Build();

// Forwarded headers, security headers, CORS, error handling, routing and rate limiting, in that order.
app.UseApiPipeline();
app.MapHealth();
app.MapApiEndpoints();
app.MapOpenApiDocument();

await app.RunAsync();

/// <summary>Entry point of the API, public so integration tests can host it.</summary>
public partial class Program;
