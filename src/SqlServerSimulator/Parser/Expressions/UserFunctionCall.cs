using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Call site of a user-defined scalar function: <c>SELECT schema.fn(arg1, arg2)</c>.
/// Bound at parse time to a specific <see cref="UserDefinedFunction"/> (the
/// function is looked up in the resolved schema's
/// <see cref="Schema.Functions"/> dict), so rename / drop after binding still
/// runs the original body — matches real SQL Server's compile-snapshot semantics
/// since the simulator has no recompilation triggers.
/// </summary>
/// <remarks>
/// <para>
/// Per-row execution: <see cref="Run"/> evaluates each argument expression in
/// the caller's <see cref="RuntimeContext"/>, then routes through
/// <see cref="Simulation.InvokeScalarFunction"/> which creates a child
/// <see cref="BatchContext"/> with parameters as variables and dispatches the
/// stored body. Recursion is capped at <see cref="SimulatedDbConnection.MaxNestingLevel"/>
/// (probe-confirmed 32-level limit raises Msg 217).
/// </para>
/// <para>
/// <strong>DEFAULT keyword.</strong> At parse time, an arg position of
/// <c>DEFAULT</c> (the <see cref="Keyword.Default"/> reserved keyword) is
/// recorded as a slot in the parsed arg list. At call time the slot is
/// replaced by the corresponding parameter's stored default expression
/// evaluated in the per-call child batch — probe-confirmed that bare
/// omission (<c>fn()</c>) raises Msg 313 even when the parameter has a
/// declared default, so the <c>DEFAULT</c> keyword is the only legal omission
/// path.
/// </para>
/// </remarks>
internal sealed class UserFunctionCall(ScalarFunction function, Expression?[] arguments) : Expression
{
    private readonly ScalarFunction function = function;

    // A return type written numeric names the call's value numeric.
    internal override bool ResultReportsNumeric => this.function.ReturnSpelledNumeric;

    internal override Schemas.AliasType? ResultAliasType => this.function.ReturnAliasType;

    /// <summary>
    /// One per declared parameter. Each entry is either an explicit
    /// <see cref="Expression"/> argument supplied by the caller, or
    /// <see langword="null"/> meaning the caller wrote <c>DEFAULT</c> and the
    /// function's stored default expression for that slot should be evaluated
    /// in the child batch at call time. A non-null entry whose corresponding
    /// parameter has no declared default fires Msg 313 at parse time, so the
    /// only way to reach a null entry is via the <c>DEFAULT</c> keyword.
    /// </summary>
    private readonly Expression?[] arguments = arguments;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // An argument takes its parameter's type as an assignment does
        // (probed 2026-09-24: a datetime passed to a decimal parameter is
        // Msg 257).
        for (var i = 0; i < this.arguments.Length && i < this.function.Parameters.Length; i++)
        {
            if (this.arguments[i] is { } argument and not TableValuedArgument)
                AssignmentRules.RequireAssignable(argument, argument.GetSqlType(batch, resolveColumnType), this.function.Parameters[i].Type);
        }
        return this.function.ReturnType;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // A skipped statement never calls the function: the body belongs to an
        // execution that doesn't happen. The gate is load-bearing rather than
        // an optimization because the FROM-less-SELECT fast path bakes its
        // projection values during the *parse*, so without it a dead branch's
        // `SELECT dbo.f()` — and every module body's, while it binds at CREATE
        // time — would dispatch the body and surface its runtime errors.
        if (runtime.Batch.IsSkipping)
            return SqlValue.Null(this.function.ReturnType);

        // Materialize argument values in the caller's row scope. NULL inputs
        // short-circuit when the function declared WITH RETURNS NULL ON NULL
        // INPUT (probe-confirmed: body never runs).
        var argCount = this.arguments.Length;
        var values = new SqlValue[argCount];
        HeapTable?[]? tables = null;
        for (var i = 0; i < argCount; i++)
        {
            var argExpr = this.arguments[i];
            if (argExpr is TableValuedArgument tableArgument)
            {
                values[i] = SqlValue.Null(this.function.Parameters[i].Type);
                (tables ??= new HeapTable?[argCount])[i] = tableArgument.Resolve(runtime);
                continue;
            }
            if (argExpr is null)
            {
                // DEFAULT slot — resolved inside InvokeScalarFunction since the
                // default expression runs in the child batch where its
                // variable references (typically constants) bind cleanly.
                values[i] = SqlValue.Null(this.function.Parameters[i].Type);
                continue;
            }
            var raw = argExpr.Run(runtime);
            values[i] = raw.CoerceTo(this.function.Parameters[i].Type);
        }

        // DEFAULT slots are signaled by a sentinel: marking the argument null
        // in the parsed-args array meant "DEFAULT keyword"; we need to tell
        // InvokeScalarFunction which slots to re-evaluate via the function's
        // stored default expression. Mirror the layout in a parallel bool[].
        var isDefault = new bool[argCount];
        for (var i = 0; i < argCount; i++)
            isDefault[i] = this.arguments[i] is null;

        return runtime.Batch.Connection.Simulation.InvokeScalarFunction(
            runtime.Batch,
            this.function,
            values,
            isDefault,
            tables);
    }

    internal override string DebugDisplay() =>
        $"{this.function.Schema.Name}.{this.function.Name}({string.Join(", ", this.arguments.Select(a => a?.DebugDisplay() ?? "DEFAULT"))})";

    internal override void Describe(NodeShape shape) => shape.Local(this.function).Children(this.arguments);

    /// <summary>
    /// Parses the comma-separated argument list of a <c>schema.fn(...)</c>
    /// call. Cursor on entry: the token <em>after</em> the opening <c>(</c>.
    /// Cursor on exit: the closing <c>)</c>.
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 313 when too few args are supplied (or a parameter has no default
    /// to back a DEFAULT keyword) or when a <c>DEFAULT</c> keyword lands at
    /// a parameter without a declared default. Msg 8144 when too many args
    /// are supplied.
    /// </exception>
    public static UserFunctionCall ParseCall(ScalarFunction function, ParserContext context)
    {
        // A scalar UDF invoked inside a query takes EXECUTE, checked once at
        // statement execution via the securable sink (never per row). Calls in
        // non-query contexts (SET / IF operands) have no active sink and stay
        // unchecked — a documented gap.
        context.SecurableSink?.Add(new ReferencedSecurable(function.Schema.Database, function, function.Schema.Name, "EXECUTE"));
        InlinedScalarCalls.Note(context, function);
        context.IndexedViewShapeCollector?.CalledFunctions.Add(function);
        var simulation = context.Batch.Connection.Simulation;
        return new(function, ParseFunctionArguments(function, context))
        {
            ReturnMask = simulation.DeclaresDataMasks ? simulation.ScalarFunctionReturnMask(context.Batch, function) : null,
        };
    }

    /// <summary>
    /// The masked columns the function's result reads, as its body's
    /// assignments and <c>RETURN</c>s carry them (see
    /// <see cref="Simulation.ScalarFunctionReturnMask"/>); null when none, or
    /// when the simulation has declared no mask.
    /// </summary>
    internal DataMask? ReturnMask;

    /// <summary>
    /// Parses the comma-separated argument list of a <c>schema.fn(...)</c>
    /// call against the function's parameter list, returning the per-parameter
    /// argument slots. Null entries are the <c>DEFAULT</c>-keyword marker (the
    /// caller materializes the stored default expression at execution time).
    /// Cursor on entry: the token <em>after</em> the opening <c>(</c>. Cursor
    /// on exit: the closing <c>)</c>.
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 313 when too few args are supplied (or a parameter has no default
    /// to back a DEFAULT keyword) or when a <c>DEFAULT</c> keyword lands at
    /// a parameter without a declared default. Msg 8144 when too many args
    /// are supplied.
    /// </exception>
    public static Expression?[] ParseFunctionArguments(UserDefinedFunction function, ParserContext context)
    {
        var arguments = new List<Expression?>();
        var declaredName = $"{function.Schema.Name}.{function.Name}";

        if (context.Token is not Operator { Character: ')' })
        {
            while (true)
            {
                var slot = arguments.Count;
                var tableType = slot < function.Parameters.Length ? function.Parameters[slot].TableType : null;
                if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
                {
                    if (slot >= function.Parameters.Length)
                        throw SimulatedSqlException.TooManyArgumentsToFunction(declaredName);
                    // A table-valued parameter's DEFAULT is an empty table, save
                    // in an inline function, whose plan has no table to stand in
                    // (probed 2026-10-04 against SQL Server 2025).
                    if (tableType is not null && function is InlineTableValuedFunction)
                        throw SimulatedSqlException.InvalidDefaultForParameter(slot + 1);
                    // A scalar function's DEFAULT for a parameter declaring
                    // none passes NULL (probed 2026-10-04 against SQL Server
                    // 2025); a table-valued function's is Msg 313.
                    if (tableType is null && function.Parameters[slot].Default is null && function is not ScalarFunction)
                        throw SimulatedSqlException.InsufficientArgumentsToFunction(declaredName);
                    arguments.Add(null); // DEFAULT marker
                    context.MoveNextRequired();
                }
                else
                {
                    arguments.Add(tableType is null ? Expression.Parse(context) : TableValuedArgument.Parse(context, tableType));
                }
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
        }

        // Insufficient args: real SQL Server raises Msg 313 even when missing
        // parameters have declared defaults — probe-confirmed `fn_default()`
        // with `@x int = 99` raises Msg 313. The DEFAULT keyword is the only
        // legal omission.
        // A table-valued function's shortfall is state 3 at line 12, the
        // line real gives what its function binding raises (probed 2026-10-04
        // against SQL Server 2025).
        return arguments.Count > function.Parameters.Length
            ? throw SimulatedSqlException.TooManyArgumentsToFunction(declaredName)
            : arguments.Count < function.Parameters.Length
                ? throw (function is ScalarFunction
                    ? SimulatedSqlException.InsufficientArgumentsToFunction(declaredName)
                    : SimulatedSqlException.InsufficientArgumentsToFunction(declaredName, state: 3).PinLine(12))
                : [.. arguments];
    }
}
