using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE FUNCTION schema.name (@p1 type [= default], ...)
    /// RETURNS &lt;type&gt; [WITH RETURNS NULL ON NULL INPUT] AS BEGIN ... END</c>
    /// and stores a <see cref="UserDefinedFunction"/> in the target
    /// <see cref="Schema.Functions"/> dict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Body capture: the source span between the outer <c>BEGIN</c> (exclusive)
    /// and matching <c>END</c> (exclusive) is recorded as a raw <see cref="string"/>
    /// and re-tokenized per call. Nesting is tracked at the token level —
    /// each <c>BEGIN</c> (not followed by <c>TRAN</c>/<c>TRANSACTION</c>/
    /// <c>DISTRIBUTED</c>) increments depth, each <c>END</c> decrements.
    /// </para>
    /// <para>
    /// Probe-confirmed fidelity:
    /// </para>
    /// <list type="bullet">
    /// <item>Body without <c>BEGIN/END</c> wrapper → Msg 102 ("Incorrect
    /// syntax near 'RETURN'"). The parser requires the keyword after
    /// <c>AS</c> to be <c>BEGIN</c>.</item>
    /// <item>Duplicate function name (or any object) → Msg 2714 (reused
    /// factory from CREATE TABLE).</item>
    /// <item>Bare <c>fn(x)</c> call (1-part name) → Msg 195 — surfaced at the
    /// call site via <see cref="Expression"/>'s built-in resolver, which
    /// falls through to "not a recognized built-in" when the name is
    /// single-part.</item>
    /// </list>
    /// <para>
    /// <strong>Not enforced at CREATE today (fidelity gaps)</strong>: real
    /// SQL Server rejects <c>PRINT</c> / <c>THROW</c> / DML-on-permanent-
    /// tables / result-set <c>SELECT</c> in the body at CREATE time
    /// (Msg 443 / Msg 444), and rejects bodies whose last statement isn't
    /// <c>RETURN</c> (Msg 455). The simulator defers all of these to call
    /// time — when the body actually dispatches, side-effecting statements
    /// surface their own errors and a body that falls through without
    /// <c>RETURN</c> returns <see cref="SqlValue.Null"/> of the declared
    /// type. Apps that rely on CREATE-time rejection diverge.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateFunction(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var functionName = BatchContext.ParseObjectName(context);
        if (!context.Batch.TryResolveSchema(functionName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(functionName.ImmediateQualifier ?? Database.DefaultSchemaName);

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
        var returnType = ParseFunctionReturnType(context);

        // Optional WITH RETURNS NULL ON NULL INPUT. The simulator only models
        // this one option for now; SCHEMABINDING / ENCRYPTION / EXECUTE AS
        // parse but aren't observable.
        var returnsNullOnNullInput = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Returns })
            {
                throw new NotSupportedException(
                    "Only WITH RETURNS NULL ON NULL INPUT is modeled (SCHEMABINDING / ENCRYPTION / EXECUTE AS aren't).");
            }
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
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Real SQL Server requires BEGIN/END for scalar UDF bodies — probe-
        // confirmed `as return @x * 10` raises Msg 102. The matching END is
        // located by token-level BEGIN/END nesting (BEGIN TRAN / TRANSACTION /
        // DISTRIBUTED don't open a block).
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Begin })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var commandText = context.Command.CommandText;
        context.MoveNextRequired(); // step past BEGIN
        var bodyStart = context.Token.StartIndex;
        var depth = 1;
        while (depth > 0)
        {
            if (context.Token is null)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Begin }:
                    {
                        // Peek next token to skip BEGIN TRAN/TRANSACTION/DISTRIBUTED
                        // which don't open a body block.
                        var checkpoint = context.SaveCheckpoint();
                        context.MoveNextRequired();
                        var isTransactionStart = context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed };
                        context.RestoreCheckpoint(checkpoint);
                        if (!isTransactionStart)
                            depth++;
                        break;
                    }
                case ReservedKeyword { Keyword: Keyword.End }:
                    depth--;
                    if (depth == 0)
                        goto bodyCaptured;
                    break;
            }
            context.MoveNextRequired();
        }
    bodyCaptured:
        var bodyEnd = context.Token.StartIndex;
        var bodyText = commandText[bodyStart..bodyEnd];
        context.MoveNextOptional(); // consume END

        if (context.Batch.IsSkipping)
            return true;

        if (schema.Functions.ContainsKey(functionName.Leaf)
            || schema.HeapTables.ContainsKey(functionName.Leaf))
        {
            throw SimulatedSqlException.ThereIsAlreadyAnObject(functionName.Leaf);
        }

        var objectId = context.CurrentDatabase.AllocateObjectId();
        var function = new UserDefinedFunction(
            schema,
            functionName.Leaf,
            objectId,
            [.. parameters],
            returnType,
            returnsNullOnNullInput,
            bodyText,
            createDate: context.Batch.CurrentStatement.UtcNow);
        schema.Functions[functionName.Leaf] = function;
        return true;
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
        if (context.Token is not Name typeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
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

        var (resolvedType, _) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, index: 1, columnName: null);
        return resolvedType;
    }
}
