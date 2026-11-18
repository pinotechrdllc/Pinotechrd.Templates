// Spike: the published Pinotechrd.Security package (1.2.0-preview.2) on one PostgreSQL schema per tenant, as ADR 0001
// designs it: no schema in the model, search_path set per connection on one shared data source. Answers Q1-Q6 and
// prints PASS or FAIL per question. Throwaway measurement code.
using System.Globalization;
using Npgsql;
using Pinotechrd.Spikes.SecuritySchemaPerTenant;
using Testcontainers.PostgreSql;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

await using var pg = new PostgreSqlBuilder("postgres:17.7").Build();
await pg.StartAsync();
var connectionString = pg.GetConnectionString();

// One data source for every tenant. A pool of four forces physical connections to be reused across tenants.
await using var shared = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 4 }.ConnectionString);
await using var services = new ServiceCollection().AddSpikeSecurity(shared)
    .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

await using (var version = shared.CreateCommand("SHOW server_version"))
{
    Console.WriteLine($"PostgreSQL {await version.ExecuteScalarAsync()}, .NET {Environment.Version}, Pinotechrd.Security " +
        typeof(Pinotechrd.Security.Extensions.SecurityExtensions).Assembly.GetName().Version);
}

var outcomes = new List<Outcome>();
Console.WriteLine("Q1. Model");
outcomes.Add(await Q1Model.RunAsync(services, shared));
var users = await Seed.RunAsync(services);
Console.WriteLine("Q2. Behaviour per tenant on one shared data source");
outcomes.Add(await Q2Behaviour.RunAsync(services, shared, connectionString, users));
Console.WriteLine("Q3. Concurrent refresh");
outcomes.Add(await Q3Refresh.RunAsync(shared, users));
Console.WriteLine("Q4. HTTP status codes");
outcomes.Add(await Q4Q6Http.Q4Async(shared));
Console.WriteLine("Q5. Start-up and build-time behaviour");
outcomes.Add(await Q5Startup.RunAsync());
Console.WriteLine("Q6. Tenant resolution middleware");
outcomes.Add(await Q4Q6Http.Q6Async(shared));

Console.WriteLine();
Console.WriteLine("## Results");
foreach (var outcome in outcomes)
{
    Console.WriteLine(outcome.Line());
}

return outcomes.Any(o => o.Required && !o.Passed) ? 1 : 0;
