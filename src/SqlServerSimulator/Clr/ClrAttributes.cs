using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SqlServerSimulator.Clr;

/// <summary>
/// Reads the <c>Microsoft.SqlServer.Server</c> attributes a SQLCLR assembly
/// marks its routines with, as <see cref="CustomAttributeData"/> — matched by
/// full name and never instantiated, so the attribute's own code never runs.
/// </summary>
internal static class ClrAttributes
{
    public const string SqlFunction = "Microsoft.SqlServer.Server.SqlFunctionAttribute";
    public const string SqlUserDefinedAggregate = "Microsoft.SqlServer.Server.SqlUserDefinedAggregateAttribute";
    public const string SqlUserDefinedType = "Microsoft.SqlServer.Server.SqlUserDefinedTypeAttribute";
    public const string SqlMethod = "Microsoft.SqlServer.Server.SqlMethodAttribute";

    /// <summary>The attribute named <paramref name="attributeName"/> on <paramref name="member"/>, or <see langword="null"/>.</summary>
    public static CustomAttributeData? Find(MemberInfo member, string attributeName)
    {
        foreach (var attribute in member.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName == attributeName)
                return attribute;
        }

        return null;
    }

    /// <summary>
    /// The string an attribute's named argument sets, or
    /// <see langword="null"/> when the attribute is absent or leaves it unset.
    /// </summary>
    public static string? NamedString(MemberInfo member, string attributeName, string argumentName)
    {
        if (Find(member, attributeName) is not { } attribute)
            return null;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.MemberName == argumentName && argument.TypedValue.Value is string value)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="method"/>'s <c>SqlFunction</c> attribute sets
    /// <c>DataAccess</c> and <c>SystemDataAccess</c> to <c>Read</c>.
    /// </summary>
    public static (bool User, bool System) DataAccess(MemberInfo method)
    {
        var access = (User: false, System: false);
        if (Find(method, SqlFunction) is not { } attribute)
            return access;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.TypedValue.Value is not int kind || kind == 0)
                continue;
            if (argument.MemberName == "DataAccess")
                access.User = true;
            else if (argument.MemberName == "SystemDataAccess")
                access.System = true;
        }

        return access;
    }

    /// <summary>The static method <paramref name="name"/> declares on <paramref name="type"/>, public or not.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:DynamicallyAccessedMembers",
        Justification = "The type comes from an assembly registered from bytes at run time, outside the application's static closure, so trimming cannot affect its members.")]
    public static MethodInfo? FindStaticMethod(Type type, string name)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name == name)
                return method;
        }

        return null;
    }
}
