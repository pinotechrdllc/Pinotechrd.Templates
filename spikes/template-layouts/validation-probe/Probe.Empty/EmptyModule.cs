using Microsoft.Extensions.DependencyInjection;

namespace Probe.Empty;

// Calls AddValidation but declares no validatable type and maps no endpoint.
public static class EmptyModule
{
    public static IServiceCollection AddEmptyModule(this IServiceCollection services) => services.AddValidation();
}
