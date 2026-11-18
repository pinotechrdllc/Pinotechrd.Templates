var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

await app.RunAsync();

/// <summary>Entry point of the API, public so integration tests can host it.</summary>
public partial class Program;
