using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The real host with the Access module, a test-only endpoint that requires <see cref="ReadPermission"/> (the permission the
/// module's checker owns, registered here the way an endpoint's module registers its permissions) and the identity fixture
/// routes. Requests carry a bearer token signed with the fake key of <see cref="AccessJwt"/>.
/// </summary>
internal static class AccessHost
{
    public const string ReadPermission = "Access.Users.Read";

    public const string ReadRoute = "/api/v1/access-fixtures/users-read";

    public static readonly string WhoAmIRoute = IdentityFixtureEndpoint.WhoAmIRoute;

    /// <summary>A host on <paramref name="connectionString"/> with the fixture endpoint and the identity fixture routes.</summary>
    public static ApiHostFactory For(string connectionString) =>
        WithFixtures(new ApiHostFactory().WithDatabase(connectionString));

    /// <summary>A host that never reaches a database (for the requests the authentication refuses first).</summary>
    public static ApiHostFactory WithoutDatabase() => WithFixtures(new ApiHostFactory());

    /// <summary>Adds the fixture endpoint and the identity fixture routes to <paramref name="factory"/>.</summary>
    public static ApiHostFactory WithFixtures(ApiHostFactory factory) =>
        factory
            .WithServices(services => services.AddPermissions(ReadPermission))
            .WithEndpoints(routes => routes.MapGet("/access-fixtures/users-read", () => Results.Ok()).RequirePermission(ReadPermission))
            .WithIdentityFixtures();

    /// <summary>A GET with the bearer token (none when <paramref name="token"/> is null).</summary>
    public static async Task<HttpResponseMessage> GetWithBearerAsync(this HttpClient client, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// A running Access host on a real PostgreSQL database with two tenants, each with an active subscription of a plan that
/// includes the Access module and the permission <see cref="AccessDatabase.ReadCode"/>. Users are added by each test.
/// </summary>
internal sealed class AccessWorld : IDisposable
{
    private AccessWorld(ApiHostFactory factory, Guid tenantA, Guid tenantB)
    {
        Factory = factory;
        TenantA = tenantA;
        TenantB = tenantB;
        Client = factory.CreateClient();
    }

    public ApiHostFactory Factory { get; }

    public HttpClient Client { get; }

    public Guid TenantA { get; }

    public Guid TenantB { get; }

    public IServiceProvider Services => Factory.Services;

    /// <summary>Creates the database, starts the host and seeds the catalog and the module of both tenants.</summary>
    public static async Task<AccessWorld> StartAsync(PostgresFixture postgres, Func<ApiHostFactory, ApiHostFactory>? configure = null)
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var connectionString = await AccessDatabase.CreateAsync(postgres, tenantA, tenantB);
        var factory = AccessHost.For(connectionString);
        if (configure is not null)
        {
            factory = configure(factory);
        }

        var world = new AccessWorld(factory, tenantA, tenantB);
        await AccessDatabase.SeedPlanAsync(world.Services, tenantA, tenantB);
        return world;
    }

    public async Task<Guid> AddUserAsync(Guid tenant, string? email = null) =>
        await AccessDatabase.AddUserAsync(Services, tenant, email ?? $"user-{Guid.NewGuid():N}@example.test");

    public Task<HttpResponseMessage> GetReadAsync(string? token) => Client.GetWithBearerAsync(AccessHost.ReadRoute, token);

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }
}
