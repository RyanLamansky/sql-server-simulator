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
        RejectQualifiedModuleName(functionName, "FUNCTION");
        var schema = ResolveModuleSchema(context, functionName, isAlter);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var parameters = new List<UdfParameter>();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            while (true)
            {
                parameters.Add(ParseParameter(context));
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
            AtPrefixedString => ParseMultiStatementTvfTail(context, schema, functionName, parameters, isAlter, createOrAlter),
            ReservedKeyword { Keyword: Keyword.Table } => ParseInlineTvfTail(context, schema, functionName, parameters, isAlter, createOrAlter),
            _ => ParseScalarTail(context, schema, functionName, parameters, isAlter, createOrAlter),
        };
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
    private static bool ParseMultiStatementTvfTail(ParserContext context, Schema schema, MultiPartName functionName, List<UdfParameter> parameters, bool isAlter, bool createOrAlter)
    {
        var returnVariableName = ((AtPrefixedString)context.Token!).Value;
        context.MoveNextRequired(); // consume @r

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Table })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Note: even in skip mode, the column list must be tokenized so the
        // cursor advances past the closing `)`. The helper returns false in
        // skip mode AFTER consuming the column list — the body still needs
        // to be captured below.
        var hasResolvedColumns = TryParseTableVariableColumnsAndConstraints(
            context,
            "@" + returnVariableName,
            out var outputColumns,
            out var keyConstraints,
            out var checkConstraints);

        // Optional WITH-clause (SCHEMABINDING is captured for
        // sys.sql_modules / OBJECTPROPERTY; ENCRYPTION parse-and-discards).
        var isSchemaBound = context.Token is ReservedKeyword { Keyword: Keyword.With }
            && ParseInlineTvfOptions(context);

        _ = ConsumeOptionalBodyAs(context);

        // BEGIN/END required for MS-TVF bodies (same shape as scalar UDF).
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var commandText = context.Command.CommandText;
        context.MoveNextRequired(); // step past BEGIN
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
        var definitionEnd = context.Token.EndIndex; // include the trailing END keyword
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

        // Bind the body before the schema dict is touched — see
        // BindModuleBodyAtCreate. Value-form RETURN raises Msg 178 from here,
        // which is where real reports it too.
        context.Simulation.BindMultiStatementTvfBodyAtCreate(
            context, functionName.Leaf, parameters, returnVariableName, outputColumns,
            keyConstraints, checkConstraints, bodyText,
            CountNewlines(commandText, context.Batch.CurrentStatement.StartIndex, bodyStart));

        var replaced = ResolveFunctionAlterTarget<MultiStatementTableValuedFunction>(context, schema, functionName, isAlter, createOrAlter);

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
            DefinitionText = BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, definitionEnd, isAlter, createOrAlter),
            IsSchemaBound = isSchemaBound,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
        };
        if (replaced is not null)
            function.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        schema.Functions[functionName.Leaf] = function;
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
    private static bool ParseScalarTail(ParserContext context, Schema schema, MultiPartName functionName, List<UdfParameter> parameters, bool isAlter, bool createOrAlter)
    {
        var returnType = ParseFunctionReturnType(context);

        // Optional WITH option [, option …] clause. RETURNS NULL ON NULL INPUT
        // is the only option that affects runtime semantics (NULL-propagation
        // skips the body); SCHEMABINDING records on the function for the
        // catalog surfaces without being enforced, and ENCRYPTION parse-and-
        // discards. Multiple options separate by commas.
        var returnsNullOnNullInput = false;
        var isSchemaBound = false;
        string? executeAsClause = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            while (true)
            {
                switch (context.Token)
                {
                    case UnquotedString { ContextualKeyword: ContextualKeyword.Returns }:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Input })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        returnsNullOnNullInput = true;
                        context.MoveNextRequired();
                        break;
                    case UnquotedString { ContextualKeyword: ContextualKeyword.SchemaBinding }:
                        isSchemaBound = true;
                        context.MoveNextRequired();
                        break;
                    case UnquotedString { ContextualKeyword: ContextualKeyword.Encryption }:
                        context.MoveNextRequired();
                        break;
                    case ReservedKeyword { Keyword: Keyword.Execute }:
                    case ReservedKeyword { Keyword: Keyword.Exec }:
                        // EXECUTE AS <caller> — consume EXECUTE, AS, and capture
                        // the following principal token (CALLER / SELF / OWNER /
                        // a quoted name) for the invocation-time frame push.
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                        executeAsClause = context.Token switch
                        {
                            Name principal => principal.Value,
                            Literal { Value: { IsNull: false } quoted } => quoted.AsString,
                            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                        };
                        context.MoveNextRequired();
                        break;
                    default:
                        throw new NotSupportedException(
                            "Scalar UDF WITH options accept RETURNS NULL ON NULL INPUT / SCHEMABINDING / ENCRYPTION / EXECUTE AS …; everything else is unmodeled.");
                }
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }

        // AS EXTERNAL NAME assembly.[class].method → the body lives in a
        // registered CLR assembly rather than in T-SQL. The CLR form needs the
        // AS, since EXTERNAL only follows it.
        var sawAs = ConsumeOptionalBodyAs(context);
        if (sawAs && context.Token is ReservedKeyword { Keyword: Keyword.External })
            return ParseClrScalarTail(context, schema, functionName, parameters, returnType, isSchemaBound, isAlter, createOrAlter);

        // BEGIN/END required for scalar UDF bodies. Capture span between
        // outer BEGIN (exclusive) and matching END (exclusive) using token-
        // level nesting; BEGIN TRAN / TRANSACTION / DISTRIBUTED don't open a
        // body block.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var commandText = context.Command.CommandText;
        context.MoveNextRequired(); // step past BEGIN
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
        var definitionEnd = context.Token.EndIndex; // include the trailing END keyword
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

        // Bind the body before the schema dict is touched — see
        // BindModuleBodyAtCreate.
        context.Simulation.BindScalarFunctionBodyAtCreate(
            context, functionName.Leaf, parameters, returnType, bodyText,
            CountNewlines(commandText, context.Batch.CurrentStatement.StartIndex, bodyStart));

        var replaced = ResolveFunctionAlterTarget<ScalarFunction>(context, schema, functionName, isAlter, createOrAlter);

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
            DefinitionText = BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, definitionEnd, isAlter, createOrAlter),
            ExecuteAsClause = executeAsClause,
            ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, executeAsClause),
            IsSchemaBound = isSchemaBound,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
        };
        if (replaced is not null)
            function.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        schema.Functions[functionName.Leaf] = function;
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

    private static bool ParseInlineTvfTail(ParserContext context, Schema schema, MultiPartName functionName, List<UdfParameter> parameters, bool isAlter, bool createOrAlter)
    {
        context.MoveNextRequired(); // step past TABLE

        // Optional WITH-clause: SCHEMABINDING is captured, ENCRYPTION
        // parse-and-discards. RETURNS NULL ON NULL INPUT here → Msg 487.
        var isSchemaBound = context.Token is ReservedKeyword { Keyword: Keyword.With }
            && ParseInlineTvfOptions(context);

        _ = ConsumeOptionalBodyAs(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Return })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional `(` before the SELECT; if present, the matching `)` ends
        // the body. Otherwise the body extends to the end of the batch (or
        // the next statement-starting keyword).
        context.MoveNextRequired();
        var commandText = context.Command.CommandText;
        var openedParen = context.Token is Operator { Character: '(' };
        if (openedParen)
            context.MoveNextRequired();

        var bodyStart = context.Token?.StartIndex
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        var bodyEnd = CaptureInlineTvfBody(context, openedParen);
        var bodyText = commandText[bodyStart..bodyEnd];
        // For the parenthesized form the cursor sits on the matching `)`; the
        // stored definition includes it. The bare RETURN form runs to bodyEnd.
        var definitionEnd = openedParen ? context.Token.EndIndex : bodyEnd;

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

        var replaced = ResolveFunctionAlterTarget<InlineTableValuedFunction>(context, schema, functionName, isAlter, createOrAlter);

        if (isSchemaBound)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "function", $"{schema.Name}.{functionName.Leaf}", bodyText);

        var outputColumns = InferInlineTvfOutputColumns(context, [.. parameters], bodyText, functionName.Leaf);

        var function = new InlineTableValuedFunction(
            schema,
            functionName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            outputColumns,
            bodyText,
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            DefinitionText = BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, definitionEnd, isAlter, createOrAlter),
            IsSchemaBound = isSchemaBound,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
        };
        if (replaced is not null)
            function.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        schema.Functions[functionName.Leaf] = function;
        RecordDdlEvent(context, replaced is null ? "CREATE_FUNCTION" : "ALTER_FUNCTION", schema.Name, functionName.Leaf, "FUNCTION");
        return true;
    }

    /// <summary>
    /// Consumes a <c>WITH option [, option ...]</c> clause on an inline TVF.
    /// Cursor on entry: the <c>WITH</c> keyword. Cursor on exit: the first
    /// token after the option list (expected to be <c>AS</c>). Returns true
    /// when <c>SCHEMABINDING</c> was among the options.
    /// </summary>
    private static bool ParseInlineTvfOptions(ParserContext context)
    {
        var isSchemaBound = false;
        context.MoveNextRequired();
        while (true)
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.SchemaBinding }:
                    isSchemaBound = true;
                    context.MoveNextRequired();
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Encryption }:
                    context.MoveNextRequired();
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Returns }:
                    // WITH RETURNS NULL ON NULL INPUT on a TVF → Msg 487.
                    throw SimulatedSqlException.InvalidOptionForCreateFunction();
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return isSchemaBound;
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
        context.MoveNextOptional();
        while (context.Token is not null)
        {
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
                    if (openedParen || depth != 0 || !IsStatementBoundary(context.Token))
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
        string functionName)
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
            variables[p.Name] = new VariableSlot(p.Type, declaredMaxLength: null, SqlValue.Null(p.Type), parameter: null);
        }

        // Use the scalar-UDF body batch constructor — it accepts a synthesized
        // command + a pre-seeded variable dict, which is exactly what we need
        // for one-shot parse-only inspection. We don't actually dispatch
        // anything here.
        var dummyFrame = new UdfFrame(SqlType.Int32);
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame);
        // Inspection runs the body's FROM-less projections, so the batch needs
        // the CREATE statement's own current-time freeze to evaluate a
        // GETDATE() / SYSDATETIME() column.
        innerBatch.AdoptStatementFreezeFrom(outerContext.Batch);
        var parser = innerBatch.Parser;
        parser.MoveNextRequired();

        var selection = ParseBodyQuery(parser, rejectsNextValueFor: true);

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
            columns[i] = new HeapColumn(name, selection.Schema[i], maxLength: null, nullable: nullable);
        }
        return columns;
    }

    /// <summary>
    /// Parses one entry in a <c>CREATE FUNCTION</c> parameter list:
    /// <c>@name type [= default]</c>. Cursor on entry: the leading <c>@</c>
    /// or parameter name token. Cursor on exit: the trailing <c>,</c> or
    /// <c>)</c> separator (caller decides which).
    /// </summary>
    private static UdfParameter ParseParameter(ParserContext context)
    {
        if (context.Token is not AtPrefixedString variable)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = variable.Value;
        context.MoveNextRequired();

        var paramType = ParseFunctionReturnType(context);

        Expression? defaultExpression = null;
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            defaultExpression = Expression.Parse(context);
        }
        return new UdfParameter(name, paramType, defaultExpression);
    }

    /// <summary>
    /// Parses a <c>RETURNS &lt;type&gt;</c> or parameter type expression. The
    /// grammar is a subset of CREATE TABLE column types: a type name optionally
    /// followed by <c>(N)</c> / <c>(N, S)</c> / <c>(MAX)</c>. Cursor on entry:
    /// the type-name token. Cursor on exit: the first token past the type
    /// (e.g. <c>WITH</c>, <c>AS</c>, <c>=</c>, <c>,</c>, <c>)</c>).
    /// </summary>
    private static SqlType ParseFunctionReturnType(ParserContext context)
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
            context.MoveNextRequired();
        }

        var (resolvedType, _, _) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: 1, columnName: null);
        return resolvedType;
    }
}
