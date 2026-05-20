using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
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

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var procName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        // sp_executesql is a built-in proc with a special argument shape
        // (param-defs and OUTPUT writeback to @-variables in the caller's
        // scope). Route to its own parser before falling through to the
        // generic-procedure path.
        if (Collation.Default.Equals(procName.Leaf, "sp_executesql"))
        {
            foreach (var outcome in ParseSpExecuteSql(batch, returnCodeVar))
                yield return outcome;
            yield break;
        }

        // Extended-property sprocs — all 3 share argument parsing + target
        // resolution via InvokeSpExtendedProperty. The bacpac loader emits
        // `EXEC sp_addextendedproperty …` for every `<SqlExtendedProperty>`
        // element in model.xml; the update/drop variants round out the API.
        if (Collation.Default.Equals(procName.Leaf, "sp_addextendedproperty"))
        {
            foreach (var outcome in InvokeSpExtendedProperty(batch, ExtendedPropertyOp.Add))
                yield return outcome;
            yield break;
        }
        if (Collation.Default.Equals(procName.Leaf, "sp_updateextendedproperty"))
        {
            foreach (var outcome in InvokeSpExtendedProperty(batch, ExtendedPropertyOp.Update))
                yield return outcome;
            yield break;
        }
        if (Collation.Default.Equals(procName.Leaf, "sp_dropextendedproperty"))
        {
            foreach (var outcome in InvokeSpExtendedProperty(batch, ExtendedPropertyOp.Drop))
                yield return outcome;
            yield break;
        }

        // Linked-server sprocs — sp_addlinkedserver / sp_dropserver carry
        // semantic effect (activating / deactivating an entry in
        // Simulation.ActiveLinkedServers); sp_addlinkedsrvlogin /
        // sp_droplinkedsrvlogin / sp_serveroption parse-and-discard since
        // the simulator has no principal-mapping or per-server-option model
        // but BACPAC / migration scripts often emit them.
        if (Collation.Default.Equals(procName.Leaf, "sp_addlinkedserver"))
        {
            foreach (var outcome in InvokeSpAddLinkedServer(batch))
                yield return outcome;
            yield break;
        }
        if (Collation.Default.Equals(procName.Leaf, "sp_dropserver"))
        {
            foreach (var outcome in InvokeSpDropServer(batch))
                yield return outcome;
            yield break;
        }
        if (Collation.Default.Equals(procName.Leaf, "sp_addlinkedsrvlogin")
            || Collation.Default.Equals(procName.Leaf, "sp_droplinkedsrvlogin")
            || Collation.Default.Equals(procName.Leaf, "sp_serveroption"))
        {
            foreach (var outcome in InvokeSpLinkedServerNoOp(batch))
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
        if (IsExecArgumentBoundary(context.Token))
            return arguments;

        var sawNamed = false;
        var seenNames = new HashSet<string>(Collation.Default);
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
                    if (!seenNames.Add(argName))
                        throw SimulatedSqlException.ParameterSuppliedMultipleTimes(argName);
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

    private static bool IsExecArgumentBoundary(Token? token) =>
        token is null
        or Operator { Character: ';' }
        or ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.Begin or Keyword.Commit or Keyword.Rollback or Keyword.Save or Keyword.Create or Keyword.Drop or Keyword.Alter or Keyword.Dbcc or Keyword.Set or Keyword.Declare or Keyword.With or Keyword.If or Keyword.Else or Keyword.End or Keyword.While or Keyword.Break or Keyword.Continue or Keyword.Return or Keyword.Print or Keyword.RaisError or Keyword.WaitFor or Keyword.Truncate or Keyword.Exec or Keyword.Execute }
        or UnquotedString { ContextualKeyword: ContextualKeyword.Throw };
}

/// <summary>
/// One argument supplied to an EXEC call after parsing. The
/// <see cref="Name"/> is non-null for the named-arg form (<c>@p = expr</c>)
/// and null for positional. <see cref="OutputSlot"/> is non-null when the
/// caller wrote <c>OUTPUT</c> on an <c>@variable</c>-valued arg — the
/// invocation writes the proc's final parameter value back into this slot
/// at exit.
/// </summary>
internal readonly struct ProcArgument(string? name, bool isDefault, SqlValue value, VariableSlot? outputSlot, HeapTable? tableValue = null)
{
    public readonly string? Name = name;
    public readonly bool IsDefault = isDefault;
    public readonly SqlValue Value = value;
    public readonly VariableSlot? OutputSlot = outputSlot;

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
