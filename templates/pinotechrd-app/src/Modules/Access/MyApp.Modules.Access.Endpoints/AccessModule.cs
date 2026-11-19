using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.Modules.Access.Application;
using MyApp.Modules.Access.Application.Security;
using MyApp.Modules.Access.Endpoints.Security;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using MyApp.Modules.Access.Infrastructure.Security;
using Pinotechrd.Security.Extensions;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Options;

namespace MyApp.Modules.Access.Endpoints;

/// <summary>
/// The Access module, part of every generated project in both architectures, and its composition root: it wires the
/// security library (JWT bearer as the default scheme, ASP.NET Core Identity, roles and permissions) onto the tenant
/// persistence and the authorization of the web building block. The claim types come from one place,
/// <see cref="ApiIdentityOptions"/> (<c>Identity</c>): the tokens the library issues carry the user claim the API reads
/// (<c>sub</c>) and the tenant claim it reads (<c>tenant_id</c>), and the bearer handler keeps every claim under the name
/// it has in the token. The JWT settings are read from <see cref="JwtSection"/> when first used and validated at start.
/// </summary>
public sealed class AccessModule : IModule
{
    /// <summary>The configuration section of the JWT settings: <c>Secret</c>, <c>Issuer</c>, <c>Audience</c>, lifetimes.</summary>
    public const string JwtSection = "Access:Jwt";

    // Issuer and audience of the throw-away settings of the build-time contract generation: never a running host's.
    private const string GenerationOnly = "contract-generation";

    /// <inheritdoc />
    public string Name => "Access";

    /// <inheritdoc />
    public Assembly EndpointsAssembly => AccessEndpointsAssembly.Assembly;

    /// <inheritdoc />
    public void AddServices(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        AddSecurity(context);
        // After the library, so its password rules are replaced by the module's (the migrator's bootstrap uses the same).
        services.AddAccessPasswordPolicy();

        services.AddAccessPersistence();
        services.AddAccessPermissions();
        services.AddScoped<ISecurityDataProvider, AccessSecurityDataProvider>();
        services.AddScoped<IIdentityValidator<AccessUser>, AccessIdentityValidator>();
        services.AddPermissionChecker<SecurityEnginePermissionChecker>(Name);
        services.AddAuthenticatedRequestStep<SecurityTenantContextStep>();

        // The catalog answers which tenant the X-Tenant header names before login, and whether a token's tenant is active.
        services.AddSharedDbContext<CatalogDbContext>(CatalogDbContext.Schema);
        services.TryAddScoped<ITenantCatalog, CatalogTenantCatalog>();
        services.AddAuthenticatedRequestStep<TenantStatusStep>();

        services.AddHandlers(AccessApplicationAssembly.Assembly);

        // The built-in validation is generated at compile time only for the request types of the endpoints mapped in the
        // project that makes this call, so it stays here, the only call in this project (a second one fails the build,
        // CS8785).
        services.AddValidation();
    }

    // The library, its tenant claim taken from the API's settings (the library reads it once, here; a start check refuses
    // a value changed later), the JWT settings bound lazily, and the bearer handler told to keep the token's claim names.
    private static void AddSecurity(ModuleContext context)
    {
        var services = context.Services;
        var identity = new ApiIdentityOptions();
        context.Configuration.GetSection(ApiIdentityOptions.SectionName).Bind(identity);

        services.AddPinotechrdSecurity<AccessDbContext, AccessUser, AccessRole>(
            security => security.Tenancy.ClaimType = identity.TenantIdClaimType);
        DecorateSecurityStore(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ApiIdentityOptions>, TenantClaimTypeValidator>());

        var configuration = context.Configuration;
        services.Configure<PinotechrdSecurityOptions>(security => configuration.GetSection(JwtSection).Bind(security.Jwt));
        if (context.IsGeneratingApiDocument)
        {
            // The build-time contract generation runs with no configuration: a random key it never uses lets the start-time
            // validation pass there. A running host always needs its own secret.
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            services.PostConfigure<PinotechrdSecurityOptions>(security =>
            {
                security.Jwt.Secret = secret;
                security.Jwt.Issuer = GenerationOnly;
                security.Jwt.Audience = GenerationOnly;
            });
        }

        // sub stays sub: the platform would rename it to the name-identifier URI, and the API reads the claim as written.
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, bearer => bearer.MapInboundClaims = false);
    }

    // The engine reads the per-user overrides from ISecurityStore: the library's registration is wrapped so they follow the
    // rules of the role permissions (see AccessSecurityStore). Its other answers, and its cache, stay the library's.
    private static void DecorateSecurityStore(IServiceCollection services)
    {
        var library = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ISecurityStore) && !descriptor.IsKeyedService)
            ?? throw new InvalidOperationException("The security library registered no ISecurityStore to decorate.");
        services.Remove(library);
        services.AddScoped<ISecurityStore>(provider =>
        {
            var inner = library switch
            {
                { ImplementationFactory: { } factory } => (ISecurityStore)factory(provider),
                { ImplementationInstance: ISecurityStore instance } => instance,
                { ImplementationType: { } type } => (ISecurityStore)ActivatorUtilities.CreateInstance(provider, type),
                _ => throw new InvalidOperationException("The security library's ISecurityStore registration is not supported."),
            };
            return new AccessSecurityStore(inner, provider.GetRequiredService<AccessDbContext>());
        });
    }
}
