using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes an inline TVF body and yields its row bytes. Allocates a
    /// child <see cref="BatchContext"/> wrapping a synthesized
    /// <see cref="SimulatedDbCommand"/> whose <c>CommandText</c> is the
    /// function's stored body. Parameters are seeded as typed variables in
    /// the child batch from <paramref name="arguments"/> (evaluated in the
    /// outer scope via <paramref name="outerResolver"/>). <c>DEFAULT</c>
    /// arg slots (null <see cref="Expression"/> entries) materialize from
    /// the parameter's stored default expression — same path scalar UDFs
    /// take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts toward <see cref="SimulatedDbConnection.NestingLevel"/>; a
    /// call that would exceed <see cref="SimulatedDbConnection.MaxNestingLevel"/>
    /// raises Msg 217 (same factory scalar UDFs use, since the cap is
    /// shared across "stored procedure, function, trigger, or view"
    /// recursion).
    /// </para>
    /// <para>
    /// The body is re-parsed on every call. The freshly parsed Selection
    /// captures the child batch's <see cref="VariableSlot"/> references, so
    /// per-call argument values flow through cleanly. The outer resolver
    /// passed to the body's Execute is null — TVF bodies in real SQL Server
    /// can't reach back into the caller's column scope; only the
    /// parameters carry outer values in.
    /// </para>
    /// </remarks>
    internal IEnumerable<byte[]> InvokeInlineTvf(
        BatchContext outerBatch,
        Func<MultiPartName, SqlValue>? outerResolver,
        InlineTableValuedFunction function,
        Expression?[] arguments,
        MultiPartName writtenName)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // Evaluate argument expressions in the caller's row scope. DEFAULT
        // slots stay flagged so the child batch evaluates the stored
        // default expression after seeding the rest of the parameters.
        var outerRuntime = new RuntimeContext(
            outerResolver ?? (name => throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString())),
            outerBatch);
        var (argValues, isDefault, tableArguments) = EvaluateFunctionArguments(function, arguments, outerRuntime);

        // The body binds and runs in the function's own database, one row at a
        // time, since the referencing statement consumes it lazily.
        var rows = InvokeInlineTvfCore(outerBatch, function, argValues, isDefault, tableArguments, writtenName);
        return ReferenceEquals(function.Schema.Database, connection.CurrentDatabase)
            ? rows
            : ModuleDatabaseScope.Enumerate(connection, function.Schema.Database, rows);
    }

    /// <summary>
    /// Evaluates a table-valued function call's arguments in the caller's
    /// scope: each scalar argument coerced to its parameter's type, a
    /// <c>DEFAULT</c> slot flagged for the child batch to fill from the stored
    /// default, and a table-valued parameter's argument resolved to the table
    /// it passes (null when there are no table-valued parameters).
    /// </summary>
    private static (SqlValue[] Values, bool[] IsDefault, HeapTable?[]? Tables) EvaluateFunctionArguments(
        UserDefinedFunction function, Expression?[] arguments, RuntimeContext outerRuntime)
    {
        var argCount = function.Parameters.Length;
        var argValues = new SqlValue[argCount];
        var isDefault = new bool[argCount];
        HeapTable?[]? tables = null;
        for (var i = 0; i < argCount; i++)
        {
            var parameter = function.Parameters[i];
            var argExpr = arguments[i];
            if (argExpr is null)
            {
                isDefault[i] = true;
                argValues[i] = SqlValue.Null(parameter.Type);
            }
            else if (parameter.TableType is not null)
            {
                argValues[i] = SqlValue.Null(parameter.Type);
                (tables ??= new HeapTable?[argCount])[i] = ((Parser.Expressions.TableValuedArgument)argExpr).Resolve(outerRuntime);
            }
            else
            {
                argValues[i] = argExpr.Run(outerRuntime).CoerceTo(parameter.Type);
            }
        }
        return (argValues, isDefault, tables);
    }

    private IEnumerable<byte[]> InvokeInlineTvfCore(
        BatchContext outerBatch,
        InlineTableValuedFunction function,
        SqlValue[] argValues,
        bool[] isDefault,
        HeapTable?[]? tableArguments,
        MultiPartName writtenName)
    {
        var connection = outerBatch.Connection;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // function.BodyText is the function's pre-validated stored body, not external input
        bodyCommand.CommandText = function.BodyText;
#pragma warning restore CA2100

        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        for (var i = 0; i < function.Parameters.Length; i++)
        {
            var param = function.Parameters[i];
            if (param.TableType is not null)
                continue;
            var value = isDefault[i] && param.Default is { } defaultExpr
                ? defaultExpr.Run(new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch))
                    .CoerceTo(param.Type)
                : argValues[i];
            // An argument past the parameter's width is cut to it, as a
            // variable assignment is (probed 2026-09-27 against SQL Server
            // 2025).
            value = Parser.Expressions.Cast.ApplyCoercion(value, param.Type, param.DeclaredMaxLength);
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: param.DeclaredMaxLength, value, parameter: null) { SpelledNumeric = param.SpelledNumeric };
        }

        // The UdfFrame here is a placeholder — inline TVF bodies don't use
        // value-form RETURN (the body is a single SELECT, not a
        // statement block ending in RETURN <expr>). Sharing the
        // batch constructor with scalar UDF invocation keeps the per-call
        // setup uniform across kinds.
        var dummyFrame = new UdfFrame(SqlType.Int32);
        // The body parses under the QUOTED_IDENTIFIER captured at CREATE, not
        // the caller's. Swapping the session flag (rather than seeding the
        // child parser) is what carries it to everything else that reads the
        // connection — dynamic SQL, the plan-cache key, the Msg 1934 gates.
        // Restored in the finally below; see docs/claude/grammar.md.
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        connection.QuotedIdentifiers = function.UsesQuotedIdentifier;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.AnsiNulls = function.UsesAnsiNulls;
        // Body errors attribute to the outer invoking statement (probe-
        // confirmed: real reports the referencing SELECT's line, no procedure).
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { SuppressDiagnosticsResolution = true, BindsModuleDefinition = true, ModuleSchema = function.Schema };
        SeedTableValuedParameters(innerBatch, outerBatch, function.Parameters, tableArguments);
        // Inlined into the referencing statement — same current-time freeze,
        // so a per-row APPLY reads one constant value (matching real).
        innerBatch.AdoptStatementFreezeFrom(outerBatch);
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            var bodySelection = ParseInlineTvfBody(parser, function, writtenName);
            // Inlined like a view body, so the same chain-breaks apply: reads
            // into another database, or of an object with another owner,
            // answer to the caller's rights.
            PermissionEnforcement.CheckModuleBodyReads(outerBatch, function, function.Schema.Database, bodySelection);
            var resultSet = bodySelection.Execute(innerBatch, outerResolver: null);
            foreach (var rowBytes in resultSet.RowBytes)
                yield return rowBytes;
        }
        finally
        {
            connection.NestingLevel--;
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            // As in the view body: the Sch-S / IS the body took are recorded
            // against this inner batch, which the dispatch loop never sees.
            innerBatch.ReleaseStatementSchemaLocks();
        }
    }

    /// <summary>
    /// A write naming a table-valued function call as its target: an inline
    /// function is the unstored view its body is, with this call's arguments,
    /// so a write passes through it to the table it reads as through a view
    /// — its filter selecting the rows, a derived column Msg 4406, a join Msg
    /// 4405 to a <c>DELETE</c> — and a multi-statement one is Msg 270 (probed
    /// 2026-10-04 against SQL Server 2025). Entered on the name's last token;
    /// on success left on the argument list's <c>)</c>.
    /// </summary>
    private static bool TryResolveFunctionWriteTarget(ParserContext context, MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        if (!context.Batch.TryResolveTableValuedFunction(name, out var function))
            return false;
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is not Operator { Character: '(' })
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }
        if (function is not InlineTableValuedFunction inline)
            throw SimulatedSqlException.ObjectCannotBeModified(name.ToString());
        context.MoveNextRequired();
        var arguments = Parser.Expressions.UserFunctionCall.ParseFunctionArguments(inline, context);
        var batch = context.Batch;
        var (argValues, isDefault, _) = EvaluateFunctionArguments(
            inline, arguments, new RuntimeContext(written => throw SimulatedSqlException.InvalidColumnName(written), batch));

        var connection = batch.Connection;
        using var bodyCommand = new SimulatedDbCommand(connection.Simulation, connection);
#pragma warning disable CA2100 // the function's pre-validated stored body, not external input
        bodyCommand.CommandText = inline.BodyText;
#pragma warning restore CA2100
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        for (var i = 0; i < inline.Parameters.Length; i++)
        {
            var param = inline.Parameters[i];
            if (param.TableType is not null)
                continue;
            var value = isDefault[i] && param.Default is { } defaultExpr
                ? defaultExpr.Run(new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), batch)).CoerceTo(param.Type)
                : argValues[i];
            value = Parser.Expressions.Cast.ApplyCoercion(value, param.Type, param.DeclaredMaxLength);
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: param.DeclaredMaxLength, value, parameter: null) { SpelledNumeric = param.SpelledNumeric };
        }
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.QuotedIdentifiers = inline.UsesQuotedIdentifier;
        connection.AnsiNulls = inline.UsesAnsiNulls;
        // The body's parameter references bind to this call's slots rather
        // than by name, so the write evaluates them from its own batch — the
        // binding a cursor's declaration gives the variables it reads.
        var innerBatch = new BatchContext(bodyCommand, variables, new UdfFrame(SqlType.Int32))
        {
            SuppressDiagnosticsResolution = true,
            BindsModuleDefinition = true,
            CursorDeclarationSnapshot = new Dictionary<string, VariableSlot>(variables, BatchContext.VariableNameComparer),
            ModuleSchema = inline.Schema,
        };
        innerBatch.AdoptStatementFreezeFrom(batch);
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            var body = ParseInlineTvfBody(parser, inline, name);
            view = UnstoredDmlView(body, inline.Schema.Database, name.ToString(), body.ColumnNames, isDerivedTable: false);
            return true;
        }
        finally
        {
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            innerBatch.ReleaseStatementSchemaLocks();
        }
    }

    /// <summary>
    /// Parses an inline function's body where it is called. A body that no
    /// longer binds — a missing object, column or qualifier — is that binder
    /// error, attributed to the function, followed by Msg 4413, as a view's is
    /// (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static Selection ParseInlineTvfBody(ParserContext parser, InlineTableValuedFunction function, MultiPartName writtenName)
    {
        try
        {
            return ParseBodyQuery(parser, position: QueryPosition.Inlined);
        }
        catch (SimulatedSqlException error) when (error.Number is 207 or 208 or 4104)
        {
            throw SimulatedSqlException.FollowedByViewBindingFailure(error, writtenName, function.Name);
        }
    }
}
