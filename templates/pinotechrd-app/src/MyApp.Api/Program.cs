using MyApp.Api;
using MyApp.BuildingBlocks.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiEndpoints(ApiAssembly.Assembly);
builder.Services.AddHealth();

var app = builder.Build();

app.MapHealth();
app.MapApiEndpoints();

await app.RunAsync();

/// <summary>Entry point of the API, public so integration tests can host it.</summary>
public partial class Program;
