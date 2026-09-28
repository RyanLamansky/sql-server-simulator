using System.Reflection;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// Where a CLR module's <c>EXTERNAL NAME</c> clause points: the registered
/// assembly, the class and method as written, and what they bound to at
/// CREATE. An aggregate names a class alone, so its method is
/// <see langword="null"/>.
/// </summary>
internal sealed class ClrEntryPoint(SqlAssembly assembly, string className, string? methodName, Type type, MethodInfo? method)
{
    public readonly SqlAssembly Assembly = assembly;

    /// <summary>The class segment, as written — <c>sys.assembly_modules.assembly_class</c>.</summary>
    public readonly string ClassName = className;

    /// <summary>The method segment, as written — <c>sys.assembly_modules.assembly_method</c>.</summary>
    public readonly string? MethodName = methodName;

    public readonly Type Type = type;

    public readonly MethodInfo? Method = method;
}
