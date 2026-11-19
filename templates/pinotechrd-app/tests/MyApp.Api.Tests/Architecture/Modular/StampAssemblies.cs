using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// The stamp module's half of <see cref="ModuleAssemblies"/>. This folder is removed from a clean project, so the stamp's
/// marker types are named only here and in the rules of this folder.
/// </summary>
internal static partial class ModuleAssemblies
{
    /// <summary>The five assemblies of the stamp module, through its marker types.</summary>
    public static IReadOnlyList<Assembly> Stamp =>
    [
        MyApp.Modules.MyModule.Contracts.MyModuleContractsAssembly.Assembly,
        MyApp.Modules.MyModule.Domain.MyModuleDomainAssembly.Assembly,
        MyApp.Modules.MyModule.Application.MyModuleApplicationAssembly.Assembly,
        MyApp.Modules.MyModule.Infrastructure.MyModuleInfrastructureAssembly.Assembly,
        MyApp.Modules.MyModule.Endpoints.MyModuleEndpointsAssembly.Assembly,
    ];
}
