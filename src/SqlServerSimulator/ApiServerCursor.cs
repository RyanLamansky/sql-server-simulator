using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

/// <summary>
/// One open API server cursor — <c>sp_cursoropen</c>, <c>sp_cursorexecute</c>
/// or <c>sp_cursorprepexec</c>, as a TDS RPC request or as <c>EXEC</c> text,
/// since a session's cursor handles are one set on real — plus the rows of its
/// last fetch buffer. It wraps an engine <see cref="SqlServerSimulator.Cursor"/>
/// registered under an opaque name in <see cref="SimulatedDbConnection.Cursors"/>,
/// so the positioned-DML <c>WHERE CURRENT OF</c> machinery resolves it.
/// </summary>
internal sealed class ApiServerCursor(string internalName, Cursor cursor, Func<SimulatedDbCommand, BatchContext> fetchBatch)
{
    /// <summary>The handle the client names the cursor by: its engine cursor's.</summary>
    public int Handle => this.Cursor.Handle;

    /// <summary>The opaque name the engine cursor is registered under.</summary>
    public readonly string InternalName = internalName;

    public readonly Cursor Cursor = cursor;

    /// <summary>
    /// Builds the batch a fetch reads through, over the command given it,
    /// carrying the parameter values bound at open: a KEYSET or DYNAMIC cursor
    /// re-runs its SELECT on every fetch, so the same bindings ride each one
    /// (real freezes a cursor's parameter values at open).
    /// </summary>
    public readonly Func<SimulatedDbCommand, BatchContext> FetchBatch = fetchBatch;

    /// <summary>
    /// The per-source addresses of the rows the most recent fetch delivered,
    /// in fetch order (one slot per FROM source, so a join cursor's positioned
    /// edit reaches every participating row), which a positioned
    /// <c>sp_cursor</c> operation indexes 1-based by its row number.
    /// </summary>
    public readonly List<(int Page, int Slot)?[]> Buffer = [];

    /// <summary>The name <c>sp_cursoroption</c>'s CURSOR_NAME gave the cursor, which T-SQL then reaches it by.</summary>
    public string? Alias;
}

/// <summary>
/// A cursor statement <c>sp_cursorprepare</c> or <c>sp_cursorprepexec</c>
/// prepared on a session, which <c>sp_cursorexecute</c> opens by handle until
/// <c>sp_cursorunprepare</c> (or <c>sp_unprepare</c>) releases it.
/// </summary>
internal sealed class PreparedApiCursor(string statement, string declaration)
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
