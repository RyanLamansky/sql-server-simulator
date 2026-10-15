namespace SqlServerSimulator;

/// <summary>
/// A statement <c>sp_prepare</c> or <c>sp_prepexec</c> prepared on a session,
/// which <c>sp_execute</c> runs by handle until <c>sp_unprepare</c> releases
/// it — whether those calls arrive as TDS RPC requests or as <c>EXEC</c>
/// text, since one session's handles are one set on real.
/// </summary>
internal sealed class PreparedStatement(string statement, string declaration)
{
    public readonly string Statement = statement;

    /// <summary>The parameter declarations as written, empty for none.</summary>
    public readonly string Declaration = declaration;

    /// <summary>
    /// The declared parameter names in order, which name the value parameters
    /// a native driver's RPC sends without one.
    /// </summary>
    public readonly List<string> ParameterNames = [.. Simulation.SplitParameterDeclaration(declaration).Select(declared => declared.Name)];
}
