using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Access.Auth;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Pinotechrd.Security.Core;
using Pinotechrd.Security.Interfaces;
using Serilog.Events;

namespace MyApp.Api.Tests.Access;

// What the upgrade of the security library to 1.2.0-preview.5 must keep: the host starts outside Development with the
// template's own stores (the library refuses its in-memory ones there), the library's EF stores and session purger stay out,
// and a replayed refresh token is reported by the library's event 4204 with the ids the template's store returns.
public class SecurityUpgradeTests
{
    private const string LibraryEventsCategory = "Pinotechrd.Security.Events";

    private const int RefreshTokenReusedEvent = 4204;

    public static TheoryData<string> EnvironmentsOutsideDevelopment() => [Environments.Production, Environments.Staging];

    // Breaks when the library's start check finds an in-memory refresh token or two-factor attempt store outside
    // Development (the host would refuse to start in every real deployment), or when the template's stores are not the ones
    // resolved there.
    [Theory]
    [MemberData(nameof(EnvironmentsOutsideDevelopment))]
    public async Task TheHostStartsOutsideDevelopmentWithTheTemplatesStores(string environment)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(HealthRegistration.LivePath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.IsType<DisabledTwoFactorAttemptStore>(factory.Services.GetRequiredService<ITwoFactorAttemptStore>());
        await using var scope = factory.Services.CreateAsyncScope();
        AccessDatabase.EnterTenant(scope.ServiceProvider, Guid.NewGuid());
        Assert.IsType<DatabaseRefreshTokenStore>(scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>());
    }

    // Breaks when the library's durable-state check is not active (an in-memory two-factor store would go unnoticed in
    // Production), i.e. when the template's DisabledTwoFactorAttemptStore is not what lets the host start.
    [Fact]
    public void AProductionHostWithTheLibrarysInMemoryTwoFactorStoreRefusesToStart()
    {
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production)
            .WithServices(services =>
            {
                services.RemoveAll<ITwoFactorAttemptStore>();
                services.AddSingleton<ITwoFactorAttemptStore, InMemoryTwoFactorAttemptStore>();
            });

        StartupFailure.AssertFailsNaming(factory, "ITwoFactorAttemptStore is the in-memory store");
    }

    // Breaks when the library's EF refresh token store, its EF two-factor store or its session purger is registered (they
    // use the same table names as the template's own refresh token tables), or when the template's stores are not the last
    // registrations, the ones the container resolves.
    [Fact]
    public void TheEffectiveStoresAreTheTemplatesAndNoLibrarySessionPurgerIsRegistered()
    {
        IServiceCollection registrations = null!;
        using var factory = AccessHost.WithoutDatabase().WithServices(services => registrations = services);
        using var client = factory.CreateClient();

        var refresh = registrations.Where(d => d.ServiceType == typeof(IRefreshTokenStore) && !d.IsKeyedService).ToArray();
        Assert.Equal(typeof(DatabaseRefreshTokenStore), refresh.Last().ImplementationType);
        Assert.DoesNotContain(refresh, d => d.ImplementationType?.Name.StartsWith("EfRefreshTokenStore", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(registrations, d => d.ServiceType == typeof(IPinotechrdSessionPurger));
        Assert.DoesNotContain(
            registrations,
            d => d.ServiceType == typeof(ITwoFactorAttemptStore) && d.ImplementationType?.Name.StartsWith("EfTwoFactorAttemptStore", StringComparison.Ordinal) == true);
        Assert.IsType<DisabledTwoFactorAttemptStore>(factory.Services.GetRequiredService<ITwoFactorAttemptStore>());
        Assert.Null(factory.Services.GetService<IPinotechrdSessionPurger>());
    }

    // Breaks when the library's refresh token or two-factor attempt entities are mapped into the Access context: the
    // library's refresh_tokens / refresh_token_families would collide with the template's tables of the same names, and a
    // two_factor_attempts table would be created by the migrations.
    [Fact]
    public void TheAccessModelMapsTheRefreshTokenTablesOnlyToTheTemplatesEntitiesAndHasNoTwoFactorAttemptTable()
    {
        using var context = new AccessDbContextFactory().CreateDbContext([]);
        var tables = context.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Select(e => (Table: e.GetTableName()!, e.ClrType))
            .ToArray();

        Assert.Equal(new[] { typeof(RefreshToken) }, tables.Where(t => t.Table == "refresh_tokens").Select(t => t.ClrType));
        Assert.Equal(new[] { typeof(RefreshTokenFamily) }, tables.Where(t => t.Table == "refresh_token_families").Select(t => t.ClrType));
        Assert.DoesNotContain(tables, t => t.Table == "two_factor_attempts");
        Assert.DoesNotContain(tables, t => t.ClrType.Assembly == typeof(InMemoryTwoFactorAttemptStore).Assembly
            && t.ClrType.Name is "RefreshTokenRecord" or "RefreshTokenFamilyRecord" or "TwoFactorAttemptRecord");
    }

    /// <summary>The id of the event's <c>EventId</c>, as Microsoft.Extensions.Logging wrote it, or null.</summary>
    internal static int? EventIdOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("EventId", out var value) && value is StructureValue structure
            && structure.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int id }
            ? id
            : null;

    // The tests that need a database.
    [Collection(PostgresContainer.Name)]
    public class WithADatabase(PostgresFixture postgres)
    {
        // Breaks when a replayed spent refresh token does not end its family (the token rotated from it still works), when the
        // library's event 4204 is not written, or when it lacks the user, family or tenant id the store must now return, or
        // carries a token value.
        [Fact]
        public async Task ReplayingASpentRefreshTokenRevokesItsFamilyAndLogsEvent4204WithTheIdsButNoToken()
        {
            var sink = default(CollectingSink)!;
            using var world = await AccessWorld.StartAsync(postgres, f =>
            {
                sink = f.CaptureLogs();
                return f;
            });
            var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
            using var client = world.Factory.AuthClient();
            var (access, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
            using var rotated = await client.RefreshAsync(AccessWorld.SlugA, first);
            var second = rotated.RefreshTokenOf();
            var rotatedAccess = await rotated.AccessTokenOfAsync();

            using var replay = await client.RefreshAsync(AccessWorld.SlugA, first);
            using var afterReplay = await client.RefreshAsync(AccessWorld.SlugA, second);

            await AuthScenarios.AssertRefreshRefusedAsync(replay);
            await AuthScenarios.AssertRefreshRefusedAsync(afterReplay);
            var family = Guid.Empty;
            DateTime? revokedAt = null;
            await AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
            {
                var row = await scope.GetRequiredService<AccessDbContext>().RefreshTokenFamilies.AsNoTracking()
                    .SingleAsync(f => f.UserId == user, TestContext.Current.CancellationToken);
                (family, revokedAt) = (row.Id, row.RevokedAt);
            });
            Assert.NotNull(revokedAt);

            var reused = Assert.Single(sink.Events, e => EventIdOf(e) == RefreshTokenReusedEvent);
            Assert.True(reused.IsFrom(LibraryEventsCategory), $"Event 4204 came from {reused.CategoryOf()}.");
            Assert.Equal(LogEventLevel.Warning, reused.Level);
            Assert.Equal(user.ToString("D"), reused.ScalarOf("UserId"));
            Assert.Equal(family.ToString("D"), reused.ScalarOf("FamilyId"));
            Assert.Equal(world.TenantA.ToString("D"), reused.ScalarOf("TenantId"));
            foreach (var text in reused.TextsOf())
            {
                foreach (var secret in new[] { first, second, access, rotatedAccess })
                {
                    Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                }
            }
        }
    }
}
