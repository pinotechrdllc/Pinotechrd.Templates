using System.Reflection;

namespace MyApp.Api.Tests.Architecture.Clean;

/// <summary>
/// The five layer assemblies of a clean project. This folder is removed from a modular project, so the layers are named
/// only here and in the rules of this folder.
/// </summary>
internal static class CleanLayers
{
    public static Assembly Domain => MyApp.Domain.DomainAssembly.Assembly;

    public static Assembly Application => MyApp.Application.ApplicationAssembly.Assembly;

    public static Assembly Contracts => MyApp.Contracts.ContractsAssembly.Assembly;

    public static Assembly Resources => MyApp.Resources.ResourcesAssembly.Assembly;

    public static Assembly Infrastructure => MyApp.Infrastructure.InfrastructureAssembly.Assembly;

    public static IReadOnlyList<Assembly> All => [Domain, Application, Contracts, Resources, Infrastructure];

    /// <summary>Every assembly of a clean solution that a rule may name: the layers, the host and the building blocks.</summary>
    public static IReadOnlyList<Assembly> AllSolutionAssemblies =>
        [.. All, ReferenceRules.Api, .. ReferenceRules.BuildingBlocks];
}
