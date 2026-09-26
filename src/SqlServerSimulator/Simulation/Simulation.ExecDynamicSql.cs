using System.Runtime.ExceptionServices;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>EXEC ( &lt;string-expression&gt; )</c> — dynamic SQL via
    /// EXEC. The string operand is evaluated in the caller's batch (so
    /// concatenation works: <c>EXEC ('SELECT ' + @col + ' FROM t')</c>),
    /// then the resulting string is re-tokenized and dispatched as a fresh
    /// batch inside its own <see cref="BatchContext"/>. Outer variables
    /// are NOT visible inside the dynamic batch (probe-confirmed: real SQL
    /// Server raises Msg 137 if the dynamic SQL references an outer
    /// <c>@var</c>). Result sets from the dynamic batch propagate to the
    /// outer caller.
    /// </summary>
    /// <remarks>
    /// Cursor on entry: the opening <c>(</c> after EXEC. Cursor on exit:
    /// the token after the closing <c>)</c>. Skip-mode evaluates the
    /// expression (cursor advance) but suppresses the dispatch.
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseExecDynamicSql(BatchContext batch, bool insertExecSource = false)
    {
        var context = batch.Parser;
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var sqlExpression = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        var resultSets = ParseExecuteOptions(batch, insertExecSource);

        if (batch.IsSkipping)
            yield break;

        var connection = batch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        var sqlValue = sqlExpression.Run(new RuntimeContext(
            name => throw SimulatedSqlException.MustDeclareScalarVariable(name.Leaf),
            batch));
        if (sqlValue.IsNull)
            yield break; // dynamic SQL of NULL → no-op (matches real SQL Server's lenient handling)

        var sqlText = sqlValue.CoerceTo(VarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;
        var dynamicBatch = ExecuteDynamicBatch(batch, sqlText, preDeclaredVariables: null);
        foreach (var outcome in resultSets is null ? dynamicBatch : ApplyResultSetsContract(dynamicBatch, resultSets))
            yield return outcome;
        batch.CurrentStatement.SuppressErrorReset = true;
    }

    /// <summary>
    /// Parses an <c>EXEC sp_executesql N'sql', N'@p1 type [OUTPUT], ...',
    /// @p1 = arg1, ...</c> call. The first argument is the SQL text; the
    /// second (optional) is a parameter-declaration string that pre-declares
    /// the dynamic batch's <c>@</c>-variables; remaining args bind values
    /// (with optional <c>OUTPUT</c> for writeback). Outer <c>@</c>-variables
    /// are NOT visible inside the dynamic batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cursor on entry: first token after the parsed <c>sp_executesql</c>
    /// procedure name (a literal, an <c>@</c>-variable, or end-of-args).
    /// Cursor on exit: the trailing statement boundary.
    /// </para>
    /// <para>
    /// The parameter-declaration string is mini-parsed: comma-separated
    /// entries each shaped like <c>@name type [OUTPUT]</c>. Values for each
    /// declared parameter come from the trailing arg list (positional or
    /// named); OUTPUT parameters write back to the caller's variable slot
    /// at exit. Probe-confirmed against SQL Server 2025.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseSpExecuteSql(BatchContext batch, string? returnCodeVar, bool insertExecSource = false)
    {
        var context = batch.Parser;

        // Argument 1: SQL text (literal or @-variable, coerced to string).
        // A leading `@name =` is accepted and the name discarded: real binds
        // sp_executesql's first two arguments purely by position and does not
        // check what they were called, so `@stmt =`, `@statement =`, `@sql =`
        // and even `@nonsense =` all run the same statement (probe-confirmed).
        // Writing the second parameter's name first doesn't reorder them
        // either — real takes `@params = N'@x int', @stmt = N'…'` as
        // statement-then-declarations and tries to run `@x int` as the batch.
        // This is what the SSMS / DacFx "create the stub if absent" idiom
        // emits, so it is the common spelling rather than an exotic one.
        // Whether an argument was named matters even though its name is
        // discarded: real's Msg 119 counts the whole list, so naming the
        // first argument obliges every later one to be named too.
        var sawNamedArgument = TryConsumeSpExecuteSqlArgumentName(context) is not null;
        var (sqlRaw, _) = ParseSpExecuteSqlValueArg(context, batch);
        var sqlValue = sqlRaw.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
        var hasMoreArgs = context.Token is Operator { Character: ',' };

        // Argument 2 (optional): parameter-declaration string.
        List<SpExecuteSqlParam>? declaredParams = null;
        // Kept verbatim: Msg 8178 quotes the two argument strings exactly as
        // written, spacing included.
        var paramDefsText = "";
        SqlType? paramDefsType = null;
        if (hasMoreArgs)
        {
            context.MoveNextRequired();
            if (TryConsumeSpExecuteSqlArgumentName(context) is not null)
                sawNamedArgument = true;
            else if (sawNamedArgument)
                throw SimulatedSqlException.MustPassParameterAsNamed();
            var (paramDefsRaw, _) = ParseSpExecuteSqlValueArg(context, batch);
            paramDefsType = paramDefsRaw.Type;
            var paramDefs = paramDefsRaw.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
            // A declaration argument of another type is refused when the call
            // runs (Msg 214), so its text is never parsed as declarations.
            if (!paramDefs.IsNull && SqlType.IsNationalStringCategory(paramDefsType))
            {
                paramDefsText = paramDefs.AsString;
                declaredParams = ParseSpExecuteSqlParamDefinitions(paramDefsText, batch.Connection);
            }
            hasMoreArgs = context.Token is Operator { Character: ',' };
        }

        // Remaining args: positional/named values bound to declared params.
        var argumentValues = new List<(string? Name, SqlValue Value, VariableSlot? OutputSlot, bool IsUntypedNull)>();
        while (hasMoreArgs)
        {
            context.MoveNextRequired();
            // Unlike the first two, a trailing value argument's name is
            // load-bearing: real matches it against the declaration list, so
            // `@y = 2, @x = 1` binds by name rather than by position
            // (probe-confirmed), and an OUTPUT writeback needs it.
            var argName = TryConsumeSpExecuteSqlArgumentName(context);
            if (argName is not null)
                sawNamedArgument = true;
            else if (sawNamedArgument)
                throw SimulatedSqlException.MustPassParameterAsNamed();
            var isUntypedNull = context.Token is ReservedKeyword { Keyword: Keyword.Null };
            var (argValue, argOutputSlot) = ParseSpExecuteSqlValueArg(context, batch);
            argumentValues.Add((argName, argValue, argOutputSlot, isUntypedNull));
            hasMoreArgs = context.Token is Operator { Character: ',' };
        }

        var resultSets = ParseExecuteOptions(batch, insertExecSource);

        if (batch.IsSkipping)
            yield break;

        if (!SqlType.IsNationalStringCategory(sqlRaw.Type))
            throw SimulatedSqlException.SpExecuteSqlArgumentNotUnicode("@statement", 2);
        if (paramDefsType is not null && !SqlType.IsNationalStringCategory(paramDefsType))
            throw SimulatedSqlException.SpExecuteSqlArgumentNotUnicode("@params", 3);

        if (sqlValue.IsNull)
            yield break;

        var sqlText = sqlValue.AsString;
        var connection = batch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // Bind declared params: positional fill first, then named lookup. An
        // error binding the arguments reports line 0, as a procedure's does —
        // but a declared parameter missing from a call that supplied none is
        // the statement's own line (probed 2026-09-26 against SQL Server 2025).
        SimulatedSqlException ArgumentBindingError(SimulatedSqlException error) =>
            argumentValues.Count > 0 ? error.PinLine(0) : error;
        var preDeclared = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var outputBindings = new List<(SpExecuteSqlParam Param, VariableSlot CallerSlot)>();
        if (declaredParams is not null)
        {
            var positional = 0;
            var bound = new SqlValue?[declaredParams.Count];
            var boundOutputSlots = new VariableSlot?[declaredParams.Count];
            var boundIsUntypedNull = new bool[declaredParams.Count];
            // Real checks the declarations for completeness *before* it
            // complains about a name it doesn't recognize, so an unknown name
            // alongside a missing declared one reports the missing one
            // (probe-confirmed) — hence the flag rather than an immediate
            // throw.
            var sawUnknownName = false;
            if (declaredParams.Count == 0 && argumentValues.Count > 0)
                throw SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine("").PinLine(0);
            foreach (var (name, value, outputSlot, isUntypedNull) in argumentValues)
            {
                int idx;
                if (name is null)
                {
                    idx = positional++;
                    if (idx >= declaredParams.Count)
                        throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
                }
                else
                {
                    idx = -1;
                    for (var i = 0; i < declaredParams.Count; i++)
                    {
                        if (context.Batch.CurrentDatabase.Collation.Equals(declaredParams[i].Name, name))
                        {
                            idx = i;
                            break;
                        }
                    }
                    if (idx < 0)
                    {
                        sawUnknownName = true;
                        continue;
                    }
                }
                // A second value for one parameter is a surplus argument.
                if (bound[idx] is not null)
                    throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
                bound[idx] = value;
                boundOutputSlots[idx] = outputSlot;
                boundIsUntypedNull[idx] = isUntypedNull;
            }
            for (var i = 0; i < declaredParams.Count; i++)
            {
                var param = declaredParams[i];
                // Every declared parameter without a default has to be
                // supplied — an explicit NULL counts, an omission does not,
                // and OUTPUT parameters are no exception. Where several are missing real names the first
                // declared one, which is what this loop's order gives.
                // The stored name is unprefixed (it keys a variable slot); the
                // message spells it the way the declaration did.
                if (bound[i] is null)
                {
                    bound[i] = param.Default ?? throw ArgumentBindingError(SimulatedSqlException.ParameterizedQueryExpectsParameter(paramDefsText, sqlText, "@" + param.Name));
                    boundIsUntypedNull[i] = bound[i]!.Value.IsNull;
                }
                if (!boundIsUntypedNull[i])
                    AssignmentRules.RequireAssignable(bound[i]!.Value.Type, param.Type);
                var initialValue = BindParameterValue(bound[i]!.Value, param.Type, param.DeclaredMaxLength, procedure: "");
                var slot = new VariableSlot(param.Type, param.DeclaredMaxLength, initialValue, parameter: null);
                preDeclared[param.Name] = slot;
                if (param.IsOutput && boundOutputSlots[i] is { } caller)
                    outputBindings.Add((param, caller));
            }

            // Only once every declaration is satisfied does an unrecognized
            // argument name become the complaint — real reports the missing
            // declaration first when both are wrong. The name is empty, which
            // is why real's message carries a double space.
            if (sawUnknownName)
                throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
        }

        // The status sp_executesql returns is @@ERROR as its batch left it,
        // including when an error of the batch's own ended it, so an error is
        // held until the status is written (probed 2026-09-24 against
        // SQL Server 2025), and what the batch sent before it still goes first.
        var dynamicBatch = ExecuteDynamicBatch(batch, sqlText, preDeclared);
        List<SimulatedStatementOutcome> outcomes = [];
        SimulatedSqlException? failure = null;
        try
        {
            foreach (var outcome in resultSets is null ? dynamicBatch : ApplyResultSetsContract(dynamicBatch, resultSets))
                outcomes.Add(outcome);
        }
        catch (SimulatedSqlException ex)
        {
            failure = ex;
        }

        // Writeback: sp_executesql's OUTPUT params copy the dynamic batch's
        // final variable values back to the caller's slots.
        if (failure is null)
        {
            foreach (var (param, callerSlot) in outputBindings)
            {
                if (preDeclared.TryGetValue(param.Name, out var slot))
                    callerSlot.Value = slot.Value.CoerceTo(callerSlot.DeclaredType);
            }
        }

        if (returnCodeVar is not null && failure is null or { EndedCalledBatch: true })
        {
            var rcSlot = batch.GetVariableSlot(returnCodeVar);
            rcSlot.Value = SqlValue.FromInt32(failure?.Number ?? batch.Connection.LastErrorNumber).CoerceTo(rcSlot.DeclaredType);
        }

        foreach (var outcome in outcomes)
            yield return outcome;
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        batch.CurrentStatement.SuppressErrorReset = true;
    }

    /// <summary>
    /// Parses one sp_executesql positional / named-value argument. Accepts
    /// the same shapes as a regular EXEC argument
    /// (<see cref="ParseExecArgument"/>) but doesn't enforce the no-mixed-
    /// position rule — sp_executesql's grammar is more permissive.
    /// </summary>
    /// <summary>
    /// Consumes an <c>@name =</c> prefix and returns the name, leaving the
    /// cursor on the value. Returns null with the cursor unmoved when the next
    /// token isn't one — an <c>@</c>-variable holding the argument's value
    /// looks identical until the following token settles it, which is why this
    /// runs off a checkpoint rather than a single-token peek.
    /// </summary>
    private static string? TryConsumeSpExecuteSqlArgumentName(ParserContext context)
    {
        if (context.Token is not AtPrefixedString candidate)
            return null;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            return candidate.Value;
        }
        context.RestoreCheckpoint(checkpoint);
        return null;
    }

    private static (SqlValue Value, VariableSlot? OutputSlot) ParseSpExecuteSqlValueArg(ParserContext context, BatchContext batch)
    {
        if (context.Token is AtPrefixedString varRef)
        {
            var slot = batch.GetVariableSlot(varRef.Value);
            context.MoveNextOptional();
            VariableSlot? outputSlot = null;
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                outputSlot = slot;
                context.MoveNextOptional();
            }
            return (slot.Value, outputSlot);
        }
        if (context.Token is Literal lit)
        {
            context.MoveNextOptional();
            return (lit.Value, null);
        }
        if (context.Token is Numeric num)
        {
            context.MoveNextOptional();
            return (num.Value, null);
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.Null })
        {
            context.MoveNextOptional();
            return (SqlValue.Null(SqlType.Int32), null);
        }
        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Mini-parser for sp_executesql's parameter-declaration string. Splits
    /// on commas (outside parens), then parses each segment as
    /// <c>@name type [OUTPUT]</c>. Returns the ordered parameter list used
    /// to seed the dynamic batch.
    /// </summary>
    private static List<SpExecuteSqlParam> ParseSpExecuteSqlParamDefinitions(string source, SimulatedDbConnection connection)
    {
        if (string.IsNullOrWhiteSpace(source))
            return [];

        // Real parses the definitions as the parenthesized list it prints in
        // Msg 8178 — `(@p int)` — so a list that ends early is a syntax error
        // near that closing `)`, and text after it is Msg 4124. A synthetic
        // SimulatedDbCommand lets the tokenizer walk it; nothing dispatches.
        using var defCommand = new SimulatedDbCommand(connection.Simulation, connection);
#pragma warning disable CA2100
        defCommand.CommandText = "(" + source + ")";
#pragma warning restore CA2100
        var defBatch = new BatchContext(defCommand);
        var defContext = defBatch.Parser;
        defContext.MoveNextOptional();
        defContext.MoveNextRequired();

        var parameters = new List<SpExecuteSqlParam>();
        while (true)
        {
            if (defContext.Token is not AtPrefixedString name)
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            defContext.MoveNextRequired();

            // Type parsing reuses the procedure-parameter type grammar.
            var (type, declaredMaxLength) = ParseSpExecuteSqlParamType(defContext, parameters.Count + 1);

            // A default comes before OUTPUT and is a constant, as a procedure
            // parameter's is: `@p int = 5 OUTPUT` (probed 2026-09-25 against
            // SQL Server 2025).
            SqlValue? defaultValue = null;
            if (defContext.Token is Operator { Character: '=' })
            {
                defaultValue = ParseSpExecuteSqlParamDefault(defContext);
                defContext.MoveNextRequired();
            }

            var isOutput = false;
            if (defContext.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                isOutput = true;
                defContext.MoveNextOptional();
            }

            parameters.Add(new SpExecuteSqlParam(name.Value, type, declaredMaxLength, isOutput, defaultValue));

            if (defContext.Token is Operator { Character: ',' })
            {
                defContext.MoveNextRequired();
                continue;
            }
            if (defContext.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            if (defContext.GetNextOptional() is not null)
                throw SimulatedSqlException.BatchParametersNotValid();
            return parameters;
        }
    }

    /// <summary>
    /// Reads a parameter declaration's <c>= constant</c> default, entered on the
    /// <c>=</c> and leaving the cursor on the constant: a literal, a signed
    /// number, <c>NULL</c>, or <c>DEFAULT</c>, which reads as NULL. A
    /// variable is Msg 137 and anything else a syntax error at it.
    /// </summary>
    private static SqlValue ParseSpExecuteSqlParamDefault(ParserContext context)
    {
        var negate = false;
        if (context.GetNextRequired() is Operator { Character: '-' or '+' } sign)
        {
            negate = sign.Character == '-';
            context.MoveNextRequired();
            if (context.Token is not Numeric)
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return context.Token switch
        {
            Numeric number => negate
                ? Parser.Expressions.Negate.Of(new Parser.Expressions.Value(number.Value, number.IntegerLiteralDigitCount)).Run(new RuntimeContext(static name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch))
                : number.Value,
            Literal { Value: var literal } => literal,
            ReservedKeyword { Keyword: Keyword.Null or Keyword.Default } => SqlValue.Null(SqlType.Int32),
            AtPrefixedString variable => throw SimulatedSqlException.MustDeclareScalarVariable(variable.Value.TrimStart('@')),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>
    /// Parses a parameter type inside an sp_executesql parameter-declaration
    /// string. Shape mirrors <see cref="ParseProcedureParameterType"/> but
    /// without the optional default expression.
    /// </summary>
    private static (SqlType Type, int? DeclaredMaxLength) ParseSpExecuteSqlParamType(ParserContext context, int ordinal)
    {
        var (qualifiedTypeName, typeName) = TypeNameSynonyms.ReadTypeName(context);
        context.MoveNextOptional();

        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    declaredScale = scaleValue.AsInt32;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        var (resolvedType, _, _) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: ordinal, TypeSpecSite.Scalar, columnName: null);
        return (resolvedType, declaredMaxLength);
    }

    /// <summary>
    /// Dispatches a string of SQL as a fresh child batch. The child batch
    /// shares the outer connection (so transaction / temp-table / catalog
    /// state are shared) but has its own variable scope — outer
    /// <c>@</c>-variables are invisible to the dynamic SQL.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> ExecuteDynamicBatch(
        BatchContext outerBatch,
        string sqlText,
        Dictionary<string, VariableSlot>? preDeclaredVariables)
    {
        var connection = outerBatch.Connection;
        using var dynCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // dynamic SQL is the application's input; the caller is responsible for sanitization
        dynCommand.CommandText = sqlText;
#pragma warning restore CA2100

        // Seed an empty variable dict (so outer @vars don't leak in) plus
        // any pre-declared sp_executesql parameters. Use the proc-body
        // BatchContext ctor with a sentinel ProcFrame so RETURN N parses
        // without raising Msg 178 — dynamic-SQL batches inherit the proc-
        // body's RETURN semantics (RETURN value is captured but unused).
        var variables = preDeclaredVariables is null
            ? new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer)
            : new Dictionary<string, VariableSlot>(preDeclaredVariables, BatchContext.VariableNameComparer);
        var procFrame = new ProcFrame("<dynamic-sql>", isDynamicSql: true);
        var innerBatch = new BatchContext(dynCommand, variables, procFrame) { ContinueOnError = ContinuesCalledBatch(outerBatch) };

        connection.NestingLevel++;
        var enteredDatabase = connection.CurrentDatabase;
        // SET NOCOUNT inside the dynamic batch binds for that batch only, the
        // same module scope USE and temp tables get (probe-confirmed for both
        // EXEC('…') and sp_executesql).
        var enteredNoCount = connection.NoCount;
        // XACT_ABORT / ROWCOUNT / DATEFIRST bind for the dynamic batch only,
        // the same module scope (probe-confirmed: `EXEC('SET XACT_ABORT ON …')`
        // leaves the caller's @@OPTIONS bit clear).
        var enteredOptions = new SimulatedDbConnection.SessionOptionScope(connection);
        List<SimulatedStatementOutcome> outcomes = [];
        SimulatedSqlException? batchError = null;
        var compiled = false;
        try
        {
            // Dynamic SQL is a batch of its own and compiles as one; an error
            // compiling it is the EXEC's own, and the caller carries on.
            if (this.CompileBatch(CompileContextFor(innerBatch, dynCommand), key: null) is { } compileError)
            {
                compileError.EndedCalledBatch = true;
                throw compileError;
            }
            compiled = true;

            // An error that ends the batch keeps what the batch sent before
            // it, which reaches the caller ahead of the error.
            var parser = innerBatch.Parser;
            parser.MoveNextOptional();
            foreach (var outcome in DispatchStatementsUntil(innerBatch, endKeyword: null))
                outcomes.Add(outcome);
        }
        catch (SimulatedSqlException ex) when (compiled)
        {
            batchError = ex;
        }
        finally
        {
            connection.NestingLevel--;
            // A USE inside the dynamic batch binds for that batch only — the
            // caller resumes on the database it was on (probe-confirmed for
            // both EXEC('…') and sp_executesql). This is what makes
            // sp_MSforeachdb's `USE [?]` idiom run each command against its
            // own database without leaving the session there.
            connection.CurrentDatabase = enteredDatabase;
            connection.NoCount = enteredNoCount;
            enteredOptions.Restore(connection);
            // A temp table created by the dynamic batch is dropped when it
            // returns (SQL Server's module-scoped lifetime — so re-running the
            // same `create table #t` through sp_executesql, as tedious does,
            // doesn't collide with Msg 2714).
            innerBatch.DropScopedTempTables();
        }

        // Copy any pre-declared variable's final value back to the caller's
        // slot (sp_executesql OUTPUT writeback path).
        if (batchError is null && preDeclaredVariables is not null)
        {
            foreach (var (name, _) in preDeclaredVariables)
                preDeclaredVariables[name] = variables[name];
        }

        // Bracket the dynamic batch's outcomes with proc-scope markers so the
        // TDS endpoint renders them with DONEINPROC and closes the scope with
        // RETURNSTATUS + DONEPROC — matching real SQL Server, which runs an
        // EXEC('…') / sp_executesql body as a nested procedure scope. In-process
        // consumers ignore the markers.
        yield return new SimulatedProcScopeBoundary(isEnter: true);
        foreach (var outcome in outcomes)
            yield return outcome;
        yield return new SimulatedProcScopeBoundary(isEnter: false);
        if (batchError is not null)
            ExceptionDispatchInfo.Throw(batchError);
    }

    /// <summary>
    /// One parameter declared in an sp_executesql parameter-declaration
    /// string. Distinct from <see cref="ProcedureParameter"/> because
    /// sp_executesql params have no defaults — every declared param must be
    /// bound by a positional/named arg.
    /// </summary>
    private readonly struct SpExecuteSqlParam(string name, SqlType type, int? declaredMaxLength, bool isOutput, SqlValue? defaultValue)
    {
        public readonly string Name = name;
        public readonly SqlType Type = type;

        /// <summary>The declared width a bound value is cut to, as a procedure parameter's is.</summary>
        public readonly int? DeclaredMaxLength = declaredMaxLength;
        public readonly bool IsOutput = isOutput;

        /// <summary>The declaration's constant default, which an unsupplied parameter takes in place of Msg 8178.</summary>
        public readonly SqlValue? Default = defaultValue;
    }
}
