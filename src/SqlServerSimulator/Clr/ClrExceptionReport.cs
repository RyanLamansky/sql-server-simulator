using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace SqlServerSimulator.Clr;

/// <summary>
/// Renders an exception a CLR routine threw the way the server's host writes
/// it into Msg 6522 / 6260 (probed 2026-09-28 against SQL Server 2025):
/// <c>type: message</c>, then the type alone, then one <c>   at …</c> line per
/// stack frame, every line CRLF-ended. Only the frames a routine author can
/// see are kept — the routine's own assembly and the public members of the
/// <c>Microsoft.SqlServer.Server</c> surface — so the reflection plumbing that
/// invoked the routine never shows, as the server's hosting frames never do.
/// </summary>
/// <remarks>
/// The optimizing JIT turns a routine's last call into a tail call, which
/// takes the routine's own frame off the stack — so a routine ending in
/// <c>SqlContext.Pipe.Send(…)</c> that throws would report the pipe's frame
/// alone, where the server always shows the routine's method last. The report
/// restores that last frame from the method the simulator invoked; a frame a
/// tail call took from between the two can't be recovered.
/// </remarks>
internal static class ClrExceptionReport
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The frames kept belong to assemblies registered from bytes at run time or to the embedded Microsoft.SqlServer.Server build, neither in the application's static closure; a frame whose method metadata is gone is simply skipped.")]
    public static string Describe(Exception exception, MethodBase? invoked)
    {
        var type = exception.GetType().FullName;
        var kept = new List<MethodBase>();
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            if (frame.GetMethod() is { } method && IsAuthorVisible(method))
                kept.Add(method);
        }

        if (invoked is not null && (kept.Count == 0 || kept[^1] != invoked))
            kept.Add(invoked);
        var frames = new StringBuilder();
        foreach (var method in kept)
            _ = frames.Append("   at ").Append(Render(method)).Append("\r\n");
        return Describe(type!, MessageOf(exception), frames.ToString());
    }

    /// <summary>
    /// The report for an exception the server's own marshalling raises, which
    /// has no managed stack of the routine's to read.
    /// </summary>
    public static string Describe(string typeName, string message, string frames) =>
        $"{typeName}: {message}\r\n{typeName}: \r\n{frames}";

    /// <summary>
    /// The report for the truncation the server's marshalling raises when an
    /// <c>nvarchar</c> return value or output parameter is longer than its
    /// declared T-SQL type, sized in bytes.
    /// </summary>
    public static string Truncation(int characters, int limitCharacters) => Describe(
        "System.Data.SqlServer.TruncationException",
        $"Trying to convert return value or output parameter of size {characters * 2} bytes to a T-SQL type with a smaller size limit of {limitCharacters * 2} bytes.",
        "   at System.Data.SqlServer.Internal.CXVariantBase.StringToWSTR(String pstrValue, Int64 cbMaxLength, Int32 iOffset, EPadding ePad)\r\n");

    /// <summary>
    /// The message as .NET Framework words it: an argument exception names its
    /// parameter on a line of its own rather than in .NET's parenthesized
    /// suffix.
    /// </summary>
    private static string MessageOf(Exception exception)
    {
        if (exception is not ArgumentException { ParamName: { Length: > 0 } parameter } argument)
            return exception.Message;
        var suffix = $" (Parameter '{parameter}')";
        var head = argument.Message.EndsWith(suffix, StringComparison.Ordinal)
            ? argument.Message[..^suffix.Length]
            : argument.Message;
        return $"{head}\r\nParameter name: {parameter}";
    }

    private static bool IsAuthorVisible(MethodBase method)
    {
        var assembly = method.Module.Assembly;
        return AssemblyLoadContext.GetLoadContext(assembly) is SqlAssemblyLoadContext
            || (ClrHost.IsServerAssembly(assembly) && method.IsPublic && method.DeclaringType is { IsPublic: true });
    }

    private static string Render(MethodBase method)
    {
        var text = new StringBuilder();
        if (method.DeclaringType is { } declaring)
            _ = text.Append(declaring.FullName?.Replace('+', '.')).Append('.');
        _ = text.Append(method.Name).Append('(');
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
                _ = text.Append(", ");
            _ = text.Append(parameters[i].ParameterType.Name).Append(' ').Append(parameters[i].Name);
        }

        return text.Append(')').ToString();
    }
}
