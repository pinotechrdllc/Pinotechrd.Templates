using MyApp.Api;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Problems;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblems();
// Built-in validation is generated at compile time for the request types of the endpoints mapped in the project that
// calls AddValidation(), wherever those types are declared (MyApp.Contracts included). A project of its own that maps
// endpoints (such as a module's endpoints) must call AddValidation() in its own registration; the calls add up.
builder.Services.AddValidation();
builder.Services.AddApiEndpoints(ApiAssembly.Assembly);
builder.Services.AddHealth();

var app = builder.Build();

app.UseProblems();
app.MapHealth();
app.MapApiEndpoints();

await app.RunAsync();

/// <summary>Entry point of the API, public so integration tests can host it.</summary>
public partial class Program;
