using SqlServerSimulator.Storage;
using System.Data.Common;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-batch runtime state. One <see cref="BatchContext"/> is constructed
/// per command execution by <see cref="Simulation.CreateResultSetsForCommand"/>;
/// it owns the <see cref="ParserContext"/> that walks the command's tokens
/// and the runtime state both parsing and execution mutate (variable slots,
/// undo log). Parsers see the parser context and reach runtime state via
/// <see cref="ParserContext.Batch"/>; the dispatch loop and writeback
/// helpers operate on the batch context directly.
/// </summary>
internal sealed class BatchContext
{
    /// <summary>The parser-side cursor / scratch state for this batch.</summary>
    public readonly ParserContext Parser;

    /// <summary>
    /// Heap-mutation undo log scoped to the current top-level statement. Set
    /// by <see cref="Simulation.CreateResultSetsForCommand"/>'s mutation
    /// dispatch around each INSERT / UPDATE / DELETE / MERGE; the
    /// <see cref="Heap.Insert"/> / <see cref="Heap.DeleteAt"/>
    /// call sites read it from here and append entries on success. A
    /// statement that throws mid-execution (e.g. a multi-row INSERT whose
    /// fourth row violates a constraint) walks the log backwards before the
    /// exception propagates, restoring the heap to its pre-statement state.
    /// Explicit transactions reuse the same log shape, lifetime extended
    /// across statements until COMMIT / ROLLBACK.
    /// </summary>
    public UndoLog? CurrentUndoLog;

    /// <summary>
    /// Per-statement scratch frame, allocated once per batch and overwritten
    /// in place by the dispatch loop at the top of each statement iteration.
    /// See <see cref="StatementContext"/> for the fields it carries.
    /// </summary>
    public readonly StatementContext CurrentStatement = new();

    /// <summary>
    /// True while the dispatch loop is walking through an un-taken IF branch.
    /// Each statement parser still runs its full parse (advances the cursor,
    /// resolves names, evaluates constant subexpressions) but gates the
    /// actual state mutation — heap inserts/updates/deletes, dict adds for
    /// CREATE TABLE / DECLARE, variable slot writes for SET, transaction
    /// state changes for BEGIN TRAN / COMMIT / ROLLBACK / SAVE, the existence
    /// check + drop for DROP TABLE, the create + bulk insert for SELECT INTO,
    /// the OBJECT_ID lookup for SET IDENTITY_INSERT, and so on. SELECT
    /// statements in a skipped branch don't yield result sets and don't
    /// update <see cref="SimulatedDbConnection.LastStatementRowCount"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fidelity gap vs SQL Server: real SQL Server defers name resolution
    /// for un-taken IF branches (an un-taken <c>SELECT bad_col FROM bad_table</c>
    /// runs silently). The simulator parses both branches the same way, so
    /// invalid table/column references in un-taken branches still raise
    /// <c>Msg 208</c> / <c>Msg 207</c> here. Common patterns
    /// (<c>IF NOT EXISTS (…) CREATE TABLE foo (…)</c>,
    /// <c>IF OBJECT_ID('foo','U') IS NOT NULL DROP TABLE foo</c>) reference
    /// names that exist at parse time when the branch is skipped, so they
    /// work end-to-end; only synthetic patterns that name nothing-tables hit
    /// the gap.
    /// </para>
    /// </remarks>
    public bool IsSkipping;

    /// <summary>The connection executing this batch.</summary>
    public SimulatedDbConnection Connection => this.Parser.Connection;

    /// <summary>The database this batch is executing against.</summary>
    public Database CurrentDatabase => this.Parser.CurrentDatabase;

    /// <summary>
    /// Per-batch variable store. Seeded with SqlClient parameters at
    /// construction; <c>DECLARE</c> adds entries; <c>SET</c> /
    /// <c>SELECT @v = expr</c> mutate them. Parameters and declared variables
    /// share a namespace — a <c>DECLARE</c> whose name collides with a
    /// parameter raises Msg 134 (probe-confirmed: real SQL Server treats
    /// SqlClient parameters as if they were already declared). End-of-batch
    /// write-back to <c>InputOutput</c> / <c>Output</c> direction parameters
    /// reads from this store.
    /// </summary>
    public readonly Dictionary<string, VariableSlot> Variables;

    public BatchContext(SimulatedDbCommand command)
    {
        this.Variables = SeedVariables(command);
        this.Parser = new ParserContext(command, this);
    }

    private static Dictionary<string, VariableSlot> SeedVariables(SimulatedDbCommand command)
    {
        var dict = new Dictionary<string, VariableSlot>(StringComparer.InvariantCultureIgnoreCase);
        foreach (DbParameter parameter in command.Parameters)
        {
            var name = parameter.ParameterName;
            if (name.StartsWith('@'))
                name = name[1..];
            var dbType = SqlType.GetByDbType(parameter.DbType);
            var seed = parameter.Value is null or DBNull
                ? SqlValue.Null(dbType)
                : dbType.ConvertParameter(parameter.Value);
            // For decimal / numeric parameters, ConvertParameter widens the
            // declared type to fit the value's natural scale (e.g. caller sends
            // 123.45m without an explicit scale → widens to decimal(28, 2)).
            // Track the post-widen type so VariableReference.GetSqlType returns
            // the right schema and downstream readers don't truncate.
            var declaredType = seed.IsNull ? dbType : seed.Type;
            dict[name] = new VariableSlot(declaredType, declaredMaxLength: null, seed, parameter);
        }
        return dict;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a live <see cref="VariableSlot"/>
    /// reference. Captured at parse time by <see cref="Expressions.VariableReference"/>
    /// so subsequent <c>SET</c> / <c>SELECT @v = expr</c> mutations are
    /// observable when the expression evaluates at runtime — the dictionary
    /// is append-only within a batch (re-DECLARE raises Msg 134), so a slot
    /// reference captured during parse stays valid.
    /// </summary>
    /// <exception cref="SimulatedSqlException">Must declare the scalar variable \"@{value of <paramref name="name"/>}\".</exception>
    public VariableSlot GetVariableSlot(string name) =>
        Variables.TryGetValue(name, out var slot)
        ? slot
        : throw SimulatedSqlException.MustDeclareScalarVariable(name);

    /// <summary>
    /// Recognizes a local temp-table name (<c>#foo</c>, including bare
    /// <c>#</c>). Global temps (<c>##foo</c>) aren't modeled and return
    /// false. The rule: leading <c>#</c>, second char is not <c>#</c> (so
    /// <c>##</c>-prefixed names fall out as not-local).
    /// </summary>
    public static bool IsLocalTempName(string name) =>
        name.Length >= 1 && name[0] == '#' && (name.Length == 1 || name[1] != '#');

    /// <summary>
    /// Resolves <paramref name="name"/> against the right table dictionary —
    /// the connection's <see cref="SimulatedDbConnection.TempTables"/> for
    /// <c>#foo</c> names, otherwise the current database's user tables and
    /// the simulation's system tables. Centralizes the routing rule so call
    /// sites (SELECT/INSERT/UPDATE/DELETE/MERGE name lookups,
    /// <c>IDENT_CURRENT</c>, <c>SET IDENTITY_INSERT</c>) stay uniform.
    /// </summary>
    public bool TryResolveTable(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapTable? table) =>
        IsLocalTempName(name)
            ? this.Connection.TempTables.TryGetValue(name, out table)
            : this.CurrentDatabase.HeapTables.TryGetValue(name, out table)
                || Simulation.SystemHeapTables.TryGetValue(name, out table);
}
