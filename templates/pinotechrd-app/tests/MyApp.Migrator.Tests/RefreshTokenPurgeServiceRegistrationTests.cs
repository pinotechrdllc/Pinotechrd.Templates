using Microsoft.Extensions.Hosting;
using MyApp.Modules.Access.Infrastructure.Security;

namespace MyApp.Migrator.Tests;

// The refresh token purge runs in the API only: the migrator is a deploy step that exits, and must not purge.
public class RefreshTokenPurgeServiceRegistrationTests
{
    // Breaks when any of the migrator's compositions (migrate, provision, bootstrap) registers the purge service.
    [Fact]
    public void TheMigratorHostDoesNotRegisterThePurgeService()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddMigrations();
        builder.AddProvisioning();
        builder.AddBootstrap();

        Assert.DoesNotContain(builder.Services, d => d.ImplementationType == typeof(RefreshTokenPurgeService));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(RefreshTokenPurgeService));
    }
}
