using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using Probe.Empty;
using Probe.WithEndpoint;

// Prints, for each library, what is observable from outside after calling its registration:
// the services added, the resolvers in ValidationOptions, and the generated types it carries.
Report("none", _ => { });
Report("empty", s => s.AddEmptyModule());
Report("endpoint", s => s.AddOrdersModule());
Report("both", s => s.AddEmptyModule().AddOrdersModule());

foreach (var assembly in new[] { typeof(EmptyModule).Assembly, typeof(OrdersModule).Assembly })
{
    var generated = assembly.GetTypes()
        .Where(t => typeof(IValidatableInfoResolver).IsAssignableFrom(t))
        .Select(t => $"{t.Namespace}.{t.Name.Split("__").Last()}")
        .ToList();
    Console.WriteLine($"resolver types in {assembly.GetName().Name}: {(generated.Count == 0 ? "(none)" : string.Join("; ", generated))}");
}

static void Report(string name, Action<IServiceCollection> register)
{
    var services = new ServiceCollection();
    register(services);
    var added = services.Select(d => d.ServiceType.Name + (d.ServiceType.IsGenericType ? "<" + string.Join(",", d.ServiceType.GetGenericArguments().Select(a => a.Name)) + ">" : "")).ToList();
    using var provider = services.BuildServiceProvider();
    var options = provider.GetService<IOptions<ValidationOptions>>()?.Value;
    // Generated resolvers are file-local types with mangled names; the stable fact is their assembly.
    var resolvers = options?.Resolvers.Select(r => $"{r.GetType().Name.Split("__").Last()} from {r.GetType().Assembly.GetName().Name}").ToList() ?? [];
    var knowsOrder = options is not null && options.TryGetValidatableTypeInfo(typeof(CreateOrder), out _);
    Console.WriteLine($"{name}: services={added.Count} [{string.Join(", ", added.Distinct())}]");
    Console.WriteLine($"{name}: resolvers={resolvers.Count} [{string.Join("; ", resolvers)}] knowsCreateOrder={knowsOrder}");
}
