using Microsoft.EntityFrameworkCore;

namespace MyApp.Migrator.Tests.Support;

/// <summary>
/// Builds a context the way <c>dotnet ef</c> does: through the context's own design-time factory, found in the context's
/// assembly by the interface name (this project does not reference the design package).
/// </summary>
internal static class DesignContexts
{
    private const string FactoryInterface = "IDesignTimeDbContextFactory";

    public static TContext Create<TContext>()
        where TContext : DbContext
    {
        var factory = typeof(TContext).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true })
            .Single(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.Name.StartsWith(FactoryInterface, StringComparison.Ordinal)
                && i.GetGenericArguments()[0] == typeof(TContext)));
        var instance = Activator.CreateInstance(factory, nonPublic: true)!;
        var create = factory.GetInterfaces()
            .Single(i => i.Name.StartsWith(FactoryInterface, StringComparison.Ordinal))
            .GetMethod("CreateDbContext")!;
        return (TContext)create.Invoke(instance, [Array.Empty<string>()])!;
    }
}
