using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Matches <paramref name="leaf"/> against the system procedure names
    /// under the database collation's equality, returning the canonical
    /// as-declared name (so the dispatch switch in <see cref="ParseExec"/>
    /// can match ordinal string constants) or null when the name is not a
    /// system procedure. Matching is collation-aware — probe-confirmed
    /// (2026-05-21): system proc names follow the database collation, so
    /// <c>SP_EXECUTESQL</c> on a case-sensitive database misses here and
    /// falls through to "procedure not found".
    /// </summary>
    private static string? ResolveSystemProcedureName(Collation collation, string leaf)
    {
        // Lazily built once per interned collation instance (stable across
        // ALTER DATABASE COLLATE, which swaps the database's field to a
        // different interned instance). A concurrent first-touch race is
        // benign: both threads build identical sets and the reference
        // assignment is atomic.
        // Canonical list + per-proc rationale live on BuiltInResources.SystemProcedureNames
        // (shared with the sys.system_objects projection). Each xp_/sp_ leaf
        // routes here from any current database (real SQL Server resolves
        // sp_/xp_ system procs through master).
        var lookup = collation.SystemProcedureLookup ??= new HashSet<string>(BuiltInResources.SystemProcedureNames, collation);
        return lookup.TryGetValue(leaf, out var canonical) ? canonical : null;
    }

    /// <summary>
    /// Parses an <c>[@rc =] EXEC[UTE] target [args]</c> statement where
    /// <c>target</c> is either a procedure name (regular EXEC), a
    /// parenthesized dynamic-SQL operand (<c>EXEC ('<i>sql</i>')</c>), or
    /// the special <c>sp_executesql</c> shape. Yields the invoked code's
    /// result sets (procedure body SELECTs / dynamic-SQL statements) to the
    /// outer caller's iterator. Probed against SQL Server 2025 (2026-05-12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Argument grammar (per <see cref="ParseExecArgument"/>): each EXEC
    /// argument is either a literal, an <c>@variable</c> reference (with an
    /// optional <c>OUTPUT</c>/<c>OUT</c> suffix), or the <c>DEFAULT</c>
    /// keyword. Arithmetic expressions in argument position are NOT
    /// accepted — probe-confirmed: <c>EXEC p @x - 1</c> raises Msg 102 in
    /// real SQL Server.
    /// </para>
    /// <para>
    /// Positional and named args can mix only with named coming AFTER
    /// positional. Once a <c>@name = value</c> appears, every subsequent
    /// arg must also be in that form (Msg 119 verbatim wording probe-
    /// confirmed). Within named args, no duplicates (Msg 8143) and no
    /// unknown parameter names (Msg 201).
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseExec(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume EXEC / EXECUTE

        // EXECUTE AS { LOGIN | USER } = 'name' collides with proc invocation at
        // the EXEC keyword; the AS keyword after EXECUTE disambiguates. It
        // yields no result sets.
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            ExecuteAsStatement(batch);
            yield break;
        }

        // Optional `@rc = ` return-code capture between EXEC and the proc
        // name. The grammar is `EXEC [@rc = ] proc_name [args]` — probe-
        // confirmed against SQL Server 2025. Peek for `@var =` and consume
        // both tokens when present; the dynamic-SQL form `EXEC (@sql)`
        // doesn't accept a return-code variable.
        string? returnCodeVar = null;
        if (context.Token is AtPrefixedString rcCandidate)
        {
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextRequired();
            if (context.Token is Operator { Character: '=' })
            {
                returnCodeVar = rcCandidate.Value;
                context.MoveNextRequired();
            }
            else
            {
                context.RestoreCheckpoint(checkpoint);
            }
        }

        // EXEC (<string-expr>) — dynamic-SQL form. The expression's value
        // is re-tokenized as a fresh batch inside its own child
        // BatchContext (so outer @vars aren't visible, matching probed
        // behavior).
        if (context.Token is Operator { Character: '(' })
        {
            foreach (var outcome in ParseExecDynamicSql(batch, returnCodeVar))
                yield return outcome;
            yield break;
        }

        // A leading `.` opens a name whose db/schema positions are omitted
        // (`..sp_tablecollations_100`, the form SqlClient's SqlBulkCopy sends);
        // ParseObjectName consumes the empty leading segments.
        if (context.Token is not Name and not Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var procName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        // System procedures route to built-in handlers before generic
        // resolution. ResolveSystemProcedureName does the collation-aware
        // match and hands back the canonical as-declared name, so the
        // switch arms below match ordinary string constants regardless of
        // the SQL text's casing. A null falls through to user-procedure
        // resolution.
        var systemProcName = ResolveSystemProcedureName(batch.CurrentDatabase.Collation, procName.Leaf);
        var systemProc = systemProcName switch
        {
            null => null,
            "sp_addextendedproperty" => InvokeSpExtendedProperty(batch, ExtendedPropertyOp.Add),
            "sp_addlinkedserver" => InvokeSpAddLinkedServer(batch),
            "sp_addlinkedsrvlogin" or "sp_droplinkedsrvlogin" or "sp_serveroption" => InvokeSpLinkedServerNoOp(batch),
            "sp_datatype_info_100" => InvokeSpDatatypeInfo100(batch),
            "sp_dropextendedproperty" => InvokeSpExtendedProperty(batch, ExtendedPropertyOp.Drop),
            "sp_dropserver" => InvokeSpDropServer(batch),
            "sp_executesql" => ParseSpExecuteSql(batch, returnCodeVar),
            "sp_getapplock" => InvokeSpGetAppLock(batch, returnCodeVar),
            "sp_releaseapplock" => InvokeSpReleaseAppLock(batch, returnCodeVar),
            "sp_set_session_context" => InvokeSpSetSessionContext(batch),
            "sp_tablecollations_100" => InvokeSpTableCollations(batch),
            "sp_updateextendedproperty" => InvokeSpExtendedProperty(batch, ExtendedPropertyOp.Update),
            "xp_instance_regread" => InvokeXpInstanceRegread(batch),
            "xp_msver" => InvokeXpMsver(batch),
            "xp_qv" => InvokeXpQv(batch, returnCodeVar),
            _ => throw new InvalidOperationException($"{systemProcName} is in SystemProcedureNames but has no dispatch arm."),
        };
        if (systemProc is not null)
        {
            foreach (var outcome in systemProc)
                yield return outcome;
            yield break;
        }

        // Args + invocation. Skip-mode runs the arg parser (cursor advance,
        // syntax errors still fire), but suppresses the invocation itself.
        var arguments = ParseExecArguments(context, batch);

        if (batch.IsSkipping)
            yield break;

        if (!batch.TryResolveProcedure(procName, out var procedure))
            throw SimulatedSqlException.CouldNotFindStoredProcedure(procName.ToString());

        foreach (var outcome in this.InvokeProcedure(batch, procedure, arguments, returnCodeVar))
            yield return outcome;
    }

    /// <summary>
    /// Parses the EXEC argument list (everything from the first argument
    /// token to the trailing statement boundary). Enforces the positional-
    /// before-named rule (Msg 119) and the no-duplicate-name rule (Msg
    /// 8143). Cursor on entry: first argument token (or the trailing
    /// terminator if the call has no args). Cursor on exit: the trailing
    /// terminator.
    /// </summary>
    private static List<ProcArgument> ParseExecArguments(ParserContext context, BatchContext batch)
    {
        var arguments = new List<ProcArgument>();
        // No args at all — return empty list. The terminator is either `;`,
        // end-of-batch, or a statement-starting keyword.
        if (IsStatementBoundary(context.Token))
            return arguments;

        var sawNamed = false;
        var seenNames = new HashSet<string>(batch.CurrentDatabase.Collation);
        while (true)
        {
            string? argName = null;

            // Named-arg form: `@name = value`. Peek for the `=` to
            // disambiguate from a bare `@var` positional argument. EOF after
            // the `@name` is legal here (the `@var` is the last positional
            // argument), so use MoveNextOptional and just bail to the
            // positional path if no token follows.
            if (context.Token is AtPrefixedString nameToken)
            {
                var checkpoint = context.SaveCheckpoint();
                context.MoveNextOptional();
                if (context.Token is Operator { Character: '=' })
                {
                    argName = nameToken.Value;
                    // Msg 8143 echoes the first-seen spelling of the name —
                    // probe-confirmed (2026-07-13): `@a=1, @ａ=2` (fullwidth
                    // duplicate under the collation's width folding) reports
                    // "Parameter '@a' was supplied multiple times."
                    if (seenNames.TryGetValue(argName, out var firstSpelling))
                        throw SimulatedSqlException.ParameterSuppliedMultipleTimes(firstSpelling);
                    _ = seenNames.Add(argName);
                    sawNamed = true;
                    context.MoveNextRequired();
                }
                else
                {
                    // Restore — this @var is a positional argument value.
                    context.RestoreCheckpoint(checkpoint);
                }
            }

            if (argName is null && sawNamed)
                throw SimulatedSqlException.MustPassParameterAsNamed();

            arguments.Add(ParseExecArgument(context, batch, argName));

            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            break;
        }
        return arguments;
    }

    /// <summary>
    /// Parses one EXEC argument value (after any leading <c>@name =</c>).
    /// Accepted forms: a literal token (numeric / string / NULL / boolean),
    /// an <c>@variable</c> reference (with optional trailing <c>OUTPUT</c>
    /// or <c>OUT</c>), or the <c>DEFAULT</c> keyword. An optional leading
    /// <c>+</c>/<c>-</c> sign on numeric literals is accepted (the only
    /// expression operator real SQL Server permits in EXEC args; arithmetic
    /// combiners like <c>@x - 1</c> raise Msg 102). Cursor on entry: the
    /// first token of the argument value; cursor on exit: the trailing
    /// separator (<c>,</c> or terminator).
    /// </summary>
    private static ProcArgument ParseExecArgument(ParserContext context, BatchContext batch, string? name)
    {
        // DEFAULT keyword: caller is explicitly asking to use the param's
        // declared default.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
        {
            context.MoveNextOptional();
            return new ProcArgument(name, isDefault: true, value: SqlValue.Null(SqlType.Int32), outputSlot: null);
        }

        // @@-prefixed niladic function (e.g. @@SERVICENAME, which SSMS's
        // AlwaysOn probe passes to xp_qv). Evaluate the single atom in a
        // column-less runtime context; session-state forms (@@SPID /
        // @@ROWCOUNT / …) read their live value through the batch.
        if (context.Token is DoubleAtPrefixedString)
        {
            var expression = Expression.Parse(context);
            var value = expression.Run(new RuntimeContext(
                columnName => throw SimulatedSqlException.ColumnReferenceNotAllowed(columnName), batch));
            return new ProcArgument(name, isDefault: false, value: value, outputSlot: null);
        }

        // @variable reference — capture the slot (live, so OUTPUT writeback
        // sees the proc's final value), read its value now, and check for a
        // trailing OUTPUT / OUT keyword. When the name resolves to a table
        // variable instead of a scalar, carry the live <see cref="HeapTable"/>
        // through the TVP-arg path; OUTPUT after a table-variable arg is
        // syntactically rejected by real SQL Server (TVP params are
        // implicitly read-only and don't write back).
        if (context.Token is AtPrefixedString varRef)
        {
            if (batch.TableVariables.TryGetValue(varRef.Value, out var tableVar))
            {
                context.MoveNextOptional();
                return new ProcArgument(name, isDefault: false, value: SqlValue.Null(SqlType.Int32), outputSlot: null, tableValue: tableVar);
            }
            // Cursor variable argument (`@c OUTPUT` for a cursor parameter):
            // carry the caller's variable name so the invocation can bind the
            // proc's assigned cursor back into it. The trailing OUTPUT keyword
            // is consumed like the scalar path.
            if (batch.CursorVariables.ContainsKey(varRef.Value))
            {
                context.MoveNextOptional();
                if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
                    context.MoveNextOptional();
                return new ProcArgument(name, isDefault: false, value: SqlValue.Null(SqlType.Int32), outputSlot: null, cursorVariableName: varRef.Value);
            }
            var slot = batch.GetVariableSlot(varRef.Value);
            context.MoveNextOptional();
            VariableSlot? outputSlot = null;
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                outputSlot = slot;
                context.MoveNextOptional();
            }
            return new ProcArgument(name, isDefault: false, value: slot.Value, outputSlot: outputSlot);
        }

        // Bare literal or sign-prefixed numeric literal. Anything more
        // structured (arithmetic, function call, parens) raises Msg 102
        // through the standard syntax-error path below.
        var negate = false;
        switch (context.Token)
        {
            case Operator { Character: '-' }:
                negate = true;
                context.MoveNextRequired();
                break;
            case Operator { Character: '+' }:
                context.MoveNextRequired();
                break;
        }

        SqlValue literalValue;
        switch (context.Token)
        {
            case Literal lit:
                literalValue = negate ? NegateLiteral(lit.Value) : lit.Value;
                break;
            case Numeric numeric:
                literalValue = negate ? NegateLiteral(numeric.Value) : numeric.Value;
                break;
            case ReservedKeyword { Keyword: Keyword.Null }:
                if (negate) throw SimulatedSqlException.SyntaxErrorNear(context);
                literalValue = SqlValue.Null(SqlType.Int32);
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return new ProcArgument(name, isDefault: false, value: literalValue, outputSlot: null);
    }

    private static SqlValue NegateLiteral(SqlValue v) =>
        v.IsNull ? v
        : v.Type == SqlType.Int32 ? SqlValue.FromInt32(-v.AsInt32)
        : v.Type == SqlType.BigInt ? SqlValue.FromInt64(-v.AsInt64)
        : v.Type == SqlType.SmallInt ? SqlValue.FromInt16((short)-v.AsInt32)
        : v;
}

/// <summary>
/// One argument supplied to an EXEC call after parsing. The
/// <see cref="Name"/> is non-null for the named-arg form (<c>@p = expr</c>)
/// and null for positional. <see cref="OutputSlot"/> is non-null when the
/// caller wrote <c>OUTPUT</c> on an <c>@variable</c>-valued arg — the
/// invocation writes the proc's final parameter value back into this slot
/// at exit.
/// </summary>
internal readonly struct ProcArgument(string? name, bool isDefault, SqlValue value, VariableSlot? outputSlot, HeapTable? tableValue = null, string? cursorVariableName = null)
{
    public readonly string? Name = name;
    public readonly bool IsDefault = isDefault;
    public readonly SqlValue Value = value;
    public readonly VariableSlot? OutputSlot = outputSlot;

    /// <summary>
    /// Non-null when the caller passed a cursor variable (<c>@c OUTPUT</c>) as
    /// the argument for a cursor parameter — the caller's cursor-variable name
    /// (leading <c>@</c> stripped). The invocation binds the cursor the proc
    /// body assigned to its cursor parameter back into this variable at exit.
    /// </summary>
    public readonly string? CursorVariableName = cursorVariableName;

    /// <summary>
    /// Non-null when the caller passed a table variable (or, eventually, an
    /// ADO.NET <see cref="System.Data.SqlDbType.Structured"/> parameter) as
    /// the argument value. <see cref="Value"/> is ignored when this is set;
    /// the binding path in <see cref="Simulation.InvokeProcedure"/> looks
    /// here first for the corresponding TVP parameter and routes through
    /// the child <see cref="BatchContext.TableVariables"/> dict.
    /// </summary>
    public readonly HeapTable? TableValue = tableValue;
}
