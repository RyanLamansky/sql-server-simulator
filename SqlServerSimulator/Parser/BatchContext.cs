using SqlServerSimulator.Parser.Tokens;
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
    /// Raw IF-skip flag: true while the dispatch loop is walking through an
    /// un-taken IF branch. The <see cref="IsSkipping"/> property OR's this
    /// with <see cref="LoopControl"/>-driven skipping (BREAK / CONTINUE in
    /// flight) so the statement-level gates can read one combined predicate
    /// regardless of why execution is short-circuited.
    /// </summary>
    public bool SkipModeFlag;

    /// <summary>
    /// In-flight loop-flow signal. <see cref="LoopControl.Break"/> /
    /// <see cref="LoopControl.Continue"/> set by their dispatch sites;
    /// <see cref="LoopControl.None"/> the default. Only the
    /// immediately-enclosing WHILE consumes the value — IF / BEGIN…END /
    /// nested blocks pass it through unchanged (subsequent statements in
    /// their scope skip naturally via <see cref="IsSkipping"/>). The
    /// BREAK / CONTINUE parsers don't throw — flag-based control flow
    /// composes cleanly with iterator-based dispatch in a way exception-
    /// signaled control flow doesn't.
    /// </summary>
    public LoopControl LoopControl;

    /// <summary>
    /// Number of WHILE loops currently mid-iteration in this batch.
    /// Incremented unconditionally by WHILE on entry (even when the WHILE
    /// itself is in skip mode), decremented on exit. BREAK / CONTINUE check
    /// this at parse time: when zero, raise Msg 135 / 136 (matches real SQL
    /// Server's compile-time loop-scope check — fires even from un-taken IF
    /// branches, distinct from the Q15 deferred-name-resolution gap).
    /// </summary>
    public int LoopDepth;

    /// <summary>
    /// Total WHILE iterations executed in this batch. Counted across all
    /// loops; the cap is global per batch. Real SQL Server has no such cap
    /// (timeouts handle runaway loops in production); the simulator caps
    /// at <see cref="LoopIterationLimit"/> so a buggy test doesn't hang CI.
    /// </summary>
    public long LoopIterations;

    /// <summary>Per-batch ceiling on total WHILE iterations.</summary>
    public const long LoopIterationLimit = 100_000;

    /// <summary>
    /// Active error context inside a <c>CATCH</c> block — set when the
    /// associated <c>TRY</c> body's dispatch caught a
    /// <see cref="SimulatedSqlException"/>, cleared when the enclosing
    /// <c>BEGIN CATCH ... END CATCH</c> exits. Drives
    /// <c>ERROR_NUMBER</c> / <c>ERROR_MESSAGE</c> / <c>ERROR_SEVERITY</c> /
    /// <c>ERROR_STATE</c> / <c>ERROR_LINE</c> / <c>ERROR_PROCEDURE</c>
    /// (which return NULL when this is null) and the no-arg
    /// <c>THROW;</c> re-raise. Nested <c>TRY/CATCH</c> saves+restores this
    /// around the inner CATCH so the outer CATCH (if reached via re-throw)
    /// sees the re-thrown error.
    /// </summary>
    public CaughtError? InFlightError;

    /// <summary>
    /// Set true when a <c>SimulatedSqlException</c> is caught at a
    /// <c>TRY/CATCH</c> boundary; <see cref="IsSkipping"/> OR's it in so the
    /// rest of the TRY body skip-dispatches until <c>END TRY</c>. Cleared
    /// when the matching CATCH begins running so its statements aren't
    /// themselves skipped.
    /// </summary>
    public bool ErrorSignaled;

    /// <summary>
    /// Number of <c>TRY</c> bodies currently being dispatched on the stack.
    /// Incremented at <c>BEGIN TRY</c>, decremented at <c>END TRY</c> — does
    /// <em>not</em> increment when the matching CATCH body runs (CATCH isn't
    /// inside its own TRY). The dispatch wrapper catches
    /// <see cref="SimulatedSqlException"/> only when this is positive;
    /// otherwise errors propagate out of the batch as before.
    /// </summary>
    public int TryFrameDepth;

    /// <summary>
    /// Number of <c>CATCH</c> bodies currently being dispatched on the stack.
    /// Incremented when a CATCH body starts running (i.e. the matching TRY
    /// caught an error), decremented when it ends. Gates <c>THROW;</c> (the
    /// no-arg re-raise — Msg 10704 when zero) and the in-CATCH detection for
    /// <c>ERROR_*()</c> functions.
    /// </summary>
    public int CatchDepth;

    /// <summary>
    /// True after a <c>RETURN</c> statement has fired in this batch. Drives
    /// early-exit propagation: the dispatch loop (and every enclosing
    /// construct — WHILE, BEGIN…END block) checks this and stops as soon as
    /// the current statement's dispatch completes. <see cref="IsSkipping"/>
    /// also OR's this in so any statements still parsed after RETURN in the
    /// same scope no-op via the skip-mode gates.
    /// </summary>
    /// <remarks>
    /// RETURN propagates through WHILE (unlike BREAK / CONTINUE, which the
    /// innermost WHILE catches). Batch-level only for now; once stored
    /// procedures and functions land, the proc-call boundary will consume
    /// the signal (and the value-form <c>RETURN N</c> will start being legal
    /// inside those scopes, ungating the Msg 178 check).
    /// </remarks>
    public bool ReturnSignaled;

    /// <summary>
    /// True while the dispatch loop should treat each statement parser as
    /// "parse only" — advance the cursor and resolve names but skip the
    /// actual state mutation (heap inserts/updates/deletes, dict adds for
    /// CREATE TABLE / DECLARE, variable slot writes for SET, transaction
    /// state changes for BEGIN TRAN / COMMIT / ROLLBACK / SAVE, the existence
    /// check + drop for DROP TABLE, the create + bulk insert for SELECT INTO,
    /// the OBJECT_ID lookup for SET IDENTITY_INSERT, and so on). SELECT
    /// statements with this flag set don't yield result sets and don't
    /// update <see cref="SimulatedDbConnection.LastStatementRowCount"/>.
    /// Combines the raw IF skip flag with the in-flight loop-flow signal so
    /// statements after a BREAK / CONTINUE in the same block also skip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fidelity gap vs SQL Server (un-taken IF): real SQL Server defers name
    /// resolution for un-taken IF branches (an un-taken
    /// <c>SELECT bad_col FROM bad_table</c> runs silently). The simulator
    /// parses both branches the same way, so invalid table/column references
    /// in un-taken branches still raise <c>Msg 208</c> / <c>Msg 207</c> here.
    /// Common patterns (<c>IF NOT EXISTS (…) CREATE TABLE foo (…)</c>,
    /// <c>IF OBJECT_ID('foo','U') IS NOT NULL DROP TABLE foo</c>) reference
    /// names that exist at parse time when the branch is skipped, so they
    /// work end-to-end; only synthetic patterns that name nothing-tables hit
    /// the gap. BREAK / CONTINUE scope checks (Msg 135 / 136) explicitly
    /// don't defer — they fire even in skip mode, matching real SQL Server's
    /// compile-time check on those statements.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Non-null when this batch is executing a scalar UDF body. Holds the
    /// declared return type (used to coerce <c>RETURN &lt;expr&gt;</c> values
    /// at the body's RETURN statement) and the return-value slot the call
    /// site reads after dispatch completes. The presence of this frame is
    /// also the "value-form RETURN is legal" gate — outside a UDF body,
    /// <c>RETURN &lt;expr&gt;</c> raises Msg 178 at parse time.
    /// </summary>
    public UdfFrame? UdfFrame;

    public bool IsSkipping =>
        this.SkipModeFlag
        || this.LoopControl != LoopControl.None
        || this.ReturnSignaled
        || this.ErrorSignaled;

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

    /// <summary>
    /// Constructs a batch for scalar-UDF body re-dispatch. The
    /// <paramref name="udfBodyCommand"/> wraps the UDF's stored body source
    /// (its <c>CommandText</c>) and is constructed with the outer call site's
    /// <see cref="SimulatedDbConnection"/>, so the child batch sees the same
    /// connection / database / transaction state as the caller. Variables are
    /// pre-seeded with the function's argument values; the
    /// <paramref name="udfFrame"/> gates value-form <c>RETURN</c> inside the
    /// body and lands the return value for the caller to read.
    /// </summary>
    public BatchContext(SimulatedDbCommand udfBodyCommand, Dictionary<string, VariableSlot> variables, UdfFrame udfFrame)
    {
        this.Variables = variables;
        this.UdfFrame = udfFrame;
        this.Parser = new ParserContext(udfBodyCommand, this);
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
    /// <c>#foo</c> names, otherwise the named schema (or
    /// <see cref="Database.DefaultSchemaName"/> for an unqualified reference)
    /// plus the simulation's flat system-table dict. Centralizes the routing
    /// rule so callsites (SELECT/INSERT/UPDATE/DELETE/MERGE name lookups,
    /// <c>IDENT_CURRENT</c>, <c>SET IDENTITY_INSERT</c>) stay uniform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution by <see cref="MultiPartName.Count"/>:
    /// </para>
    /// <list type="bullet">
    /// <item>1-part <c>t</c> — temp dict (if <c>#</c>-prefixed); else default
    /// schema then system tables.</item>
    /// <item>2-part <c>schema.t</c> — named schema; falls through to false
    /// when the schema doesn't exist or doesn't hold a table by that name.
    /// System tables are <em>not</em> reachable through a schema qualifier
    /// (real SQL Server's <c>sys.&lt;table&gt;</c> isn't modeled).</item>
    /// <item>3-part <c>db.schema.t</c> — same as 2-part after validating the
    /// db segment matches <see cref="CurrentDatabase"/>'s name; mismatched db
    /// returns false.</item>
    /// <item>4-part <c>server.db.schema.t</c> — false (linked servers not
    /// modeled; the callsite raises Msg 208 via the standard path).</item>
    /// </list>
    /// <para>
    /// For <c>#</c>-prefixed leaves a qualifier is cosmetic and ignored —
    /// matches probe-confirmed behavior for <c>tempdb..#foo</c> /
    /// <c>tempdb.dbo.#foo</c> in DROP TABLE; the connection's temp-table dict
    /// is the routing key regardless of preceding segments.
    /// </para>
    /// </remarks>
    public bool TryResolveTable(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapTable? table)
    {
        if (IsLocalTempName(name.Leaf))
            return this.Connection.TempTables.TryGetValue(name.Leaf, out table);

        if (!this.TryResolveSchema(name, out var schema))
        {
            // 1-part fallback to system tables when the default schema lookup
            // misses; matches the legacy bare-`systypes` access path.
            if (name.Count == 1)
                return Simulation.SystemHeapTables.TryGetValue(name.Leaf, out table);
            table = null;
            return false;
        }

        if (schema.HeapTables.TryGetValue(name.Leaf, out table))
            return true;

        // Bare 1-part also falls through to system tables when the default
        // schema doesn't hold the table.
        if (name.Count == 1)
            return Simulation.SystemHeapTables.TryGetValue(name.Leaf, out table);

        table = null;
        return false;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to the <see cref="Schema"/> a CREATE /
    /// DROP / TRUNCATE / SELECT-INTO target lives in. Returns false when the
    /// schema (the segment to the left of the leaf) doesn't exist, when a
    /// 3-part name's db segment doesn't match <see cref="CurrentDatabase"/>,
    /// or when the name is 4-part (linked-server names aren't modeled — the
    /// simulator returns false rather than silently ignoring the server
    /// segment). A 1-part name resolves to <see cref="Database.DefaultSchemaName"/>
    /// (always present, so this branch never returns false).
    /// </summary>
    public bool TryResolveSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema)
    {
        if (name.Count >= 4)
        {
            schema = null;
            return false;
        }
        if (name.Count == 3 && !Collation.Default.Equals(name[0], this.CurrentDatabase.Name))
        {
            schema = null;
            return false;
        }
        var schemaName = name.Count >= 2 ? name.ImmediateQualifier! : Database.DefaultSchemaName;
        return this.CurrentDatabase.Schemas.TryGetValue(schemaName, out schema);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered scalar
    /// <see cref="UserDefinedFunction"/>. Schema-qualified (2- or 3-part)
    /// references route through <see cref="TryResolveSchema"/>; 1-part names
    /// fall through to <see langword="false"/> (real SQL Server treats
    /// unqualified UDF calls as built-in function lookups, raising Msg 195
    /// when nothing matches — the call site enforces that 2-part minimum by
    /// only invoking this resolver when <see cref="MultiPartName.Count"/>
    /// is &gt;= 2).
    /// </summary>
    public bool TryResolveFunction(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function)
    {
        function = null;
        return name.Count >= 2
            && this.TryResolveSchema(name, out var schema)
            && schema.Functions.TryGetValue(name.Leaf, out function);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="View"/>.
    /// Unlike scalar UDFs, views accept 1-part names too (probe-confirmed:
    /// <c>FROM v1</c> works the same as <c>FROM dbo.v1</c>) — the lookup
    /// falls back to <see cref="Database.DefaultSchemaName"/> for the
    /// unqualified case. Schema-qualified misses return false; the caller
    /// is responsible for routing those to Msg 208.
    /// </summary>
    public bool TryResolveView(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        return this.TryResolveSchema(name, out var schema)
            && schema.Views.TryGetValue(name.Leaf, out view);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a <see cref="CatalogView"/> in
    /// either the <c>sys</c> or <c>INFORMATION_SCHEMA</c> schema. Returns
    /// true for 2-part names <c>{sys|INFORMATION_SCHEMA}.&lt;view&gt;</c>
    /// (case-insensitive) whose leaf matches a registered view, or for
    /// 3-part names whose db segment matches <see cref="CurrentDatabase"/>.
    /// Used by the FROM parser to route catalog-view references to virtual
    /// projections before falling through to the regular
    /// <see cref="TryResolveTable"/> path. The registry is keyed by the
    /// fully-qualified name (e.g. <c>"sys.tables"</c>,
    /// <c>"INFORMATION_SCHEMA.COLUMNS"</c>) so one resolver can serve both
    /// schemas without per-namespace dispatch.
    /// </summary>
    public bool TryResolveCatalogView(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogView? view)
    {
        view = null;
        if (name.Count is not (2 or 3))
            return false;
        if (name.Count == 3 && !Collation.Default.Equals(name[0], this.CurrentDatabase.Name))
            return false;
        var key = $"{name.ImmediateQualifier}.{name.Leaf}";
        return Simulation.CatalogViews.TryGetValue(key, out view);
    }

    /// <summary>
    /// Parses an object name (1–4 dotted segments) at the current token,
    /// leaving the cursor on the <em>last</em> consumed name segment (matching
    /// the standard parser-context contract that every parser leaves Token on
    /// its last consumed token). Empty segments (<c>tempdb..#foo</c>, db with
    /// omitted schema) are tolerated — they're silently compressed out, so
    /// <c>tempdb..#foo</c> returns a 2-part name (<c>tempdb</c> + <c>#foo</c>).
    /// Used everywhere a table-shaped name appears (CREATE / DROP / TRUNCATE
    /// / SELECT-FROM / INSERT / UPDATE / DELETE / MERGE / SET IDENTITY_INSERT)
    /// so the multi-part-name grammar lives in one place. The 5th segment
    /// raises Msg 4104 via <see cref="MultiPartName.WithAddedPart"/>.
    /// </summary>
    public static MultiPartName ParseObjectName(ParserContext context)
    {
        if (context.Token is not Name first)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = new MultiPartName(first.Value);
        while (true)
        {
            // Peek for a `.` continuation without permanently advancing — if
            // the next token isn't a dot, restore so the cursor sits on the
            // last consumed name segment.
            var checkpoint = context.SaveCheckpoint();
            if (!context.MoveNext() || context.Token is not Operator { Character: '.' })
            {
                context.RestoreCheckpoint(checkpoint);
                return name;
            }

            // Advanced past the dot. Read the next segment — a Name extends
            // the dotted name; a second `.` is an empty segment that we skip
            // and read one more time.
            if (!context.MoveNext())
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is Name next)
            {
                name = name.WithAddedPart(next.Value);
                continue;
            }
            if (context.Token is Operator { Character: '.' } && context.MoveNext() && context.Token is Name afterEmpty)
            {
                name = name.WithAddedPart(afterEmpty.Value);
                continue;
            }
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
