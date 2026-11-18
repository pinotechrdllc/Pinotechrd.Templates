using System.Reflection;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// A view of an assembly that exposes only the types it is given, under the name of that assembly. The module fixtures
/// live in the test assembly (the validation resolver of the test assembly is what the tests look at), but a module's
/// endpoints are found by scanning its assembly, and scanning the test assembly would map every public endpoint of every
/// test as part of the module. The view gives each fixture module its own endpoints and nothing else, and keeps them out
/// of any test that scans the real test assembly.
/// </summary>
internal sealed class TypeSetAssembly : Assembly
{
    private readonly Assembly _backing;
    private readonly Type[] _types;

    public TypeSetAssembly(Assembly backing, params Type[] types)
    {
        _backing = backing;
        _types = types;
    }

    public override string? FullName => _backing.FullName;

    public override IEnumerable<Type> ExportedTypes => GetExportedTypes();

    public override IEnumerable<TypeInfo> DefinedTypes => _types.Select(t => t.GetTypeInfo());

    public override AssemblyName GetName() => _backing.GetName();

    public override Type[] GetTypes() => [.. _types];

    public override Type[] GetExportedTypes() => [.. _types.Where(t => t.IsVisible)];
}
