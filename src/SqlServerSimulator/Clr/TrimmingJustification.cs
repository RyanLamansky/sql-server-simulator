namespace SqlServerSimulator.Clr;

/// <summary>The shared reason reflection over a registered assembly is trim-safe.</summary>
internal static class TrimmingJustification
{
    public const string RegisteredAssembly = "The type comes from an assembly registered from bytes at run time, outside the application's static closure, so trimming cannot affect its members.";
}
