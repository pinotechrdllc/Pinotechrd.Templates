using MyApp.Api.Tests.Architecture.Modular.ContractsSamples;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>A module's Contracts assembly holds interfaces, records and enums, and the one assembly marker type.</summary>
public class ModuleContractsShapeTests
{
    // Breaks when a class, a struct, a delegate or any other kind of type is added to the Contracts of a module.
    [Fact]
    public void TheContractsOfEveryModuleHoldOnlyInterfacesRecordsAndEnumsAndTheirMarkerType()
    {
        var violations = ModuleAssemblies.OfLayer("Contracts")
            .SelectMany(m => ContractsShape.Violations(m.Name.Module, ReferenceRules.NameOf(m.Assembly), m.Assembly.GetTypes()))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Guards the rule above against being vacuous: the stamp's Contracts really holds the marker the rule exempts.
    [Fact]
    public void EveryModuleContractsAssemblyHoldsItsMarkerType()
    {
        var contracts = ModuleAssemblies.OfLayer("Contracts");

        Assert.NotEmpty(contracts);
        Assert.All(
            contracts,
            m => Assert.Contains(m.Assembly.GetTypes(), t => t.Name == ContractsShape.MarkerNameOf(m.Name.Module)));
    }

    // Self-tests on synthetic assemblies, made of the sample types below.

    [Fact]
    public void InterfacesRecordsEnumsAndTheMarkerAreAccepted()
    {
        var types = new[]
        {
            typeof(ISample), typeof(SampleRecord), typeof(SampleRecordStruct), typeof(SampleKind), typeof(SampleContractsAssembly),
        };

        Assert.Empty(ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", types));
    }

    [Theory]
    [InlineData(typeof(SampleService), "a class")]
    [InlineData(typeof(SampleAbstract), "an abstract class")]
    [InlineData(typeof(SampleStaticHelper), "a static class")]
    [InlineData(typeof(SamplePlainStruct), "a struct that is not a record")]
    [InlineData(typeof(SampleCallback), "a delegate")]
    public void AnyOtherKindOfTypeIsReportedByName(Type offender, string description)
    {
        var types = new[] { typeof(ISample), typeof(SampleContractsAssembly), offender };

        var violation = Assert.Single(ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", types));

        Assert.Contains(offender.FullName!, violation, StringComparison.Ordinal);
        Assert.Contains(description, violation, StringComparison.Ordinal);
    }

    // The exemption is for the marker by its name pattern and nothing else: the marker of another module is not exempt.
    [Fact]
    public void TheMarkerOfAnotherModuleIsNotExempt()
    {
        var types = new[] { typeof(SampleContractsAssembly), typeof(OtherContractsAssembly) };

        var violation = Assert.Single(ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", types));

        Assert.Contains(typeof(OtherContractsAssembly).FullName!, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondTypeWithTheNameOfTheMarkerIsReported()
    {
        var types = new[] { typeof(SampleContractsAssembly), typeof(Impostor.SampleContractsAssembly) };

        var violations = ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", types);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, m => m.Contains("exactly one marker", StringComparison.Ordinal));
    }

    [Fact]
    public void AMarkerThatIsNotAStaticClassIsReported()
    {
        var violation = Assert.Single(ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", [typeof(Impostor.SampleContractsAssembly)]));

        Assert.Contains("static class", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssemblyWithoutItsMarkerIsReported()
    {
        var violation = Assert.Single(ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", [typeof(ISample)]));

        Assert.Contains("SampleContractsAssembly", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeTheCompilerGeneratesIsNotATypeOfTheContract()
    {
        var generated = typeof(ContractsSamplesCompilerGenerated)
            .GetNestedTypes(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        Assert.NotEmpty(generated);
        Assert.Empty(ContractsShape.Violations("Sample", "Acme.Modules.Sample.Contracts", [typeof(SampleContractsAssembly), .. generated]));
    }
}
