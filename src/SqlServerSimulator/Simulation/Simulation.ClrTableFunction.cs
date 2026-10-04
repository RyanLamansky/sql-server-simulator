using System.Collections;
using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses a CLR table-valued function's tail, <c>TABLE (cols) [ORDER
    /// (cols)] [WITH options] AS EXTERNAL NAME assembly.[class].method</c>,
    /// binds the init and <c>FillRow</c> methods, and stores the function.
    /// Cursor on entry: the <c>TABLE</c> keyword, with <c>(</c> after it.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-28 against SQL Server 2025. The result table streams, so
    /// before any binding it refuses what a streamed row can't carry: the ANSI
    /// string and legacy large-object types (Msg 6514 state 3), <c>IDENTITY</c>
    /// (6514 state 2), <c>timestamp</c> (6514 state 1), <c>NOT NULL</c> and
    /// <c>DEFAULT</c> (Msg 6526) and a key constraint (Msg 6525). Binding then
    /// runs as the scalar kind's does — Msg 6550 / 6551 / 6552, the return type
    /// being <see cref="IEnumerable"/> or <see cref="IEnumerator"/> — and goes
    /// on to the <c>FillRow</c> method the init method's
    /// <c>SqlFunction(FillRowMethodName = …)</c> names: Msg 10306 when the
    /// attribute names none, 6506 when the class has no such method, 6208 when
    /// it doesn't take one parameter more than the table has columns, and
    /// 6258 naming the first column whose <c>out</c> parameter doesn't bind to
    /// its type. The <c>ORDER</c> clause is accepted and has no effect;
    /// <c>SCHEMABINDING</c> is Msg 487.
    /// </remarks>
    private static bool ParseClrTableFunctionTail(
        ParserContext context,
        Schema schema,
        MultiPartName functionName,
        List<UdfParameter> parameters,
        List<SimulatedSqlException> declarationErrors,
        bool isAlter,
        bool createOrAlter)
    {
        var hasResolvedColumns = TryParseTableVariableColumnsAndConstraints(
            context, functionName.Leaf, out var outputColumns, out var keyConstraints, out var checkConstraints);

        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
            SkipOrderClause(context);

        var options = ParseModuleOptions(context, ModuleOptionHost.TableFunction, functionName.Leaf);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.As } || context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.External })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var externalName = ParseExternalName(context, 3);

        if (context.Batch.IsSkipping || !hasResolvedColumns)
            return true;

        if (options.SchemaBinding)
            throw SimulatedSqlException.InvalidOptionForCreateStatement("FUNCTION", 1);
        RejectStreamingResultShape(outputColumns, keyConstraints, checkConstraints);

        CheckModuleDdlPermission(
            context, "CREATE FUNCTION", functionName, schema, isAlter, createOrAlter,
            schema.Functions.GetValueOrDefault(functionName.Leaf));
        var replaced = ResolveClrFunctionAlterTarget<ClrTableValuedFunction>(context, schema, functionName, isAlter, createOrAlter);

        var (assembly, type) = ResolveClrClass(context, externalName[0], externalName[1]);
        var method = ResolveClrMethod(type, externalName[2], externalName[1], assembly.Name);
        if (HeldDeclarationErrors(declarationErrors) is { } held)
            throw held;
        var methodParameters = method.GetParameters();
        if (methodParameters.Length != parameters.Count)
            throw SimulatedSqlException.ClrParameterCountMismatch("CREATE FUNCTION");
        if (method.ReturnType != typeof(IEnumerable) && method.ReturnType != typeof(IEnumerator))
            throw SimulatedSqlException.ClrReturnTypeMismatch("CREATE FUNCTION", functionName.Leaf);
        for (var i = 0; i < methodParameters.Length; i++)
        {
            if (!ClrTypeMarshaller.Matches(parameters[i].Type, methodParameters[i].ParameterType))
                throw SimulatedSqlException.ClrParameterTypeMismatch("CREATE FUNCTION", functionName.Leaf, "@" + parameters[i].Name);
        }

        var fillRowName = ClrAttributes.NamedString(method, ClrAttributes.SqlFunction, "FillRowMethodName")
            ?? throw SimulatedSqlException.ClrTvfMissingFillRow();
        var fillRow = ClrAttributes.FindStaticMethod(type, fillRowName)
            ?? throw SimulatedSqlException.ClrMethodNotFound(fillRowName, externalName[1], assembly.Name);
        var fillParameters = fillRow.GetParameters();
        if (fillParameters.Length != outputColumns.Length + 1)
            throw SimulatedSqlException.ClrFillRowParameterCount();
        for (var i = 0; i < outputColumns.Length; i++)
        {
            var clrType = fillParameters[i + 1].ParameterType;
            if (!clrType.IsByRef || !ClrTypeMarshaller.Matches(outputColumns[i].Type, clrType.GetElementType()!))
                throw SimulatedSqlException.ClrFillRowColumnMismatch(functionName.Leaf, i + 1);
        }

        var function = new ClrTableValuedFunction(
            schema,
            functionName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            outputColumns,
            new ClrEntryPoint(assembly, externalName[1], externalName[2], type, method),
            fillRow,
            replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            ExecuteAsClause = options.ExecuteAs,
            ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, options.ExecuteAs),
        };
        if (replaced is not null)
            function.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        schema.Functions[functionName.Leaf] = function;
        RecordSlotUndo(context, schema.Functions, functionName.Leaf, replaced);
        if (replaced is not null)
            RebindExtendedProperties(context.Batch, replaced, function);
        RecordDdlEvent(context, replaced is null ? "CREATE_FUNCTION" : "ALTER_FUNCTION", schema.Name, functionName.Leaf, "FUNCTION");
        return true;
    }

    /// <summary>
    /// <see cref="ResolveFunctionAlterTarget"/> for a CLR function, which
    /// first refuses to replace a T-SQL function with Msg 6530 (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    private static T? ResolveClrFunctionAlterTarget<T>(
        ParserContext context, Schema schema, MultiPartName functionName, bool isAlter, bool createOrAlter)
        where T : ClrFunction
    {
        if ((isAlter || createOrAlter) && schema.Functions.GetValueOrDefault(functionName.Leaf) is { } and not ClrFunction)
            throw SimulatedSqlException.ClrAlterIncompatible(functionName.Leaf);
        return ResolveFunctionAlterTarget<T>(context, schema, functionName, isAlter, createOrAlter);
    }

    /// <summary>
    /// Skips a CLR table-valued function's <c>ORDER (col [ASC | DESC], …)</c>
    /// clause, which promises the rows' order to the optimizer and changes
    /// nothing a query observes. Cursor on entry: <c>ORDER</c>; on exit, the
    /// token after the closing <c>)</c>.
    /// </summary>
    private static void SkipOrderClause(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        SkipBalancedParens(context);
        context.MoveNextRequired();
    }

    /// <summary>
    /// The result-table shapes a streaming (CLR) table-valued function can't
    /// return, in column order.
    /// </summary>
    private static void RejectStreamingResultShape(HeapColumn[] columns, KeyConstraint[] keyConstraints, CheckConstraint[] checkConstraints)
    {
        // A key goes first: its column's implied NOT NULL isn't what real
        // names (probed 2026-09-28 against SQL Server 2025).
        if (keyConstraints.Length > 0)
            throw SimulatedSqlException.ClrTvfTableConstraintRefused(keyConstraints[0].Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE");
        if (checkConstraints.Length > 0)
        {
            throw checkConstraints[0].InlineColumn is { } checkedColumn
                ? SimulatedSqlException.ClrTvfColumnConstraintRefused("CHECK", checkedColumn)
                : SimulatedSqlException.ClrTvfTableConstraintRefused("CHECK");
        }

        foreach (var column in columns)
        {
            var refusedKind = column.Type switch
            {
                VarcharSqlType or CharSqlType or TextSqlType or NTextSqlType or ImageSqlType => column.Type.SqlServerName,
                _ => null,
            };
            if (refusedKind is not null)
                throw SimulatedSqlException.ClrTvfColumnKindRefused(refusedKind, column.Name, 3);
            if (column.Type is RowVersionSqlType)
                throw SimulatedSqlException.ClrTvfColumnKindRefused("TIMESTAMP", column.Name, 1);
            if (column.Identity is not null)
                throw SimulatedSqlException.ClrTvfColumnKindRefused("IDENTITY", column.Name, 2);
            if (!column.Nullable)
                throw SimulatedSqlException.ClrTvfColumnConstraintRefused("NOT NULL", column.Name);
            if (column.DefaultConstraint is not null)
                throw SimulatedSqlException.ClrTvfColumnConstraintRefused("DEFAULT", column.Name);
        }
    }

    /// <summary>
    /// Runs a CLR table-valued function for one call: evaluates the arguments
    /// in the caller's scope, calls the init method for the row objects, and
    /// yields each as the encoded row its <c>FillRow</c> method splits it
    /// into. A throw from the init method is Msg 6522 at the function's
    /// <see cref="ClrFunction.ThrowState"/> naming it, and only the init
    /// method of a function marked to read data opens the context connection; a throw from <c>FillRow</c>, or an <c>nvarchar(n)</c> value
    /// longer than <c>n</c>, is Msg 6260 (probed 2026-09-28 against SQL
    /// Server 2025). A <see langword="null"/> collection is no rows.
    /// </summary>
    internal static IEnumerable<byte[]> InvokeClrTableFunction(
        BatchContext outerBatch,
        Func<MultiPartName, SqlValue>? outerResolver,
        ClrTableValuedFunction function,
        Expression?[] arguments)
    {
        if (!outerBatch.Connection.Simulation.EnableClr)
            throw SimulatedSqlException.ClrExecutionDisabled();

        var outerRuntime = new RuntimeContext(
            outerResolver ?? (name => throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString())),
            outerBatch);
        var method = function.Entry.Method!;
        var clrParameters = method.GetParameters();
        var values = new object?[clrParameters.Length];
        for (var i = 0; i < clrParameters.Length; i++)
        {
            var parameter = function.Parameters[i];
            var value = arguments[i] is { } argument
                ? argument.Run(outerRuntime)
                : parameter.Default is { } defaultExpression
                    ? defaultExpression.Run(new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch))
                    : SqlValue.Null(parameter.Type);
            values[i] = ClrTypeMarshaller.ToClr(
                Parser.Expressions.Cast.ApplyCoercion(value.CoerceTo(parameter.Type), parameter.Type, parameter.DeclaredMaxLength),
                clrParameters[i].ParameterType);
        }

        var usesContext = function.Entry.Assembly.UsesServerContext;
        var contextConnection = usesContext && function.ReadsData
            ? new ClrContextConnection(outerBatch, function.Name, function.Entry.Assembly, pipe: null, triggerFrame: null, isFunction: true, restrictsUserData: !function.DataAccess.User)
            : null;
        object? rows = null;
        string? report = null;
        SimulatedSqlException? ending;
        try
        {
            using (CultureScope.Clr())
            using (usesContext ? ClrHost.Enter(pipe: null, contextConnection: contextConnection) : default(ClrHost.RoutineScope?))
                rows = method.Invoke(null, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            report = ClrExceptionReport.Describe(ex.InnerException, method);
        }
        finally
        {
            ending = contextConnection?.Leave(report);
        }

        if (report is not null)
            throw ending ?? SimulatedSqlException.ClrRoutineThrew(function.Name, report, function.ThrowState);
        if (ending is not null)
            throw ending;

        return EnumerateClrTableRows(function, rows switch
        {
            IEnumerable enumerable => enumerable.GetEnumerator(),
            IEnumerator enumerator => enumerator,
            _ => null,
        }, usesContext);
    }

    private static IEnumerable<byte[]> EnumerateClrTableRows(ClrTableValuedFunction function, IEnumerator? rows, bool usesContext)
    {
        if (rows is null)
            yield break;

        var columns = function.OutputColumns;
        var fillArguments = new object?[columns.Length + 1];
        var row = new SqlValue[columns.Length];
        while (true)
        {
            bool advanced;
            try
            {
                using (CultureScope.Clr())
                using (usesContext ? ClrHost.Enter(pipe: null) : default(ClrHost.RoutineScope?))
                    advanced = rows.MoveNext();
            }
            catch (Exception ex) when (ex is not SimulatedSqlException)
            {
                throw SimulatedSqlException.ClrFillRowThrew(ClrExceptionReport.Describe(ex, invoked: null));
            }

            if (!advanced)
                yield break;

            Array.Clear(fillArguments);
            try
            {
                using (CultureScope.Clr())
                using (usesContext ? ClrHost.Enter(pipe: null) : default(ClrHost.RoutineScope?))
                {
                    fillArguments[0] = rows.Current;
                    _ = function.FillRow.Invoke(null, fillArguments);
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw SimulatedSqlException.ClrFillRowThrew(ClrExceptionReport.Describe(ex.InnerException, function.FillRow));
            }

            for (var i = 0; i < columns.Length; i++)
            {
                var value = ClrTypeMarshaller.FromClr(fillArguments[i + 1], columns[i].Type);
                if (ClrTypeMarshaller.OverflowedWidth(value, columns[i].Type) is { } width)
                    throw SimulatedSqlException.ClrFillRowThrew(ClrExceptionReport.Truncation(value.AsString.Length, width));
                row[i] = value;
            }

            yield return RowEncoder.EncodeRow(columns, row);
        }
    }
}
