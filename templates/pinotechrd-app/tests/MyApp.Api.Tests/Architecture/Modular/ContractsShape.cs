using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// The shape of a module's Contracts assembly: what other modules may call or receive, so it holds interfaces, records
/// and enums and nothing else. The one exemption is the assembly marker type, found by its documented name
/// <c>{Module}ContractsAssembly</c> and required to be a static class; a type the compiler generates for a member is not
/// a type of the contract.
/// </summary>
internal static class ContractsShape
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>The name of the marker type of the Contracts assembly of <paramref name="module"/>.</summary>
    public static string MarkerNameOf(string module) => module + "ContractsAssembly";

    /// <summary>One message per type of the assembly that is not an interface, a record or an enum, and per problem with the marker.</summary>
    public static IReadOnlyList<string> Violations(string module, string assembly, IEnumerable<Type> types)
    {
        var messages = new List<string>();
        var markerName = MarkerNameOf(module);
        var contractTypes = types.Where(t => !IsCompilerGenerated(t)).ToList();

        var markers = contractTypes.Where(t => t.Name == markerName).ToList();
        if (markers.Count != 1)
        {
            messages.Add($"Assembly '{assembly}' must hold exactly one marker type named '{markerName}', it holds {markers.Count}.");
        }

        foreach (var type in contractTypes)
        {
            if (type.Name == markerName && markers.Count == 1)
            {
                if (!IsStaticClass(type))
                {
                    messages.Add($"The marker type '{type.FullName}' must be a static class.");
                }

                continue;
            }

            if (!(type.IsInterface || type.IsEnum || IsRecord(type)))
            {
                messages.Add($"Assembly '{assembly}' holds '{type.FullName}', which is {Describe(type)}: a contracts assembly holds interfaces, records and enums only.");
            }
        }

        return messages;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || (type.DeclaringType is { } outer && IsCompilerGenerated(outer));

    private static bool IsStaticClass(Type type) => type is { IsClass: true, IsAbstract: true, IsSealed: true };

    // The compiler gives a record class a hidden clone method and a record struct the PrintMembers method of its ToString.
    private static bool IsRecord(Type type) =>
        type.IsClass
            ? type.GetMethod("<Clone>$", Instance) is not null
            : type.IsValueType && type.GetMethod("PrintMembers", Instance, [typeof(StringBuilder)]) is not null;

    private static string Describe(Type type) =>
        type switch
        {
            { IsClass: true } when typeof(Delegate).IsAssignableFrom(type) => "a delegate",
            { IsClass: true } when IsStaticClass(type) => "a static class",
            { IsClass: true, IsAbstract: true } => "an abstract class",
            { IsClass: true } => "a class",
            { IsValueType: true } => "a struct that is not a record",
            _ => "not an interface, record or enum",
        };
}
