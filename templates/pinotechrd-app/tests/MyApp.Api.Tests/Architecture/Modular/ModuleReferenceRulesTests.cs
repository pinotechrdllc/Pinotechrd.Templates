using System.Xml.Linq;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// Self-tests of the module rules on synthetic reference graphs, names only. A generated project has one module and
/// its projects reference almost nothing yet, so the rules on the real assemblies have little to look at: these prove
/// that each side of each rule can fail and that what the README allows is accepted.
/// </summary>
public class ModuleReferenceRulesTests
{
    private const string Root = "Acme";
    private const string SalesContracts = "Acme.Modules.Sales.Contracts";
    private const string SalesDomain = "Acme.Modules.Sales.Domain";
    private const string SalesApplication = "Acme.Modules.Sales.Application";
    private const string SalesInfrastructure = "Acme.Modules.Sales.Infrastructure";
    private const string SalesEndpoints = "Acme.Modules.Sales.Endpoints";
    private const string BillingContracts = "Acme.Modules.Billing.Contracts";
    private const string BillingDomain = "Acme.Modules.Billing.Domain";
    private const string BillingApplication = "Acme.Modules.Billing.Application";
    private const string BillingInfrastructure = "Acme.Modules.Billing.Infrastructure";
    private const string BillingEndpoints = "Acme.Modules.Billing.Endpoints";
    private const string DomainBlock = "Acme.BuildingBlocks.Domain";
    private const string ApplicationBlock = "Acme.BuildingBlocks.Application";
    private const string InfrastructureBlock = "Acme.BuildingBlocks.Infrastructure";
    private const string WebBlock = "Acme.BuildingBlocks.Web";
    private const string Host = "Acme.Api";

    // Inside a module (criterion: layers). Each row is one reference from one project and whether the rule reports it.
    [Theory]
    [InlineData(SalesContracts, DomainBlock, true)]
    [InlineData(SalesContracts, SalesDomain, true)]
    [InlineData(SalesContracts, BillingContracts, true)]
    [InlineData(SalesContracts, "System.Runtime", false)]
    [InlineData(SalesDomain, DomainBlock, false)]
    [InlineData(SalesDomain, ApplicationBlock, true)]
    [InlineData(SalesDomain, SalesContracts, true)]
    [InlineData(SalesDomain, BillingContracts, true)]
    [InlineData(SalesDomain, Host, true)]
    [InlineData(SalesApplication, SalesDomain, false)]
    [InlineData(SalesApplication, SalesContracts, false)]
    [InlineData(SalesApplication, DomainBlock, false)]
    [InlineData(SalesApplication, ApplicationBlock, false)]
    [InlineData(SalesApplication, BillingContracts, false)]
    [InlineData(SalesApplication, BillingDomain, true)]
    [InlineData(SalesApplication, SalesInfrastructure, true)]
    [InlineData(SalesApplication, SalesEndpoints, true)]
    [InlineData(SalesApplication, InfrastructureBlock, true)]
    [InlineData(SalesApplication, WebBlock, true)]
    [InlineData(SalesApplication, Host, true)]
    [InlineData(SalesInfrastructure, SalesApplication, false)]
    [InlineData(SalesInfrastructure, SalesDomain, false)]
    [InlineData(SalesInfrastructure, SalesContracts, false)]
    [InlineData(SalesInfrastructure, DomainBlock, false)]
    [InlineData(SalesInfrastructure, ApplicationBlock, false)]
    [InlineData(SalesInfrastructure, InfrastructureBlock, false)]
    [InlineData(SalesInfrastructure, WebBlock, true)]
    [InlineData(SalesInfrastructure, SalesEndpoints, true)]
    [InlineData(SalesInfrastructure, BillingApplication, true)]
    [InlineData(SalesInfrastructure, Host, true)]
    [InlineData(SalesEndpoints, SalesContracts, false)]
    [InlineData(SalesEndpoints, SalesDomain, false)]
    [InlineData(SalesEndpoints, SalesApplication, false)]
    [InlineData(SalesEndpoints, SalesInfrastructure, false)]
    [InlineData(SalesEndpoints, WebBlock, false)]
    [InlineData(SalesEndpoints, InfrastructureBlock, false)]
    [InlineData(SalesEndpoints, DomainBlock, false)]
    [InlineData(SalesEndpoints, ApplicationBlock, false)]
    [InlineData(SalesEndpoints, BillingContracts, false)]
    [InlineData(SalesEndpoints, BillingInfrastructure, true)]
    [InlineData(SalesEndpoints, Host, true)]
    [InlineData(SalesEndpoints, "Microsoft.AspNetCore.Http.Abstractions", false)]
    public void AModuleProjectMayReferenceOnlyWhatItsLayerAllows(string assembly, string reference, bool reported)
    {
        var violations = ModuleReferenceRules.LayerViolations(assembly, [reference], Root);

        Assert.Equal(reported, violations.Count > 0);
        Assert.All(violations, m => Assert.Contains(reference, m, StringComparison.Ordinal));
    }

    [Fact]
    public void TheLayerRuleSaysNothingAboutAnAssemblyThatIsNotAModuleProject()
    {
        Assert.Empty(ModuleReferenceRules.LayerViolations(Host, [SalesDomain, BillingInfrastructure], Root));
        Assert.Empty(ModuleReferenceRules.LayerViolations(WebBlock, [SalesDomain], Root));
    }

    // Between modules (criterion: across modules): the Contracts of another module only.
    [Fact]
    public void AReferenceToTheContractsOfAnotherModuleIsAllowed()
    {
        Assert.Empty(ModuleReferenceRules.CrossModuleViolations(SalesApplication, [BillingContracts, SalesDomain, DomainBlock, "System.Runtime"], Root));
    }

    [Theory]
    [InlineData(BillingDomain)]
    [InlineData(BillingApplication)]
    [InlineData(BillingInfrastructure)]
    [InlineData(BillingEndpoints)]
    public void AReferenceToAnythingButTheContractsOfAnotherModuleIsReportedWithBothModules(string reference)
    {
        var violation = Assert.Single(ModuleReferenceRules.CrossModuleViolations(SalesApplication, [BillingContracts, reference], Root));

        Assert.Contains("Sales", violation, StringComparison.Ordinal);
        Assert.Contains("Billing", violation, StringComparison.Ordinal);
        Assert.Contains(reference, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ACrossModuleReferenceIsReportedFromEveryProjectOfTheModule()
    {
        foreach (var assembly in new[] { SalesContracts, SalesDomain, SalesApplication, SalesInfrastructure, SalesEndpoints })
        {
            Assert.NotEmpty(ModuleReferenceRules.CrossModuleViolations(assembly, [BillingDomain], Root));
        }
    }

    [Fact]
    public void TheCrossModuleRuleDoesNotApplyToTheHostOrABuildingBlock()
    {
        Assert.Empty(ModuleReferenceRules.CrossModuleViolations(Host, [SalesEndpoints, BillingEndpoints], Root));
        Assert.Empty(ModuleReferenceRules.CrossModuleViolations(WebBlock, [SalesEndpoints], Root));
    }

    // Who may reference the Endpoints and the Infrastructure of a module (criterion: access).
    [Theory]
    [InlineData(Host, SalesEndpoints, false)]
    [InlineData(SalesApplication, SalesEndpoints, true)]
    [InlineData(SalesInfrastructure, SalesEndpoints, true)]
    [InlineData(BillingEndpoints, SalesEndpoints, true)]
    [InlineData(WebBlock, SalesEndpoints, true)]
    [InlineData(SalesEndpoints, SalesInfrastructure, false)]
    [InlineData(BillingEndpoints, SalesInfrastructure, true)]
    [InlineData(SalesApplication, SalesInfrastructure, true)]
    [InlineData(SalesDomain, SalesInfrastructure, true)]
    [InlineData(Host, SalesInfrastructure, true)]
    [InlineData(InfrastructureBlock, SalesInfrastructure, true)]
    [InlineData(Host, SalesDomain, false)]
    public void OnlyTheHostReferencesTheEndpointsOfAModuleAndOnlyItsOwnEndpointsReferenceItsInfrastructure(
        string assembly, string reference, bool reported)
    {
        var violations = ModuleReferenceRules.AccessViolations(assembly, [reference], Root);

        Assert.Equal(reported, violations.Count > 0);
        Assert.All(violations, m => Assert.Contains(reference, m, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DomainBlock, false)]
    [InlineData(WebBlock, true)]
    [InlineData(InfrastructureBlock, true)]
    public void ABuildingBlockThatReferencesAModuleIsReported(string buildingBlock, bool referencesModule)
    {
        var references = referencesModule ? new[] { DomainBlock, SalesContracts } : [DomainBlock];

        var violations = ModuleReferenceRules.BuildingBlockViolations(buildingBlock, references, Root);

        Assert.Equal(referencesModule, violations.Count == 1);
        Assert.All(violations, m => Assert.Contains(SalesContracts, m, StringComparison.Ordinal));
    }

    [Fact]
    public void TheBuildingBlockRuleDoesNotApplyToTheHostOrAModule()
    {
        Assert.Empty(ModuleReferenceRules.BuildingBlockViolations(Host, [SalesEndpoints], Root));
        Assert.Empty(ModuleReferenceRules.BuildingBlockViolations(BillingApplication, [BillingDomain], Root));
    }

    [Theory]
    [InlineData(SalesEndpoints, false)]
    [InlineData(SalesContracts, true)]
    [InlineData(SalesDomain, true)]
    [InlineData(SalesApplication, true)]
    [InlineData(SalesInfrastructure, true)]
    public void TheHostCodeMayUseTheEndpointsProjectOfAModuleOnly(string reference, bool reported)
    {
        Assert.Equal(reported, ModuleReferenceRules.HostCodeViolations(Host, [reference, WebBlock, "Acme.Application"], Root).Count == 1);
    }

    // The project file of the host (criterion: the Host references no module project other than Endpoints).
    [Fact]
    public void TheHostProjectFileMayListTheEndpointsProjectOfAModule()
    {
        Assert.Empty(ModuleReferenceRules.HostProjectFileViolations([SalesEndpoints, WebBlock, InfrastructureBlock, "Acme.Application"], Root));
    }

    [Theory]
    [InlineData(SalesContracts)]
    [InlineData(SalesDomain)]
    [InlineData(SalesApplication)]
    [InlineData(SalesInfrastructure)]
    public void TheHostProjectFileMayNotListAnotherProjectOfAModule(string project)
    {
        var violation = Assert.Single(ModuleReferenceRules.HostProjectFileViolations([SalesEndpoints, project], Root));

        Assert.Contains(project, violation, StringComparison.Ordinal);
    }

    // Project names are read from the text of the include path. The separators of a project file are backslashes, which
    // are not path separators on Linux, so these hold on both systems only because the path is split by hand.
    [Theory]
    [InlineData(@"..\Modules\Sales\Acme.Modules.Sales.Endpoints\Acme.Modules.Sales.Endpoints.csproj", "Acme.Modules.Sales.Endpoints")]
    [InlineData("../Modules/Sales/Acme.Modules.Sales.Endpoints/Acme.Modules.Sales.Endpoints.csproj", "Acme.Modules.Sales.Endpoints")]
    [InlineData(@"..\Modules/Sales\Acme.Modules.Sales.Domain/Acme.Modules.Sales.Domain.csproj", "Acme.Modules.Sales.Domain")]
    [InlineData("Acme.Modules.Sales.Domain.csproj", "Acme.Modules.Sales.Domain")]
    [InlineData(@"..\Acme.Application\Acme.Application.CSPROJ", "Acme.Application")]
    public void AProjectNameIsReadFromAnIncludePathWhateverItsSeparators(string include, string name)
    {
        Assert.Equal(name, ModuleReferenceRules.ProjectNameOf(include));
    }

    [Fact]
    public void TheProjectReferencesOfAProjectFileAreReadByName()
    {
        var project = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <!-- A comment between references. -->
                <ProjectReference Include="..\Modules\Sales\Acme.Modules.Sales.Endpoints\Acme.Modules.Sales.Endpoints.csproj" />
                <ProjectReference Include="..\Modules\Sales\Acme.Modules.Sales.Domain\Acme.Modules.Sales.Domain.csproj" />
                <ProjectReference Include="..\BuildingBlocks\Acme.BuildingBlocks.Web\Acme.BuildingBlocks.Web.csproj" />
              </ItemGroup>
            </Project>
            """);

        var names = ModuleReferenceRules.ProjectReferencesOf(project);

        Assert.Equal([SalesEndpoints, SalesDomain, WebBlock], names);
        var violation = Assert.Single(ModuleReferenceRules.HostProjectFileViolations(names, Root));
        Assert.Contains(SalesDomain, violation, StringComparison.Ordinal);
    }

    // Framework rules (criterion: Domain and Contracts use the base class library only, Application no web or data framework).
    [Theory]
    [InlineData(SalesDomain, "System.Runtime", false)]
    [InlineData(SalesDomain, "System.Collections", false)]
    [InlineData(SalesDomain, "netstandard", false)]
    [InlineData(SalesDomain, DomainBlock, false)]
    [InlineData(SalesDomain, "Microsoft.AspNetCore.Http.Abstractions", true)]
    [InlineData(SalesDomain, "Microsoft.Extensions.DependencyInjection.Abstractions", true)]
    [InlineData(SalesDomain, "Microsoft.EntityFrameworkCore", true)]
    [InlineData(SalesDomain, "Npgsql", true)]
    [InlineData(SalesDomain, "System.Data.Common", true)]
    [InlineData(SalesDomain, "System.Net.Http", true)]
    [InlineData(SalesDomain, "Newtonsoft.Json", true)]
    [InlineData(SalesContracts, "System.Runtime", false)]
    [InlineData(SalesContracts, "Microsoft.AspNetCore.Http.Abstractions", true)]
    [InlineData(SalesContracts, "Microsoft.Extensions.Logging.Abstractions", true)]
    [InlineData(SalesContracts, "Dapper", true)]
    [InlineData(SalesContracts, "Newtonsoft.Json", true)]
    [InlineData(SalesApplication, "System.Runtime", false)]
    [InlineData(SalesApplication, "Microsoft.Extensions.DependencyInjection.Abstractions", false)]
    [InlineData(SalesApplication, "Newtonsoft.Json", false)]
    [InlineData(SalesApplication, "Microsoft.AspNetCore.Http.Abstractions", true)]
    [InlineData(SalesApplication, "Microsoft.EntityFrameworkCore", true)]
    [InlineData(SalesApplication, "Npgsql", true)]
    [InlineData(SalesApplication, "Dapper", true)]
    [InlineData(SalesApplication, "System.Data.Common", true)]
    [InlineData(SalesApplication, "System.Net.Http", true)]
    [InlineData(SalesInfrastructure, "Npgsql", false)]
    [InlineData(SalesEndpoints, "Microsoft.AspNetCore.Http.Abstractions", false)]
    public void ModuleProjectsUseOnlyTheFrameworksTheirLayerAllows(string assembly, string reference, bool reported)
    {
        var violations = ModuleReferenceRules.FrameworkViolations(assembly, [reference], Root);

        Assert.Equal(reported, violations.Count > 0);
        Assert.All(violations, m => Assert.Contains(reference, m, StringComparison.Ordinal));
    }
}
