namespace MyApp.BuildingBlocks.Tests.Application;

// Fixture types scanned by the handler registration tests. Only these types may end in "Handler" in this assembly.
#pragma warning disable CA1812 // Instantiated through dependency injection or never instantiated on purpose.
#pragma warning disable CA1822 // Fixture methods do not need instance state.

public interface IFixtureHandler;

public sealed class Dependency;

public sealed class PublicFixtureHandler
{
    public Task HandleAsync() => Task.CompletedTask;
}

internal sealed class InternalFixtureHandler
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class DependentHandler(Dependency dependency)
{
    public Dependency Dependency { get; } = dependency;

    public Task HandleAsync() => Task.CompletedTask;
}

public abstract class AbstractFixtureHandler
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class GenericFixtureHandler<T>
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class HandlerWithAnotherSuffix
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class FixtureHandlerBase
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class FixtureService
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class ImplementedFixtureHandler : IFixtureHandler
{
    public Task HandleAsync() => Task.CompletedTask;
}

// A handler-named class without HandleAsync, such as an authorization or delegating handler.
public sealed class AuthorizationFixtureHandler;

public sealed class InternalHandleAsyncFixtureHandler
{
    internal Task HandleAsync() => Task.CompletedTask;
}

public sealed class PrivateHandleAsyncFixtureHandler
{
    private Task HandleAsync() => Task.CompletedTask;

    public bool Touch() => HandleAsync().IsCompleted;
}

public sealed class StaticHandleAsyncFixtureHandler
{
    public static Task HandleAsync() => Task.CompletedTask;
}

public abstract class HandleAsyncProvider
{
    public Task HandleAsync() => Task.CompletedTask;
}

public sealed class InheritingFixtureHandler : HandleAsyncProvider;

public static class NestedFixtures
{
    public sealed class PublicNestedHandler
    {
        public Task HandleAsync() => Task.CompletedTask;
    }

    internal sealed class InternalNestedHandler
    {
        public Task HandleAsync() => Task.CompletedTask;
    }

    public sealed class NestedWithoutHandleAsyncHandler;
}
