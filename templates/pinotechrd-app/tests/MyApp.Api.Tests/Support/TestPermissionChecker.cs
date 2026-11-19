using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// A permission checker for the tests: it grants exactly the permissions it was given and records every question it was
/// asked (the user and the permission string it received), so a test can tell what the pipeline asked and when.
/// </summary>
internal sealed class TestPermissionChecker(params string[] granted) : IPermissionChecker
{
    private readonly HashSet<string> _granted = [.. granted];

    /// <summary>One question: the user id and the permission, exactly as received.</summary>
    internal sealed record Call(bool IsAuthenticated, Guid? UserId, string Permission);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken)
    {
        Calls.Enqueue(new Call(user.IsAuthenticated, user.IsAuthenticated ? user.UserId : null, permission));
        return ValueTask.FromResult(_granted.Contains(permission));
    }
}

internal static class TestPermissionCheckerExtensions
{
    /// <summary>
    /// Registers <paramref name="checker"/> as the permission checker of <paramref name="module"/> (the first segment of the
    /// permissions it owns), the way a module does with <c>AddPermissionChecker</c>: a keyed service.
    /// </summary>
    public static ApiHostFactory WithPermissionChecker(this ApiHostFactory factory, IPermissionChecker checker, string module = "Fixture") =>
        factory.WithServices(services => services.AddKeyedSingleton(module, checker));
}
