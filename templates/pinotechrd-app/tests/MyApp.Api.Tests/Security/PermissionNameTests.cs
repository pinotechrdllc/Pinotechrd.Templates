using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// The rule for permission names, as the README documents it: {Module}.{Resource}.{Action}, three PascalCase segments of
// ASCII letters and digits. It holds where a name is written (the endpoint declaration, the registration, the catalogue),
// so a bad name stops the application from starting instead of closing an endpoint silently.
public class PermissionNameTests
{
    // Names the rule must accept. Digits are allowed after the first character of a segment (the XML documentation of
    // PermissionName gives Sales.Orders2.Export); a segment may be a single capital letter.
    public static TheoryData<string> Valid =>
    [
        "Access.Users.Read",
        "Sales.Orders.Export",
        "Sales.Orders2.Export",
        "A.B.C",
        "ACCESS.USERS.READ",
        "Access.Users.ReadAll",
        "Module9.Resource9.Action9",
    ];

    // Names the rule must reject, each one a way to break it: case, segment count, empty segments, spaces, characters
    // outside ASCII letters and digits, a digit or a lower-case letter first.
    public static TheoryData<string> Invalid =>
    [
        "access.users.read",
        "Access.users.Read",
        "Access.Users.read",
        "Access.Users",
        "Access",
        "Access.Users.Read.All",
        "Access.Users.Read.",
        ".Access.Users.Read",
        "Access..Read",
        "Access.Users.",
        "..",
        "Access. Users.Read",
        "Access.Users.Read ",
        " Access.Users.Read",
        "Access.Us ers.Read",
        "Access.Users.Read\n",
        "Access.Users.Re_ad",
        "Access.Users.Re-ad",
        "Access.Users.Re:ad",
        "Access:Users:Read",
        "Access/Users/Read",
        "Accéss.Users.Read",
        "Access.Üsers.Read",
        "Access.Users.Reaд",
        "Access.Users.Read٣",
        "Ａccess.Users.Read",
        "1Access.Users.Read",
        "Access.2Users.Read",
        "Access.Users.3Read",
        "Access.Users.*",
        "Access.Users.Read,Write",
        "",
        " ",
    ];

    // Breaks when the shared rule accepts a name it must reject or rejects a valid one.
    [Theory]
    [MemberData(nameof(Valid))]
    public void AValidNameIsAccepted(string name)
    {
        Assert.True(PermissionName.IsValid(name));
        PermissionName.Validate(name);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnInvalidNameIsRejected(string name)
    {
        Assert.False(PermissionName.IsValid(name));
        Assert.Throws<ArgumentException>(() => PermissionName.Validate(name));
    }

    [Fact]
    public void ANullNameIsRejected()
    {
        Assert.False(PermissionName.IsValid(null));
        Assert.ThrowsAny<ArgumentException>(() => PermissionName.Validate(null));
    }

    // Breaks when a name is accepted where an endpoint declares it, which would close the endpoint for good with no hint.
    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnInvalidNameIsRejectedWhenAnEndpointDeclaresIt(string name)
    {
        var endpoint = RouteToDeclareOn();

        var error = Assert.Throws<ArgumentException>(() => endpoint.RequirePermission(name));

        if (name.Trim().Length > 0)
        {
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
        }
    }

    // Breaks when a name is accepted by the registration of a module: the catalogue would hold a name nothing could use.
    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnInvalidNameIsRejectedWhenItIsRegistered(string name)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddPermissions("Access.Users.Read", name));

        // Nothing of a refused call is registered: the valid name before the bad one is not left behind half registered.
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<PermissionCatalogue>());
    }

    // Breaks when the catalogue built by hand takes a name that the registration would refuse.
    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnInvalidNameIsRejectedByTheCatalogue(string name)
    {
        Assert.Throws<ArgumentException>(() => new PermissionCatalogue(["Access.Users.Read", name]));
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void AValidNameIsAcceptedWhereAnEndpointDeclaresItWhereItIsRegisteredAndByTheCatalogue(string name)
    {
        var endpoint = RouteToDeclareOn();
        var services = new ServiceCollection();

        endpoint.RequirePermission(name);
        services.AddPermissions(name);
        var catalogue = new PermissionCatalogue([name]);

        Assert.True(catalogue.Contains(name));
        Assert.Equal([name], catalogue.Permissions);
    }

    // Breaks when the catalogue matches ignoring case: "access.users.read" would be taken for a registered permission.
    [Fact]
    public void TheCatalogueComparesNamesExactly()
    {
        var catalogue = new PermissionCatalogue(["Access.Users.Read"]);

        Assert.True(catalogue.Contains("Access.Users.Read"));
        Assert.False(catalogue.Contains("access.users.read"));
        Assert.False(catalogue.Contains("Access.USERS.Read"));
        Assert.False(catalogue.Contains("Access.Users.Read "));
        Assert.False(catalogue.Contains(string.Empty));
    }

    // Breaks when the catalogue keeps a name twice, loses the ordinal order the seeding reads, or drops the permissions of
    // one of the calls (each module registers its own).
    [Fact]
    public void TheCatalogueHoldsEveryRegisteredPermissionOnceInOrdinalOrder()
    {
        var services = new ServiceCollection();
        services.AddPermissions("Sales.Orders.Read", "Access.Users.Read");
        services.AddPermissions("Sales.Orders.Read", "Access.Users.Write");
        services.AddPermissions();
        using var provider = services.BuildServiceProvider();

        var catalogue = provider.GetRequiredService<PermissionCatalogue>();

        Assert.Equal(["Access.Users.Read", "Access.Users.Write", "Sales.Orders.Read"], catalogue.Permissions);
    }

    // The end to end of a valid name: declared, registered, and the exact string reaches the checker.
    [Fact]
    public async Task AValidNameAnEndpointDeclaresIsWhatTheCheckerReceives()
    {
        var checker = new TestPermissionChecker("Reports.Users.Read");
        using var factory = new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddPermissions("Reports.Users.Read"))
            .WithEndpoints(routes => routes.MapGet("/access-fixtures/read", () => Results.Ok()).RequirePermission("Reports.Users.Read"))
            .WithPermissionChecker(checker, "Reports");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync("/api/v1/access-fixtures/read", user: "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Reports.Users.Read", Assert.Single(checker.Calls).Permission);
    }

    // A route builder to declare on, without starting a host: the declaration throws where it is written.
    private static RouteHandlerBuilder RouteToDeclareOn() =>
        WebApplication.CreateBuilder().Build().MapGet("/names", () => Results.Ok());
}
