using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [OR ALTER] PROCEDURE schema.name [(@p1 type [=
    /// default] [OUTPUT], ...)] [WITH options] AS body</c>. The body source
    /// is captured between the <c>AS</c> keyword (exclusive) and the
    /// trailing statement boundary, then re-tokenized per call inside a
    /// child <see cref="BatchContext"/> with parameters seeded as variables.
    /// Stored in the target <see cref="Schema.Procedures"/> dict keyed by
    /// name. Probed against SQL Server 2025 (2026-05-12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Body capture</strong>: unlike scalar UDFs (which require an
    /// outer <c>BEGIN ... END</c> wrapping), procedures accept either form.
    /// The body span runs from the first token after <c>AS</c> to end-of-
    /// batch. Empty bodies are legal — <c>CREATE PROC p AS</c> with nothing
    /// after <c>AS</c> succeeds and produces no result sets when invoked
    /// (probe-confirmed). Parens around the parameter list are also optional
    /// (<c>CREATE PROC p (@x int) AS</c> equivalent to <c>CREATE PROC p @x
    /// int AS</c>).
    /// </para>
    /// <para>
    /// <strong>OR ALTER</strong>: the modern <c>CREATE OR ALTER PROCEDURE</c>
    /// syntax does an upsert — creates when missing, replaces when present
    /// (preserving the <see cref="SchemaObject.ObjectId"/>). Pure
    /// <c>CREATE PROCEDURE</c> on an existing name raises Msg 2714; pure
    /// <c>ALTER PROCEDURE</c> on a missing name raises Msg 208.
    /// </para>
    /// <para>
    /// <strong>Fidelity gaps</strong>: real SQL Server enforces Msg 111 (the
    /// "CREATE/ALTER PROCEDURE must be the first statement in a query batch"
    /// rule). The simulator doesn't (same stance as scalar UDFs / views —
    /// no <c>GO</c> support means batch boundaries are CommandText
    /// boundaries, so the rule has no enforcement target). <c>WITH
    /// RECOMPILE</c> / <c>EXECUTE AS</c> / <c>ENCRYPTION</c> / <c>FOR
    /// REPLICATION</c> parse and are silently ignored — they affect query-
    /// planner / security / replication behavior the simulator doesn't
    /// model.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateProcedure(ParserContext context, bool isAlter, bool createOrAlter)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var procName = BatchContext.ParseObjectName(context);
        if (!context.Batch.TryResolveSchema(procName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(procName.ImmediateQualifier ?? Database.DefaultSchemaName);

        context.MoveNextRequired();

        // Optional parenthesized parameter list. Inside or outside parens,
        // parameter parsing is identical — the only difference is the
        // terminator (`)` vs the WITH/AS keyword).
        var openParen = context.Token is Operator { Character: '(' };
        if (openParen)
            context.MoveNextRequired();

        var parameters = new List<ProcedureParameter>();
        while (true)
        {
            if (openParen && context.Token is Operator { Character: ')' })
            {
                context.MoveNextRequired();
                break;
            }
            // Without parens, the WITH / AS keyword (or end-of-stream) ends
            // the parameter list. WITH is reserved; AS is reserved; parser
            // sees them as ReservedKeyword tokens.
            if (!openParen && context.Token is ReservedKeyword { Keyword: Keyword.With or Keyword.As })
                break;

            parameters.Add(ParseProcedureParameter(context));

            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            if (openParen && context.Token is Operator { Character: ')' })
            {
                context.MoveNextRequired();
                break;
            }
            if (!openParen && context.Token is ReservedKeyword { Keyword: Keyword.With or Keyword.As })
                break;
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // Optional WITH option-list before AS: RECOMPILE / EXECUTE AS / ENCRYPTION
        // are parse-and-ignore (we don't model query-planner / security /
        // encryption semantics). FOR REPLICATION is another parse-and-ignore.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            ParseProcedureWithOptions(context);

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Capture body source from the first token after AS to end-of-batch
        // (or whatever the outer dispatch considers the statement boundary).
        var commandText = context.Command.CommandText;
        context.MoveNextOptional();
        // Empty body is legal — `CREATE PROC p AS` with nothing after AS
        // succeeds in real SQL Server. The body capture below produces an
        // empty string, which the per-call invocation handles cleanly.
        var bodyStart = context.Token?.StartIndex ?? commandText.Length;
        var bodyEnd = commandText.Length;
        while (context.Token is not null)
        {
            bodyEnd = context.Token.EndIndex;
            context.MoveNextOptional();
        }
        var bodyText = commandText[bodyStart..bodyEnd];

        if (context.Batch.IsSkipping)
            return true;

        var existed = schema.Procedures.TryGetValue(procName.Leaf, out var existing);

        // CREATE-only (no OR ALTER) collides with any existing object of the
        // same name (procs share the namespace with tables / views /
        // functions). ALTER requires the proc to exist.
        if (!isAlter && !createOrAlter && schema.HasNameInSharedNamespace(procName.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(procName.Leaf);
        if (isAlter && !existed)
            throw SimulatedSqlException.InvalidObjectName(procName);

        // ALTER preserves the existing object_id (probe-confirmed). CREATE
        // and CREATE OR ALTER (on a missing name) allocate a fresh id.
        var objectId = existed ? existing!.ObjectId : context.CurrentDatabase.AllocateObjectId();
        var procedure = new Procedure(
            schema,
            procName.Leaf,
            objectId,
            [.. parameters],
            bodyText,
            createDate: existed ? existing!.CreateDate : context.Batch.CurrentStatement.UtcNow);
        schema.Procedures[procName.Leaf] = procedure;
        return true;
    }

    /// <summary>
    /// Parses one entry in a <c>CREATE PROCEDURE</c> parameter list:
    /// <c>@name type [= default] [OUTPUT]</c>. Cursor on entry: the leading
    /// <c>@</c> or parameter name token. Cursor on exit: the trailing
    /// separator (<c>,</c>, <c>)</c>, or the <c>WITH</c>/<c>AS</c> keyword).
    /// </summary>
    private static ProcedureParameter ParseProcedureParameter(ParserContext context)
    {
        if (context.Token is not AtPrefixedString variable)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = variable.Value;
        context.MoveNextRequired();

        // Try table-valued-parameter binding first. A multi-part name (e.g.
        // `dbo.MyType`) unambiguously means user-defined type; a 1-part name
        // checks TableTypes first with fallback to the scalar parser.
        var tableType = TryResolveProcedureTableTypeParameter(context, name);
        if (tableType is not null)
        {
            // READONLY is mandatory after a TVP parameter (probe-confirmed:
            // Msg 352 if missing). DEFAULT / OUTPUT shapes raise Msg 102
            // because the grammar after a TVP-type parameter only permits
            // READONLY (probe-confirmed wording: "Incorrect syntax near
            // '=' / 'output'").
            if (context.Token is Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.ReadOnly })
                throw SimulatedSqlException.TableValuedParameterMustBeReadOnly("@" + name);
            context.MoveNextRequired();
            // After READONLY no further trailers are accepted (no = default,
            // no OUTPUT).
            return new ProcedureParameter(name, SqlType.Int32, declaredMaxLength: null, defaultExpression: null, isOutput: false, tableType: tableType);
        }

        var (paramType, declaredMaxLength) = ParseProcedureParameterType(context);

        Expression? defaultExpression = null;
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            defaultExpression = Expression.Parse(context);
        }

        // `OUTPUT` (with the synonym `OUT`) marks the parameter as a writeback
        // slot. Real SQL Server treats `OUT` and `OUTPUT` as equivalent; both
        // surface as ContextualKeyword on the tokenizer side (neither is
        // reserved).
        var isOutput = false;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
        {
            isOutput = true;
            context.MoveNextRequired();
        }

        return new ProcedureParameter(name, paramType, declaredMaxLength, defaultExpression, isOutput);
    }

    /// <summary>
    /// Probes the cursor for a user-defined table type reference. Returns
    /// the matched <see cref="TableType"/> with the cursor advanced past the
    /// type name; returns null (cursor unchanged) for any other shape so the
    /// caller falls through to the scalar parameter-type parser.
    /// </summary>
    private static TableType? TryResolveProcedureTableTypeParameter(ParserContext context, string parameterName)
    {
        if (context.Token is not Name firstName)
            return null;

        // Multi-part detection: peek for `.` without permanently advancing.
        var checkpoint = context.SaveCheckpoint();
        var sawDot = context.MoveNext() && context.Token is Operator { Character: '.' };
        context.RestoreCheckpoint(checkpoint);

        if (sawDot)
        {
            // Multi-part name: only resolvable as user-defined type.
            // Built-in scalars are 1-part only; a 2-part scalar reference
            // would already fail in the scalar parser, so consuming the
            // name here is safe.
            var objectName = BatchContext.ParseObjectName(context);
            if (!context.Batch.TryResolveTableType(objectName, out var tableType))
                throw SimulatedSqlException.CannotFindDataType(parameterIndex: 1, objectName.ToString(), "@" + parameterName);
            context.MoveNextOptional();
            return tableType;
        }

        // 1-part: try TableTypes, fall through to scalar on miss.
        if (!context.Batch.TryResolveTableType(new MultiPartName(firstName.Value), out var singleType))
            return null;
        context.MoveNextOptional();
        return singleType;
    }

    /// <summary>
    /// Parses a procedure parameter type. Same grammar as <c>CREATE
    /// FUNCTION</c>'s parameter type (a type name with optional <c>(N)</c>
    /// or <c>(N, S)</c> or <c>(MAX)</c>) — returns both the resolved
    /// <see cref="SqlType"/> and the declared length (passed through to
    /// <see cref="ProcedureParameter.DeclaredMaxLength"/> for catalog-view
    /// surfaces).
    /// </summary>
    private static (SqlType Type, int? DeclaredMaxLength) ParseProcedureParameterType(ParserContext context)
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
        return (resolvedType, declaredMaxLength);
    }

    /// <summary>
    /// Consumes the optional <c>WITH option [, option ...]</c> clause before
    /// <c>AS</c>. Accepted options (all parse-and-ignore in the simulator):
    /// <c>RECOMPILE</c>, <c>ENCRYPTION</c>, <c>EXECUTE AS CALLER|SELF|OWNER|'name'</c>,
    /// <c>FOR REPLICATION</c>. Cursor on entry: the <c>WITH</c> keyword;
    /// cursor on exit: the <c>AS</c> keyword.
    /// </summary>
    private static void ParseProcedureWithOptions(ParserContext context)
    {
        context.MoveNextRequired();
        while (true)
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Recompile }:
                case UnquotedString { ContextualKeyword: ContextualKeyword.Encryption }:
                    context.MoveNextRequired();
                    break;
                case ReservedKeyword { Keyword: Keyword.Execute }:
                case ReservedKeyword { Keyword: Keyword.Exec }:
                    // EXECUTE AS <caller> — consume EXECUTE, AS, and the
                    // following principal token (CALLER / SELF / OWNER / a
                    // quoted name). The simulator has no principal model;
                    // the choice has no runtime effect.
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    if (context.Token is not (Name or Literal))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    break;
                case ReservedKeyword { Keyword: Keyword.For }:
                    if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Replication })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
    }
}
