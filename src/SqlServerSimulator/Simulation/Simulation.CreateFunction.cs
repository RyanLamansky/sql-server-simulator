using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [OR ALTER] FUNCTION schema.name (@p1 type [= default], ...)</c>
    /// — and, via <paramref name="isAlter"/>, the identically-shaped
    /// <c>ALTER FUNCTION</c> — followed by either:
    /// <list type="bullet">
    /// <item><c>RETURNS &lt;scalar-type&gt; [WITH RETURNS NULL ON NULL INPUT]
    /// [AS] BEGIN ... END</c> — scalar UDF, stored as a
    /// <see cref="ScalarFunction"/>.</item>
    /// <item><c>RETURNS TABLE [WITH SCHEMABINDING | ENCRYPTION] [AS] RETURN
    /// [(] &lt;SELECT&gt; [)]</c> — inline table-valued function, stored as
    /// an <see cref="InlineTableValuedFunction"/>.</item>
    /// </list>
    /// The <c>AS</c> introducing the body is optional for every function kind
    /// — see <see cref="ConsumeOptionalBodyAs"/>.
    /// Both kinds land in the target <see cref="Schema.Functions"/> dict
    /// keyed by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Body capture (scalar)</strong>: the source span between the
    /// outer <c>BEGIN</c> (exclusive) and matching <c>END</c> (exclusive) is
    /// recorded as a raw <see cref="string"/> and re-tokenized per call.
    /// Nesting is tracked at the token level — each <c>BEGIN</c> (not followed
    /// by <c>TRAN</c>/<c>TRANSACTION</c>/<c>DISTRIBUTED</c>) increments depth,
    /// each <c>END</c> decrements.
    /// </para>
    /// <para>
    /// <strong>Body capture (inline TVF)</strong>: the SELECT statement
    /// between <c>AS RETURN [(</c> and the trailing <c>)]</c> is recorded as
    /// a raw <see cref="string"/>. Parens are optional in source; the
    /// capture stops at the closing <c>)</c> if one was opened, otherwise at
    /// end-of-batch or the next statement boundary. The body is parsed once
    /// at CREATE time (with parameters seeded as typed variables in a
    /// throwaway child <see cref="BatchContext"/>) to derive the output
    /// column schema; it's re-parsed per call when invoked from a FROM
    /// clause.
    /// </para>
    /// <para>
    /// <strong>Fidelity gaps</strong>: real SQL Server schema-binds inline
    /// TVFs and rejects DROP TABLE of any table the body references; the
    /// simulator records <c>WITH SCHEMABINDING</c> on
    /// <see cref="UserDefinedFunction.IsSchemaBound"/> but doesn't track the
    /// dependency, so a DROP of a referenced table succeeds and the TVF
    /// later fails at call time when re-resolving.
    /// </para>
    /// <para>
    /// <strong>ALTER</strong>: the replacement keeps the function's
    /// <see cref="SchemaObject.ObjectId"/>, <see cref="SchemaObject.CreateDate"/>
    /// and granted permissions, and advances
    /// <see cref="SchemaObject.ModifyDate"/>. The function's <em>kind</em> is
    /// fixed at creation — an ALTER body that writes a different one (scalar ↔
    /// inline TVF ↔ multi-statement TVF) raises <strong>Msg 2010</strong>, the
    /// same error a name held by an unrelated object kind gets. Bare
    /// <c>ALTER FUNCTION</c> on a name nothing holds raises
    /// <strong>Msg 208</strong>.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateFunction(ParserContext context, bool isAlter, bool createOrAlter)
    {
        // CREATE OR ALTER reports under the plain CREATE label (probe-confirmed
        // — real names the statement by the verb it started with).
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
            throw SimulatedSqlException.MustBeFirstStatementInBatch(isAlter ? "ALTER FUNCTION" : "CREATE FUNCTION");

        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var functionName = BatchContext.ParseObjectName(context);
        // Every error from here on names the function as its Procedure, as the
        // statement wrote it (probed 2026-09-25 against SQL Server 2025; see
        // the matching note in TryParseCreateView).
        context.Batch.ErrorProcedureName = functionName.Leaf;
        RejectQualifiedModuleName(functionName, "FUNCTION");
        var schema = ResolveModuleSchema(context, functionName, isAlter);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var parameters = new List<UdfParameter>();
        var declarationErrors = new List<SimulatedSqlException>();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            while (true)
            {
                var parameter = ParseParameter(context, parameters.Count + 1, declarationErrors);
                if (parameters.Exists(declared => BatchContext.VariableNameComparer.Equals(declared.Name, parameter.Name)))
                    throw SimulatedSqlException.VariableAlreadyDeclared(parameter.Name);
                parameters.Add(parameter);
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
        }

        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Returns })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        // RETURNS @r TABLE (...) → multi-statement TVF; RETURNS TABLE → inline
        // TVF; otherwise the existing scalar path.
        return context.Token switch
        {
            AtPrefixedString => ParseMultiStatementTvfTail(context, schema, functionName, parameters, declarationErrors, isAlter, createOrAlter),
            ReservedKeyword { Keyword: Keyword.Table } when NextIsOpenParen(context) => ParseClrTableFunctionTail(context, schema, functionName, parameters, declarationErrors, isAlter, createOrAlter),
            ReservedKeyword { Keyword: Keyword.Table } => ParseInlineTvfTail(context, schema, functionName, parameters, declarationErrors, isAlter, createOrAlter),
            _ => ParseScalarTail(context, schema, functionName, parameters, declarationErrors, isAlter, createOrAlter),
        };
    }

    /// <summary>
    /// Whether the token after the cursor is <c>(</c> — which after
    /// <c>RETURNS TABLE</c> opens a CLR function's result-table declaration.
    /// Leaves the cursor where it was.
    /// </summary>
    private static bool NextIsOpenParen(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next is Operator { Character: '(' };
    }

    /// <summary>
    /// Runs the shared CREATE / ALTER / CREATE OR ALTER existence rules for a
    /// function tail, narrowing the stored object to <typeparamref name="T"/>
    /// first: a function of any <em>other</em> kind reaches
    /// <see cref="ResolveModuleAlterTarget"/> as absent, which is what turns a
    /// kind-changing ALTER into Msg 2010.
    /// </summary>
    private static T? ResolveFunctionAlterTarget<T>(
        ParserContext context, Schema schema, MultiPartName functionName, bool isAlter, bool createOrAlter)
        where T : UserDefinedFunction =>
        (T?)ResolveModuleAlterTarget(
            context, schema, functionName, isAlter, createOrAlter,
            schema.Functions.TryGetValue(functionName.Leaf, out var existing) ? existing as T : null);

    /// <summary>
    /// Parses the multi-statement TVF tail: <c>@r TABLE (cols) [WITH option ...]
    /// AS BEGIN ... END</c>. Cursor on entry: the <c>@variable</c> token after
    /// <c>RETURNS</c>. The body's contents may freely <c>INSERT</c> into
    /// <c>@r</c> (registered as a table variable in the per-call child batch),
    /// and bare <c>RETURN;</c> projects the accumulated rows. Value-form
    /// <c>RETURN N</c> in the body raises Msg 178 at invoke time (probe-
    /// confirmed against real SQL Server, which surfaces this at CREATE time;
    /// the simulator defers to runtime — same convention scalar UDFs use).
    /// </summary>
    /// <remarks>
    /// Column-list parsing reuses
    /// <see cref="TryParseTableVariableColumnsAndConstraints"/> so the
    /// <c>RETURNS @r TABLE</c> grammar accepts the same column features as
    /// <c>DECLARE @t TABLE</c> — typed columns, IDENTITY, computed columns,
    /// inline / table-level CHECK, PRIMARY KEY / UNIQUE. Named constraints
    /// (<c>CONSTRAINT pk PRIMARY KEY</c>) and FOREIGN KEY remain rejected
    /// here too (Msg 102, inherited from the column-list parser's
    /// <c>isTableVariable: true</c> branch).
    /// </remarks>
    /// <summary>
    /// Draws again, from <paramref name="ownerName"/>, the auto-names the
    /// shared column-list parser drew from <paramref name="tableName"/>, for
    /// the two tables real names otherwise than their errors do: a return
    /// table's unnamed constraints are named after the function —
    /// <c>DF__mfz__d__…</c>, <c>PK__mfz__…</c> — though its errors name the
    /// table <c>@r</c> (probed 2026-09-26 against SQL Server 2025), and a
    /// table variable's after its <c>#</c>-and-hex name inside <c>tempdb</c>
    /// (probed 2026-09-28), whose nine characters a CHECK and a DEFAULT keep
    /// whole (<paramref name="tempNamePadding"/>).
    /// </summary>
    private static void RenameAutoNamedConstraints(
        string ownerName, string tableName, HeapColumn[] columns, KeyConstraint[] keyConstraints, CheckConstraint[] checkConstraints,
        int tempNamePadding = 16)
    {
        foreach (var column in columns)
        {
            if (column.DefaultConstraint is { IsSystemNamed: true } defaultConstraint)
                defaultConstraint.Name = AutoDefaultName(ownerName, column.Name, tempNamePadding);
        }
        foreach (var key in keyConstraints)
        {
            if (key.Name == AutoConstraintName(tableName, key.Kind, key.FullOrdinals, columns))
                key.Name = AutoConstraintName(ownerName, key.Kind, key.FullOrdinals, columns);
        }
        for (var i = 0; i < checkConstraints.Length; i++)
        {
            if (checkConstraints[i].IsSystemNamed)
                checkConstraints[i].Name = AutoCheckName(ownerName, checkConstraints[i].InlineColumn, i, tempNamePadding);
        }
    }

    private static bool ParseMultiStatementTvfTail(ParserContext context, Schema schema, MultiPartName functionName, List<UdfParameter> parameters, List<SimulatedSqlException> declarationErrors, bool isAlter, bool createOrAlter)
    {
        var returnVariableName = ((AtPrefixedString)context.Token!).Value;
        var returnVariableLine = context.Token.LineNumber;
        context.MoveNextRequired(); // consume @r

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Table })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Note: even in skip mode, the column list must be tokenized so the
        // cursor advances past the closing `)`. The helper returns false in
        // skip mode AFTER consuming the column list — the body still needs
        // to be captured below.
        var returnTableIndexes = new List<PendingInlineIndex>();
        var hasResolvedColumns = TryParseTableVariableColumnsAndConstraints(
            context,
            "@" + returnVariableName,
            out var outputColumns,
            out var keyConstraints,
            out var checkConstraints,
            returnTableIndexes);
        if (returnTableIndexes.Count > 0)
            throw new NotSupportedException("An inline INDEX on a multi-statement function's return table isn't modeled.");
        RenameAutoNamedConstraints(functionName.Leaf, "@" + returnVariableName, outputColumns, keyConstraints, checkConstraints);

        // Optional WITH-clause (SCHEMABINDING is captured for
        // sys.sql_modules / OBJECTPROPERTY; ENCRYPTION parse-and-discards).
        var options = ParseModuleOptions(context, ModuleOptionHost.TableFunction, functionName.Leaf);
        var isSchemaBound = options.SchemaBinding;

        _ = ConsumeOptionalBodyAs(context);

        // BEGIN/END required for MS-TVF bodies (same shape as scalar UDF).
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var commandText = context.Command.CommandText;
        context.MoveNextRequired(); // step past BEGIN
        // A function can't be natively compiled here, so its body can't open
        // with BEGIN ATOMIC (probed 2026-09-25 against SQL Server 2025).
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Atomic })
            throw SimulatedSqlException.BeginAtomicOutsideNativeModule();
        var bodyStart = context.Token.StartIndex;
        var depth = 1;
        var caseDepth = 0;
        while (depth > 0)
        {
            if (context.Token is null)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Begin }:
                    {
                        var checkpoint = context.SaveCheckpoint();
                        context.MoveNextRequired();
                        var isTransactionStart = context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed };
                        context.RestoreCheckpoint(checkpoint);
                        if (!isTransactionStart)
                            depth++;
                        break;
                    }
                case ReservedKeyword { Keyword: Keyword.Case }:
                    caseDepth++;
                    break;
                case ReservedKeyword { Keyword: Keyword.End }:
                    if (caseDepth > 0)
                    {
                        caseDepth--;
                        break;
                    }
                    depth--;
                    if (depth == 0)
                        goto bodyCaptured;
                    break;
            }
            context.MoveNextRequired();
        }
    bodyCaptured:
        var bodyEnd = context.Token.StartIndex;
        var endLine = context.Token.LineNumber;
        var bodyText = commandText[bodyStart..bodyEnd];
        context.MoveNextOptional(); // consume END

        // A function body runs to the end of its batch — anything past the
        // closing END is a syntax error at that token, before the function is
        // created.
        RejectStatementAfterModuleBody(context, functionName.Leaf);

        if (context.Batch.IsSkipping || !hasResolvedColumns)
            return true;

        // DDL gate: db-scope CREATE FUNCTION + schema ALTER when the statement
        // creates (Msg 262 state 18 with the function as Procedure attribution,
        // else Msg 2760), object ALTER when it replaces an existing function
        // (Msg 3701 state 20).
        CheckModuleDdlPermission(
            context, "CREATE FUNCTION", functionName, schema, isAlter, createOrAlter,
            schema.Functions.GetValueOrDefault(functionName.Leaf));

        if (isSchemaBound)
            SchemaBinding.EnforceNoAliasTypes(context.Batch, parameters, returnsAliasScalar: false, outputColumns, bodyText, CountNewlines(commandText, 0, bodyStart), endLine);

        // Bind the body before the schema dict is touched — see
        // BindModuleBodyAtCreate. Value-form RETURN raises Msg 178 from here,
        // which is where real reports it too.
        int? timestampColumnLine = Array.Exists(outputColumns, static column => column.Type == SqlType.RowVersion) ? returnVariableLine : null;
        BindBehindDeclarationErrors(HeldDeclarationErrors(declarationErrors), () => context.Simulation.BindMultiStatementTvfBodyAtCreate(
            context, functionName.Leaf, parameters, returnVariableName, outputColumns,
            keyConstraints, checkConstraints, bodyText,
            CountNewlines(commandText, 0, bodyStart), timestampColumnLine, commandText[bodyEnd..(bodyEnd + 3)]));

        var replaced = ResolveFunctionAlterTarget<MultiStatementTableValuedFunction>(context, schema, functionName, isAlter, createOrAlter);
        RejectTimestampParameters(parameters);

        if (isSchemaBound)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "function", $"{schema.Name}.{functionName.Leaf}", bodyText);

        var function = new MultiStatementTableValuedFunction(
            schema,
            functionName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            returnVariableName,
            outputColumns,
            keyConstraints,
            checkConstraints,
            bodyText,
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            DefinitionText = options.Encryption ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            IsSchemaBound = isSchemaBound,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
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
    /// Parses the scalar UDF tail: a scalar return type, optional
    /// <c>WITH RETURNS NULL ON NULL INPUT</c>, then <c>AS BEGIN ... END</c>
    /// with the body source captured for per-call re-tokenization. Cursor on
    /// entry: the type-name token (right after the <c>RETURNS</c> keyword
    /// the outer parser already advanced past).
    /// </summary>
    private static bool ParseScalarTail(ParserContext context, Schema schema, MultiPartName functionName, List<UdfParameter> parameters, List<SimulatedSqlException> declarationErrors, bool isAlter, bool createOrAlter)
    {
        var returnSpelledNumeric = IsNumericTypeWord(context.Token);
        var returnType = ParseFunctionReturnType(context, ordinal: 0, parameterName: "", out var returnMaxLength, out var returnAliasType);
        returnSpelledNumeric = returnAliasType?.SpelledNumeric ?? returnSpelledNumeric;

        // Optional WITH clause. RETURNS NULL ON NULL INPUT is the only option
        // that affects runtime semantics (NULL-propagation skips the body);
        // SCHEMABINDING records on the function for the catalog surfaces
        // without being enforced.
        var options = ParseModuleOptions(context, ModuleOptionHost.ScalarFunction, functionName.Leaf);
        if (options.NativeCompilation)
            throw new NotSupportedException("A natively compiled scalar function isn't modeled.");
        var returnsNullOnNullInput = options.ReturnsNullOnNullInput;
        var isSchemaBound = options.SchemaBinding;
        var executeAsClause = options.ExecuteAs;

        // AS EXTERNAL NAME assembly.[class].method → the body lives in a
        // registered CLR assembly rather than in T-SQL. The CLR form needs the
        // AS, since EXTERNAL only follows it.
        var sawAs = ConsumeOptionalBodyAs(context);
        if (sawAs && context.Token is ReservedKeyword { Keyword: Keyword.External })
            return ParseClrScalarTail(context, schema, functionName, parameters, declarationErrors, returnType, isSchemaBound, isAlter, createOrAlter);

        // BEGIN/END required for scalar UDF bodies. Capture span between
        // outer BEGIN (exclusive) and matching END (exclusive) using token-
        // level nesting; BEGIN TRAN / TRANSACTION / DISTRIBUTED don't open a
        // body block.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var commandText = context.Command.CommandText;
        context.MoveNextRequired(); // step past BEGIN
        // A function can't be natively compiled here, so its body can't open
        // with BEGIN ATOMIC (probed 2026-09-25 against SQL Server 2025).
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Atomic })
            throw SimulatedSqlException.BeginAtomicOutsideNativeModule();
        var bodyStart = context.Token.StartIndex;
        var depth = 1;
        var caseDepth = 0;
        while (depth > 0)
        {
            if (context.Token is null)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Begin }:
                    {
                        var checkpoint = context.SaveCheckpoint();
                        context.MoveNextRequired();
                        var isTransactionStart = context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed };
                        context.RestoreCheckpoint(checkpoint);
                        if (!isTransactionStart)
                            depth++;
                        break;
                    }
                case ReservedKeyword { Keyword: Keyword.Case }:
                    caseDepth++;
                    break;
                case ReservedKeyword { Keyword: Keyword.End }:
                    if (caseDepth > 0)
                    {
                        caseDepth--;
                        break;
                    }
                    depth--;
                    if (depth == 0)
                        goto bodyCaptured;
                    break;
            }
            context.MoveNextRequired();
        }
    bodyCaptured:
        var bodyEnd = context.Token.StartIndex;
        var endLine = context.Token.LineNumber;
        var bodyText = commandText[bodyStart..bodyEnd];
        context.MoveNextOptional(); // consume END

        // A function body runs to the end of its batch — anything past the
        // closing END is a syntax error at that token, before the function is
        // created.
        RejectStatementAfterModuleBody(context, functionName.Leaf);

        if (context.Batch.IsSkipping)
            return true;

        // DDL gate: db-scope CREATE FUNCTION + schema ALTER when the statement
        // creates (Msg 262 state 18 with the function as Procedure attribution,
        // else Msg 2760), object ALTER when it replaces an existing function
        // (Msg 3701 state 20).
        CheckModuleDdlPermission(
            context, "CREATE FUNCTION", functionName, schema, isAlter, createOrAlter,
            schema.Functions.GetValueOrDefault(functionName.Leaf));

        if (isSchemaBound)
            SchemaBinding.EnforceNoAliasTypes(context.Batch, parameters, returnsAliasScalar: returnAliasType is not null, null, bodyText, CountNewlines(commandText, 0, bodyStart), endLine);

        // Bind the body before the schema dict is touched — see
        // BindModuleBodyAtCreate.
        BindBehindDeclarationErrors(HeldDeclarationErrors(declarationErrors, returnType, endLine), () => context.Simulation.BindScalarFunctionBodyAtCreate(
            context, functionName.Leaf, parameters, returnType, bodyText,
            CountNewlines(commandText, 0, bodyStart), commandText[bodyEnd..(bodyEnd + 3)]));

        // INLINE = ON over a body that can't inline is refused after the body
        // binds and ahead of the name check (probed 2026-10-01 against SQL
        // Server 2025).
        if (options.Inline == true && !ModuleInlining.IsInlineableScalar(bodyText, functionName.Leaf, executeAsClause))
            throw SimulatedSqlException.InlineOptionNotValid();
        var replaced = ResolveFunctionAlterTarget<ScalarFunction>(context, schema, functionName, isAlter, createOrAlter);
        RejectTimestampParameters(parameters);

        if (isSchemaBound)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "function", $"{schema.Name}.{functionName.Leaf}", bodyText);

        var function = new ScalarFunction(
            schema,
            functionName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            returnType,
            returnsNullOnNullInput,
            bodyText,
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            DefinitionText = options.Encryption ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            ExecuteAsClause = executeAsClause,
            ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, executeAsClause),
            IsSchemaBound = isSchemaBound,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            ReturnSpelledNumeric = returnSpelledNumeric,
            ReturnAliasType = returnAliasType,
            ReturnMaxLength = returnMaxLength,
            BodyLineOffset = CountNewlines(commandText, 0, bodyStart),
            InlineOption = options.Inline,
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
    /// Parses the inline-TVF tail. Cursor on entry: the <c>TABLE</c> reserved
    /// keyword (right after <c>RETURNS</c>). The grammar accepted:
    /// <code>
    /// TABLE [WITH option [, option ...]] AS RETURN [(] &lt;select&gt; [)]
    /// </code>
    /// where <c>option</c> is <c>SCHEMABINDING</c> (parse-and-ignore) or
    /// <c>ENCRYPTION</c> (parse-and-ignore). <c>RETURNS NULL ON NULL
    /// INPUT</c> in the <c>WITH</c> slot of a TVF raises Msg 487 (probe-
    /// confirmed — that option is scalar-only).
    /// </summary>
    /// <summary>
    /// Steps past the <c>AS</c> that introduces a function body, if it's
    /// there. Returns whether one was consumed; the cursor ends on the first
    /// body token either way.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>CREATE FUNCTION</c> grammar takes the keyword as
    /// optional for all three function kinds — scalar, inline table-valued and
    /// multi-statement table-valued — so <c>RETURNS nvarchar(4000) BEGIN … END</c>
    /// creates the same function as <c>RETURNS nvarchar(4000) AS BEGIN … END</c>
    /// (probe-confirmed against SQL Server 2025). Hand-written functions do
    /// omit it, so requiring the keyword drops them on import.
    /// <c>CREATE PROCEDURE</c> does <em>not</em> share the licence — omitting
    /// the keyword there is Msg 102 at the following token — so the procedure
    /// parser keeps requiring it.
    /// </remarks>
    private static bool ConsumeOptionalBodyAs(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            return false;
        context.MoveNextRequired();
        return true;
    }

    private static bool ParseInlineTvfTail(ParserContext context, Schema schema, MultiPartName functionName, List<UdfParameter> parameters, List<SimulatedSqlException> declarationErrors, bool isAlter, bool createOrAlter)
    {
        context.MoveNextRequired(); // step past TABLE

        // Optional WITH-clause: SCHEMABINDING is captured, ENCRYPTION
        // parse-and-discards. RETURNS NULL ON NULL INPUT here → Msg 487.
        var options = ParseModuleOptions(context, ModuleOptionHost.InlineFunction, functionName.Leaf);
        var isSchemaBound = options.SchemaBinding;

        _ = ConsumeOptionalBodyAs(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Return })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional `(` before the SELECT; if present, the matching `)` ends
        // the body. Otherwise the body extends to the end of the batch (or
        // the next statement-starting keyword). A body opening with a
        // parenthesized set-operation branch, `(SELECT …) UNION ALL (SELECT …)`,
        // takes the second form.
        context.MoveNextRequired();
        var commandText = context.Command.CommandText;
        var openedParen = Selection.CountWrappingParentheses(context) > 0;
        if (openedParen)
            context.MoveNextRequired();

        var bodyStart = context.Token?.StartIndex
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        var bodyEnd = CaptureInlineTvfBody(context, openedParen);
        var bodyText = commandText[bodyStart..bodyEnd];

        if (openedParen)
        {
            // CaptureInlineTvfBody leaves the cursor at the matching `)`.
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

        // An inline-TVF body runs to the end of its batch, in both the
        // parenthesized and the bare-RETURN form.
        RejectStatementAfterModuleBody(context, functionName.Leaf);

        if (context.Batch.IsSkipping)
            return true;

        // DDL gate: db-scope CREATE FUNCTION + schema ALTER when the statement
        // creates (Msg 262 state 18 with the function as Procedure attribution,
        // else Msg 2760), object ALTER when it replaces an existing function
        // (Msg 3701 state 20).
        CheckModuleDdlPermission(
            context, "CREATE FUNCTION", functionName, schema, isAlter, createOrAlter,
            schema.Functions.GetValueOrDefault(functionName.Leaf));

        if (isSchemaBound)
            SchemaBinding.EnforceNoAliasTypes(context.Batch, parameters, returnsAliasScalar: false, null, "", 0, 0);

        // The body binds ahead of the name check, as a multi-statement
        // function's does: real reports a body's own errors over an existing
        // function of that name (probed 2026-09-30 against SQL Server 2025).
        HeapColumn[] outputColumns = [];
        byte[]? outputWireFlags = null;
        BindBehindDeclarationErrors(HeldDeclarationErrors(declarationErrors), () => outputColumns = InferInlineTvfOutputColumns(context, [.. parameters], bodyText, functionName.Leaf, CountNewlines(commandText, 0, bodyStart), out outputWireFlags));

        var replaced = ResolveFunctionAlterTarget<InlineTableValuedFunction>(context, schema, functionName, isAlter, createOrAlter);

        if (isSchemaBound)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "function", $"{schema.Name}.{functionName.Leaf}", bodyText);

        RejectTimestampParameters(parameters);

        var function = new InlineTableValuedFunction(
            schema,
            functionName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            outputColumns,
            bodyText,
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            DefinitionText = options.Encryption ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            IsSchemaBound = isSchemaBound,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            OutputWireFlags = outputWireFlags,
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
    /// Scans forward from the inline-TVF body's first token to the closing
    /// <c>)</c> (when <paramref name="openedParen"/> is true) or the end of
    /// the batch / the next statement-starting reserved keyword. Returns the
    /// character index of the byte AFTER the last body token (i.e. the
    /// exclusive end of the body span in the command text). Cursor on exit:
    /// the closing <c>)</c> (when <paramref name="openedParen"/>) or the
    /// statement boundary.
    /// </summary>
    /// <remarks>
    /// The paren-less form ends at a statement keyword only at the body's own
    /// nesting level: a <c>SELECT</c> inside a derived table, a subquery, or a
    /// CTE definition belongs to the body and must not truncate the captured
    /// span. A body opening with <c>WITH</c> additionally spends one
    /// depth-0 statement keyword on the query the CTE prefix scopes to.
    /// </remarks>
    private static int CaptureInlineTvfBody(ParserContext context, bool openedParen)
    {
        // The first token is always the body's own start — the leading SELECT,
        // or the WITH of a CTE prefix. Consume it unconditionally so the
        // statement-boundary check doesn't bail on the token that opens the
        // body. After that, scan until the matching `)` (paren-form) or the
        // next statement-starting keyword (paren-less form).
        var awaitingCtePrefixedQuery = context.Token is ReservedKeyword { Keyword: Keyword.With };
        var depth = openedParen ? 1 : 0;
        var lastBodyEnd = context.Token!.EndIndex;
        var afterSetOperator = false;
        // A parenthesized first branch opens a level the loop below closes.
        if (context.Token is Operator { Character: '(' })
            depth++;
        context.MoveNextOptional();
        while (context.Token is not null)
        {
            // A SELECT after UNION [ALL] / EXCEPT / INTERSECT is the set
            // operation's next branch, not a new statement (probed 2026-09-26
            // against SQL Server 2025).
            var continuesSetOperation = afterSetOperator;
            afterSetOperator = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect } => true,
                ReservedKeyword { Keyword: Keyword.All } => afterSetOperator,
                _ => false,
            };
            switch (context.Token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    if (openedParen && depth == 1)
                        return lastBodyEnd;
                    depth--;
                    break;
                default:
                    if (openedParen || depth != 0 || continuesSetOperation || !IsStatementBoundary(context.Token))
                        break;
                    // The query a CTE prefix scopes to is the one statement
                    // keyword that continues the body instead of ending it; a
                    // `;` ends it either way.
                    if (!awaitingCtePrefixedQuery || context.Token is not ReservedKeyword)
                        return lastBodyEnd;
                    awaitingCtePrefixedQuery = false;
                    break;
            }
            lastBodyEnd = context.Token.EndIndex;
            context.MoveNextOptional();
        }
        return lastBodyEnd;
    }

    /// <summary>
    /// The columns a reference to <paramref name="function"/> reads, with each
    /// mask settled as the body binds now: a mask its base table gained or lost
    /// since <c>CREATE FUNCTION</c> reaches the reference (probed 2026-09-29
    /// against SQL Server 2025), as it does a view's. The recorded columns come
    /// back unchanged while no mask has been declared or when the body no
    /// longer binds.
    /// </summary>
    internal static HeapColumn[] InlineTvfColumnsWithCurrentMasks(ParserContext context, InlineTableValuedFunction function)
    {
        if (!context.Batch.Connection.Simulation.DeclaresDataMasks)
            return function.OutputColumns;
        HeapColumn[] fresh;
        try
        {
            fresh = InferInlineTvfOutputColumns(context, function.Parameters, function.BodyText, function.Name, 0, out _);
        }
        catch (Exception error) when (error is SimulatedSqlException or NotSupportedException)
        {
            return function.OutputColumns;
        }
        if (fresh.Length != function.OutputColumns.Length)
            return function.OutputColumns;
        var columns = new HeapColumn[fresh.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = ReferenceEquals(fresh[i].DerivedMask, function.OutputColumns[i].DerivedMask) ? function.OutputColumns[i] : function.OutputColumns[i].WithDerivedMask(fresh[i].DerivedMask);
        return columns;
    }

    /// <summary>
    /// Parses the inline-TVF body once at CREATE-FUNCTION time to derive its
    /// output column schema (column names + types). Allocates a synthetic
    /// child <see cref="BatchContext"/> with the function's declared
    /// parameters pre-seeded as typed variables so <c>@p</c> references
    /// resolve cleanly. Enforces Msg 4514 (unnamed projection column) and
    /// Msg 4506 (duplicate column name) before returning.
    /// </summary>
    /// <remarks>
    /// Per-column nullability comes from the body's own projection inference
    /// (<see cref="Selection.ColumnNullability"/>), the same rules the TDS
    /// COLMETADATA fNullable flag reports. That inference declines the joined
    /// and multi-source shapes, which keep the conservative
    /// <see langword="true"/>.
    /// </remarks>
    private static HeapColumn[] InferInlineTvfOutputColumns(
        ParserContext outerContext,
        UdfParameter[] parameters,
        string bodyText,
        string functionName,
        int bodyLineOffset,
        out byte[]? wireFlags)
    {
        // Synthesize a command + batch to parse the body in isolation. The
        // batch shares the outer connection so it sees the same schemas /
        // tables, but its own Variables dict pre-seeds the parameters with
        // NULL-of-declared-type so the parser doesn't trip Msg 137.
        var connection = outerContext.Batch.Connection;
        using var bodyCommand = new SimulatedDbCommand(connection.Simulation, connection);
#pragma warning disable CA2100 // bodyText is the simulator's own captured body span
        bodyCommand.CommandText = bodyText;
#pragma warning restore CA2100

        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            variables[p.Name] = new VariableSlot(p.Type, declaredMaxLength: null, SqlValue.Null(p.Type), parameter: null) { SpelledNumeric = p.SpelledNumeric };
        }

        // Use the scalar-UDF body batch constructor — it accepts a synthesized
        // command + a pre-seeded variable dict, which is exactly what we need
        // for one-shot parse-only inspection. We don't actually dispatch
        // anything here.
        var dummyFrame = new UdfFrame(SqlType.Int32);
        // The body binds at CREATE as any module body does — a parameter has
        // no value yet, so `TOP (@n)` is settled from its declared type rather
        // than refused as a NULL count (probed 2026-09-26).
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { CreateTimeBinding = true, LineOffset = bodyLineOffset };
        // Inspection runs the body's FROM-less projections, so the batch needs
        // the CREATE statement's own current-time freeze to evaluate a
        // GETDATE() / SYSDATETIME() column.
        innerBatch.AdoptStatementFreezeFrom(outerContext.Batch);
        try
        {
            var parser = innerBatch.Parser;
            parser.SchemaBoundBody = outerContext.SchemaBoundBody;
            parser.MoveNextRequired();

            Selection selection;
            try
            {
                selection = ReportingEveryBindError(innerBatch, bodyLineOffset, () => ParseBodyQuery(parser, rejectsNextValueFor: true));
            }
            catch (SimulatedSqlException parsePhase) when (IsRecoverableSyntaxError(parsePhase) && parser.Token is { } errorToken)
            {
                // Real's parser recovers past a syntax error in the body and
                // reads what follows as statements, reporting each error it
                // finds there. The body's query parsed outside any dispatch
                // frame, so its error takes the body's line here.
                parsePhase.ResolveDiagnostics(errorToken.LineNumber, bodyLineOffset, functionName);
                innerBatch.ErrorProcedureName = functionName;
                throw connection.Simulation.WithRecoveredSyntaxErrors(innerBatch, parsePhase, errorToken, command =>
                {
                    var recovery = new BatchContext(command, new Dictionary<string, VariableSlot>(variables, BatchContext.VariableNameComparer), new UdfFrame(SqlType.Int32));
                    recovery.AdoptStatementFreezeFrom(outerContext.Batch);
                    return recovery;
                });
            }

            // Msg 1033: an inline function is one of the five constructs the
            // message names, so its ORDER BY needs a companion TOP / OFFSET /
            // FETCH — the same test the view and CTE bodies run (probe-confirmed
            // 2026-08-06).
            if (selection.HasOrderBy && !selection.HasTopOrOffsetOrFetch)
                throw SimulatedSqlException.OrderByInvalidInCte();

            var columns = new HeapColumn[selection.Schema.Length];
            var seenNames = new HashSet<string>(outerContext.Batch.CurrentDatabase.Collation);
            var nullability = selection.ColumnNullability;
            for (var i = 0; i < selection.Schema.Length; i++)
            {
                var name = selection.ColumnNames[i];
                if (string.IsNullOrEmpty(name))
                    throw SimulatedSqlException.InlineTvfMissingColumnName(i + 1);
                if (!seenNames.Add(name))
                    throw SimulatedSqlException.DuplicateColumnInViewOrFunction(name, functionName);
                var nullable = nullability is null || i >= nullability.Length || nullability[i];
                // A numeric literal's column reports numeric, and an alias-typed
                // one its alias, as a view's column does (probed 2026-09-26).
                columns[i] = new HeapColumn(name, selection.Schema[i], maxLength: null, nullable: nullable,
                    spelledNumeric: selection.ColumnReportsNumeric is { } numeric && numeric[i])
                {
                    AliasType = selection.ColumnAliasTypes?[i],
                    // Settled as the body binds at CREATE: a mask the base
                    // table gains or loses later reaches the function's
                    // columns only when it is re-created or altered.
                    DerivedMask = selection.ColumnMasks?[i],
                };
            }
            wireFlags = selection.ColumnWireFlags;
            return columns;
        }
        finally
        {
            // Binding the body takes Sch-S / IS on everything it names, and
            // this batch never reaches the dispatch loop that would release
            // them — so it releases its own. In a finally because the
            // validation above throws: a CREATE that fails its own checks must
            // not leave the locks its partial parse already took.
            innerBatch.ReleaseStatementSchemaLocks();
        }
    }

    /// <summary>
    /// Parses one entry in a <c>CREATE FUNCTION</c> parameter list:
    /// <c>@name type [= default] [READONLY]</c>. Cursor on entry: the leading
    /// <c>@</c> or parameter name token. Cursor on exit: the trailing <c>,</c>
    /// or <c>)</c> separator (caller decides which). A type that doesn't
    /// resolve and a <c>READONLY</c> on it land on
    /// <paramref name="declarationErrors"/> instead of ending the parse — see
    /// <see cref="HeldDeclarationErrors"/>.
    /// </summary>
    private static UdfParameter ParseParameter(ParserContext context, int ordinal, List<SimulatedSqlException> declarationErrors)
    {
        if (context.Token is not AtPrefixedString variable)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = variable.Value;
        context.MoveNextRequired();

        var spelledNumeric = IsNumericTypeWord(context.Token);
        SqlType paramType;
        int? paramMaxLength;
        AliasType? aliasType;
        var typeResolved = true;
        try
        {
            paramType = ParseFunctionReturnType(context, ordinal, "@" + name, out paramMaxLength, out aliasType);
        }
        catch (SimulatedSqlException error) when (error.Number == 2715)
        {
            declarationErrors.Add(error);
            (paramType, paramMaxLength, aliasType) = (SqlType.Int32, null, null);
            typeResolved = false;
        }
        spelledNumeric = aliasType?.SpelledNumeric ?? spelledNumeric;

        Expression? defaultExpression = null;
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            defaultExpression = Expression.Parse(context);
            if (typeResolved)
                NoteUnassignableDefault(context.Batch, defaultExpression, paramType, declarationErrors);
        }
        _ = NoteReadOnlyScalarParameter(context, variable, declarationErrors);
        return new UdfParameter(name, paramType, defaultExpression) { SpelledNumeric = spelledNumeric, AliasType = aliasType, DeclaredMaxLength = paramMaxLength, LineNumber = variable.LineNumber };
    }

    /// <summary>
    /// Notes on <paramref name="declarationErrors"/> a parameter default that
    /// can't be assigned to the parameter's type — Msg 206, or Msg 257 for a
    /// conversion real makes only explicitly — which real refuses the module's
    /// <c>CREATE</c> or <c>ALTER</c> with ahead of anything else it would
    /// report (probed 2026-09-30 against SQL Server 2025, for every function
    /// kind and a procedure; see <see cref="HeldDeclarationErrors"/>).
    /// </summary>
    private static void NoteUnassignableDefault(BatchContext batch, Expression defaultExpression, SqlType parameterType, List<SimulatedSqlException> declarationErrors)
    {
        try
        {
            AssignmentRules.RequireAssignable(defaultExpression, defaultExpression.GetSqlType(batch, NoColumnTypeResolver), parameterType);
        }
        catch (SimulatedSqlException refusal) when (refusal.Number is 206 or 257)
        {
            declarationErrors.Add(refusal);
        }
        catch (SimulatedSqlException)
        {
            // A default that doesn't type at all raises where it is used.
        }
    }

    /// <summary>
    /// Steps past a <c>READONLY</c> after a parameter that isn't table-valued,
    /// noting real's Msg 346 at the parameter's line on
    /// <paramref name="declarationErrors"/>. Real reads the keyword after the
    /// default and before <c>OUTPUT</c>, so <c>READONLY OUTPUT</c> is a syntax
    /// error at the <c>OUTPUT</c> (probed 2026-09-30 against SQL Server 2025).
    /// Answers whether the keyword was there, which closes the declaration.
    /// </summary>
    private static bool NoteReadOnlyScalarParameter(ParserContext context, AtPrefixedString parameter, List<SimulatedSqlException> declarationErrors)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.ReadOnly })
            return false;
        declarationErrors.Add(SimulatedSqlException.ReadOnlyScalarParameter("@" + parameter.Value, parameter.LineNumber));
        context.MoveNextRequired();
        return true;
    }

    /// <summary>
    /// The errors a module's parameter list and return type raise, which real
    /// reports once the whole statement has parsed — so a syntax error in the
    /// body outranks them — and ahead of anything its body binds (probed
    /// 2026-09-30 against SQL Server 2025). A <c>timestamp</c> return type is
    /// Msg 2733 alone, at the line the statement ends on; otherwise every
    /// parameter's Msg 346 and Msg 2715 (with its Msg 2724 note), in parameter
    /// order. A parameter default that can't be assigned to its type
    /// (<see cref="NoteUnassignableDefault"/>) is reported alone, the first
    /// one, when no parameter's type is missing. Null when there are none.
    /// </summary>
    private static SimulatedSqlException? HeldDeclarationErrors(List<SimulatedSqlException> declarationErrors, SqlType? returnType = null, int endLine = 0)
    {
        var unassignableDefault = declarationErrors.Find(static held => held.Number is 206 or 257);
        if (unassignableDefault is not null && !declarationErrors.Exists(static held => held.Number == 2715))
            return unassignableDefault;
        _ = declarationErrors.RemoveAll(static held => held.Number is 206 or 257);
        if (returnType == SqlType.RowVersion)
        {
            var refusal = SimulatedSqlException.TimestampReturnTypeInvalid();
            refusal.Errors[0].LineNumber = endLine;
            return refusal;
        }
        return declarationErrors.Count == 0 ? null : SimulatedSqlException.Aggregate(declarationErrors);
    }

    /// <summary>
    /// Runs <paramref name="bind"/> — a module body's CREATE-time bind — behind
    /// <paramref name="held"/>: a parse-phase error the body raises (severity
    /// 15, a syntax error) is still reported, and anything the bind would
    /// report after it gives way to the held errors.
    /// </summary>
    private static void BindBehindDeclarationErrors(SimulatedSqlException? held, Action bind)
    {
        if (held is null)
        {
            bind();
            return;
        }
        try
        {
            bind();
        }
        catch (SimulatedSqlException error) when (error.Class != 15)
        {
        }
        catch (NotSupportedException)
        {
        }
        throw held;
    }

    /// <summary>
    /// Msg 2724 for the first function parameter declared <c>timestamp</c> /
    /// <c>rowversion</c> — raised once the body has bound, so a body's binder
    /// errors outrank it (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    private static void RejectTimestampParameters(List<UdfParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Type == SqlType.RowVersion)
                throw SimulatedSqlException.FunctionParameterInvalidType("@" + parameter.Name);
        }
    }

    /// <summary>
    /// Parses a <c>RETURNS &lt;type&gt;</c> or parameter type expression. The
    /// grammar is a subset of CREATE TABLE column types: a type name optionally
    /// followed by <c>(N)</c> / <c>(N, S)</c> / <c>(MAX)</c>. Cursor on entry:
    /// the type-name token. Cursor on exit: the first token past the type
    /// (e.g. <c>WITH</c>, <c>AS</c>, <c>=</c>, <c>,</c>, <c>)</c>).
    /// <paramref name="ordinal"/> is the parameter's position, 0 for the
    /// return type.
    /// </summary>
    private static SqlType ParseFunctionReturnType(ParserContext context, int ordinal, string parameterName, out int? maxLength, out AliasType? aliasType)
    {
        var (qualifiedTypeName, typeName) = TypeNameSynonyms.ReadTypeName(context);
        context.MoveNextRequired();

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
                    _ = context.GetNextRequired();
                    declaredScale = TypeNameSynonyms.ReadSecondTypeArgument(context, typeName);
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }

        (var resolvedType, maxLength, _, aliasType) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: ordinal, TypeSpecSite.Scalar, columnName: parameterName);
        return resolvedType;
    }
}
