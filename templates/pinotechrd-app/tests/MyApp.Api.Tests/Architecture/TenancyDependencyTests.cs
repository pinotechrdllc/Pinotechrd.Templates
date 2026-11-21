using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// The dependency rules of tenant persistence. They hold in every architecture and name no assembly that exists in one
/// only. The infrastructure building block now owns EF Core on the shared data source; nothing else gains data access.
/// </summary>
public class TenancyDependencyTests
{
    // Everything outside the base class library that the infrastructure building block may reference, by exact name. The
    // Microsoft.Extensions.* abstractions (configuration, dependency injection, hosting, health checks, options) are
    // allowed as a family: none of them carries ASP.NET Core.
    private static readonly string[] AllowedInfrastructureReferences =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Relational",
        "Npgsql",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "EFCore.NamingConventions",
        "System.Data.Common",
    ];

    private static readonly string[] DataAccessPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "EFCore",
        "Microsoft.Data",
        "System.Data",
        "Dapper",
    ];

    // Breaks when the infrastructure building block references anything outside the exact set: another EF Core package (a
    // provider, the design package, abstractions), another database library, ASP.NET Core, the web building block, a
    // business assembly or any other solution assembly than the application and domain building blocks.
    [Fact]
    public void InfrastructureBuildingBlocksReferenceExactlyTheTenantPersistenceSetAndNothingElse()
    {
        var violations = ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksInfrastructure)
            .Where(name => !ReferenceRules.IsSolutionAssembly(name)
                && !ReferenceRules.IsBaseClassLibrary(name)
                && !AllowedInfrastructureReferences.Contains(name, StringComparer.Ordinal)
                && !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .Select(name => $"The infrastructure building block must not reference '{name}'.")
            .Concat(ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksInfrastructure, [ReferenceRules.BuildingBlocksWeb], ["Microsoft.AspNetCore"]))
            .Concat(ReferenceRules.SolutionReferenceViolations(
                ReferenceRules.BuildingBlocksInfrastructure,
                ReferenceRules.BuildingBlocksApplication,
                ReferenceRules.BuildingBlocksDomain))
            .Concat(ReferenceRules.ViolationsByName(ReferenceRules.BuildingBlocksInfrastructure, BusinessAssemblies.Discover(), []))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Guards the rule above against being vacuous: the checker sees each member of the set where it is really used.
    [Fact]
    public void InfrastructureBuildingBlocksActuallyReferenceEfCoreNpgsqlEfCoreNamingConventionsAndTheApplicationBuildingBlock()
    {
        var names = ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksInfrastructure);

        Assert.Contains("Microsoft.EntityFrameworkCore", names);
        Assert.Contains("Microsoft.EntityFrameworkCore.Relational", names);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", names);
        Assert.Contains("EFCore.NamingConventions", names);
        Assert.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksApplication), names);
    }

    // Breaks when the rule above passes things it should not: the same checker, asked about the web building block, which
    // really references ASP.NET Core and is outside the set.
    [Fact]
    public void TheExactSetIsNotMetByTheWebBuildingBlock()
    {
        var outside = ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksWeb)
            .Where(name => !ReferenceRules.IsSolutionAssembly(name)
                && !ReferenceRules.IsBaseClassLibrary(name)
                && !AllowedInfrastructureReferences.Contains(name, StringComparer.Ordinal)
                && !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(outside, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    // Breaks when the application building block gains a data-access dependency (EF Core, Npgsql, the naming convention,
    // ADO.NET, Dapper), directly.
    [Fact]
    public void ApplicationBuildingBlocksReferenceNoEfCoreAndNoNpgsqlAndNoOtherDataAccess()
    {
        var violations = ReferenceRules.Violations(ReferenceRules.BuildingBlocksApplication, [], DataAccessPrefixes);

        ReferenceRules.AssertNone(violations);
    }

    // Breaks when the tenant contract (what a use case reads) moves out of the application building block, or when its
    // public surface exposes a type of a data-access library to its callers.
    [Fact]
    public void TheTenantContractLivesInTheApplicationBuildingBlockAndExposesNoDataAccessType()
    {
        Assert.Same(ReferenceRules.BuildingBlocksApplication, typeof(ICurrentTenant).Assembly);
        // The initializer is infrastructure: business code reads the tenant and never chooses it.
        Assert.Same(ReferenceRules.BuildingBlocksInfrastructure, typeof(ICurrentTenantInitializer).Assembly);

        var exposed = ReferenceRules.BuildingBlocksApplication.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => TypesOf(m).Select(x => (Type: t, Member: m, Used: x))))
            .Where(x => DataAccessPrefixes.Any(p => ReferenceRules.HasPrefix(x.Used.Namespace ?? string.Empty, p)))
            .Select(x => $"{x.Type.FullName}.{x.Member.Name} exposes {x.Used.FullName}.")
            .ToList();

        ReferenceRules.AssertNone(exposed);
    }

    // Breaks when the migrator contract or the module contract needs ASP.NET Core to be used (a web type in a signature, or the
    // module contract moving to the web building block), which a plain host that only migrates could not satisfy.
    [Fact]
    public void ModuleMigrationsAndTheMigratorExposeNoAspNetCoreType()
    {
        Type[] contracts = [typeof(IModuleMigrations), typeof(ITenantMigrator), typeof(TenantMigrationResult), typeof(ModuleMigrationResult), typeof(TenantPersistenceRegistration), typeof(TenantSchemaName)];

        Assert.All(contracts, t => Assert.Same(ReferenceRules.BuildingBlocksInfrastructure, t.Assembly));
        var web = contracts
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => TypesOf(m)
                    .Where(x => (x.Namespace ?? string.Empty).StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
                    .Select(x => $"{t.FullName}.{m.Name} exposes {x.FullName}.")))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(web);
    }

    // Business code reads the tenant and never chooses it: the initializer is for the code that knows the tenant from a
    // trusted source. Only the host, the infrastructure building block and test assemblies may use the type. The check reads
    // the metadata of the compiled assemblies (every type reference, which any use of the type or of one of its members
    // leaves), not only the assembly references.
    // Breaks when the domain or application building block, the web building block or a business assembly uses the type.
    [Fact]
    public void OnlyTheHostTheInfrastructureBuildingBlockAndTestsReferenceTheTenantInitializer()
    {
        List<Assembly> candidates =
        [
            ReferenceRules.BuildingBlocksDomain,
            ReferenceRules.BuildingBlocksApplication,
            ReferenceRules.BuildingBlocksWeb,
            .. BusinessAssemblies.Load(BusinessAssemblies.Discover()),
        ];

        var violations = candidates
            .Where(a => ReferencesType(a.Location, typeof(ICurrentTenantInitializer)))
            .Select(a => $"Assembly '{ReferenceRules.NameOf(a)}' must not use {typeof(ICurrentTenantInitializer).FullName}.")
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Guards the rule above against being vacuous: the checker finds the type in an assembly that really uses it (this test
    // assembly does), finds a type by its exact name, and does not report an assembly that does not use it.
    [Fact]
    public void TheTypeReferenceCheckerFindsAKnownReferenceAndNothingElse()
    {
        var tests = typeof(TenancyDependencyTests).Assembly.Location;

        Assert.True(ReferencesType(tests, typeof(ICurrentTenantInitializer)));
        Assert.True(ReferencesType(tests, typeof(ICurrentTenant)));
        Assert.False(ReferencesType(ReferenceRules.BuildingBlocksDomain.Location, typeof(ICurrentTenantInitializer)));
        Assert.False(ReferencesType(tests, typeof(TenancyDependencyTests)));
    }

    // Background work for tenants goes through the job runner, which takes the tenants from the directory: business code
    // supplies the work and never the tenant, so it cannot run work as a tenant it chose.
    // Breaks when a public member of ITenantJobRunner takes a tenant id: a Guid, Guid? or string parameter (a tenant id or
    // its text form; alone or inside a collection, tuple or other generic), or a delegate parameter whose inputs include
    // one (Func<Guid, ...>, Func<string, ...>).
    [Fact]
    public void NoPublicMemberOfTheTenantJobRunnerTakesATenantId()
    {
        ReferenceRules.AssertNone(TenantIdParameters(typeof(ITenantJobRunner)));
    }

    // Breaks when a public type of the infrastructure building block's tenancy namespace (other than the initializer itself)
    // gains a way to run caller-supplied work as a caller-chosen tenant: a public method or constructor that takes a delegate
    // together with a tenant id (a Guid or Guid?), or a delegate whose inputs include a tenant id (a Guid, Guid? or string).
    // Types that only name a tenant (the schema and role names, the migrator, the result records) take no work and are not
    // caught. A string beside the work is not taken for a tenant id here: the registrations take a module name and a
    // configuration callback (AddTenantDbContext, UseTenantPersistence); on the runner itself a string is refused.
    [Fact]
    public void NoPublicTypeOfTheTenancyNamespaceRunsWorkForACallerChosenTenant()
    {
        ReferenceRules.AssertNone([.. TenancyPublicTypes().SelectMany(TenantChoosingWorkMembers)]);
    }

    // Guards the two rules above against being vacuous: the scan covers the runner (and the namespace's other public types,
    // not the initializer), the runner really has a delegate parameter the checker inspects, and the checkers flag the
    // shapes they forbid (a tenant id beside the work, a tenant id given to the work) while passing the runner's own shape.
    [Fact]
    public void TheTenantIdCheckersFlagAHypotheticalPerTenantRunnerAndPassTheRunnersShape()
    {
        var scanned = TenancyPublicTypes();
        Assert.Contains(typeof(ITenantJobRunner), scanned);
        Assert.Contains(typeof(ITenantMigrator), scanned);
        Assert.DoesNotContain(typeof(ICurrentTenantInitializer), scanned);
        Assert.Contains(
            PublicMembers(typeof(ITenantJobRunner)).SelectMany(m => m.GetParameters()),
            p => typeof(Delegate).IsAssignableFrom(p.ParameterType));

        var choosing = TenantIdParameters(typeof(IHypotheticalTenantRunner));
        Assert.Contains(choosing, v => v.Contains(nameof(IHypotheticalTenantRunner.RunForTenantAsync), StringComparison.Ordinal));
        Assert.Contains(choosing, v => v.Contains(nameof(IHypotheticalTenantRunner.RunForTenantsAsync), StringComparison.Ordinal));
        Assert.Contains(choosing, v => v.Contains(nameof(IHypotheticalTenantRunner.ForEachWithIdAsync), StringComparison.Ordinal));
        Assert.Contains(choosing, v => v.Contains(nameof(IHypotheticalTenantRunner.RunForOptionalTenantAsync), StringComparison.Ordinal));
        Assert.Contains(choosing, v => v.Contains(nameof(IHypotheticalTenantRunner.RunForTenantTextAsync), StringComparison.Ordinal));
        Assert.Contains(choosing, v => v.Contains(nameof(IHypotheticalTenantRunner.ForEachWithTextIdAsync), StringComparison.Ordinal));
        var work = TenantChoosingWorkMembers(typeof(IHypotheticalTenantRunner)).ToList();
        // Every shape but the string beside the work, which the namespace-wide rule leaves to the runner's own rule.
        Assert.Equal(5, work.Count);
        Assert.DoesNotContain(work, v => v.Contains(nameof(IHypotheticalTenantRunner.RunForTenantTextAsync), StringComparison.Ordinal));

        Assert.Empty(TenantIdParameters(typeof(ICompliantTenantRunner)));
        Assert.Empty(TenantChoosingWorkMembers(typeof(ICompliantTenantRunner)));
        // A type that only names a tenant, with no work, is not mistaken for a runner.
        Assert.NotEmpty(TenantIdParameters(typeof(ITenantMigrator)));
        Assert.Empty(TenantChoosingWorkMembers(typeof(ITenantMigrator)));
    }

    // The shapes the rules forbid, for the guard above; never implemented.
    private interface IHypotheticalTenantRunner
    {
        Task RunForTenantAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken);

        Task RunForTenantsAsync(IReadOnlyList<Guid> tenantIds, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken);

        Task ForEachWithIdAsync(Func<Guid, IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken);

        Task RunForOptionalTenantAsync(Guid? tenantId, Action<IServiceProvider> work);

        Task RunForTenantTextAsync(string tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken);

        Task ForEachWithTextIdAsync(Func<string, IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken);
    }

    // The runner's own shape, for the guard above; never implemented.
    private interface ICompliantTenantRunner
    {
        Task<IReadOnlyList<TenantJobOutcome<T>>> ForEachTenantAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    }

    private static List<Type> TenancyPublicTypes() =>
        [.. ReferenceRules.BuildingBlocksInfrastructure.GetExportedTypes()
            .Where(t => t.Namespace == typeof(ITenantJobRunner).Namespace && t != typeof(ICurrentTenantInitializer))];

    private static IEnumerable<MethodBase> PublicMembers(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

    private static List<string> TenantIdParameters(Type type) =>
        [.. PublicMembers(type)
            .SelectMany(m => m.GetParameters().Where(p => TakesTenantId(p.ParameterType, strings: true))
                .Select(p => $"{type.FullName}.{m.Name} takes a tenant id: parameter '{p.Name}' ({p.ParameterType})."))];

    private static IEnumerable<string> TenantChoosingWorkMembers(Type type) =>
        PublicMembers(type)
            .Where(m =>
            {
                var parameters = m.GetParameters();
                var delegates = parameters.Where(p => IsDelegate(p.ParameterType)).ToList();
                return delegates.Any(p => TakesTenantId(p.ParameterType, strings: true))
                    || (delegates.Count > 0 && parameters.Any(p => !IsDelegate(p.ParameterType) && TakesTenantId(p.ParameterType, strings: false)));
            })
            .Select(m => $"{type.FullName}.{m.Name} runs work for a tenant its caller names.");

    private static bool IsDelegate(Type type) => typeof(Delegate).IsAssignableFrom(type);

    // A Guid or Guid? (and, with strings, a string: a tenant id in text form) anywhere in the type (an element, a generic
    // argument), or, for a delegate, in one of its inputs, where a string always counts. What a delegate returns is not an
    // input, and a generic parameter (T) is not a tenant id. Only parameters are checked: a member that returns a string
    // (TenantSchemaName.For, TenantRoleName.For) is not caught for that.
    private static bool TakesTenantId(Type type, bool strings)
    {
        if (type.HasElementType)
        {
            return TakesTenantId(type.GetElementType()!, strings);
        }

        if (type == typeof(Guid) || (strings && type == typeof(string)))
        {
            return true;
        }

        if (IsDelegate(type) && type != typeof(Delegate) && type != typeof(MulticastDelegate))
        {
            return type.GetMethod("Invoke")!.GetParameters().Any(p => TakesTenantId(p.ParameterType, strings: true));
        }

        return type.IsGenericType && type.GetGenericArguments().Any(a => TakesTenantId(a, strings));
    }

    private static bool ReferencesType(string assemblyPath, Type type)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeReferences
            .Select(metadata.GetTypeReference)
            .Any(r => metadata.GetString(r.Name) == type.Name && metadata.GetString(r.Namespace) == type.Namespace);
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member) =>
        member switch
        {
            MethodBase method => method.GetParameters().Select(p => p.ParameterType)
                .Concat(method is MethodInfo info ? [info.ReturnType] : []).SelectMany(Unwrap),
            PropertyInfo property => Unwrap(property.PropertyType),
            FieldInfo field => Unwrap(field.FieldType),
            EventInfo @event => @event.EventHandlerType is { } handler ? Unwrap(handler) : [],
            _ => [],
        };

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType)
        {
            return Unwrap(type.GetElementType()!);
        }

        return type.IsGenericType ? [type, .. type.GetGenericArguments().SelectMany(Unwrap)] : [type];
    }
}
