using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Instance-method call on an <c>xml</c> value: <c>expr.value(…)</c>,
/// <c>expr.nodes(…)</c>, <c>expr.query(…)</c>, <c>expr.exist(…)</c>, or
/// <c>expr.modify(…)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>value</c> evaluates its XQuery path against the target xml through
/// <see cref="XmlQueryEngine"/> and casts the selected node's string value to
/// the requested SQL type (its second argument, a string literal). <c>nodes</c>
/// produces a rowset and is only valid in a FROM / APPLY source position; the
/// parser (<see cref="Selection"/>) intercepts the parsed <see cref="XmlMethodCall"/>
/// there via <see cref="IsNodes"/> / <see cref="Target"/> / <see cref="XQuery"/>
/// and builds a correlated source — reaching <see cref="Run"/> for <c>nodes</c>
/// means it appeared in scalar position, which is unsupported.
/// </para>
/// <para>
/// <c>modify</c> is the mutator, legal only as the whole right-hand side of
/// <c>SET @x.modify(…)</c> or an UPDATE's <c>SET col.modify(…)</c>; those two
/// sites parse it through <see cref="XmlModify"/>, so seeing it here means it
/// was written in a value position and the answer is Msg 8137.
/// </para>
/// </remarks>
internal sealed class XmlMethodCall : Expression
{
    /// <summary>The xml-valued expression the method is invoked on.</summary>
    public readonly Expression Target;

    private readonly string methodName;
    private readonly XmlQueryExpr? xquery;
    private readonly SqlType valueType;
    private readonly int? valueMaxLength;

    private XmlMethodCall(Expression target, string methodName, XmlQueryExpr? xquery, SqlType valueType, int? valueMaxLength)
    {
        this.Target = target;
        this.methodName = methodName;
        this.xquery = xquery;
        this.valueType = valueType;
        this.valueMaxLength = valueMaxLength;
    }

    /// <summary>True when this is a <c>.nodes()</c> call (rowset-producing).</summary>
    public bool IsNodes => this.methodName.Equals("nodes", StringComparison.Ordinal);

    /// <summary>The compiled XQuery argument, built at parse time.</summary>
    public XmlQueryExpr XQuery => this.xquery ?? throw new InvalidOperationException("XML method has no captured XQuery argument.");

    /// <summary>
    /// Returns true if <paramref name="name"/> matches one of the five XML
    /// instance method names. Used by the expression parser to take the
    /// method-call path instead of multipart-reference dispatch.
    /// </summary>
    public static bool IsKnownMethodName(string name) =>
        name.Equals("value", StringComparison.Ordinal)
        || name.Equals("nodes", StringComparison.Ordinal)
        || name.Equals("query", StringComparison.Ordinal)
        || name.Equals("exist", StringComparison.Ordinal)
        || name.Equals("modify", StringComparison.Ordinal);

    /// <summary>
    /// Parses <c>expr.MethodName(args)</c>. Cursor enters on <c>(</c>; on
    /// return cursor sits on the closing <c>)</c>. The first argument (XQuery
    /// path) and, for <c>value</c>, the second (target SQL type) are captured
    /// as compile-time string literals; a non-literal argument raises
    /// <see cref="NotSupportedException"/> (dynamic XQuery isn't modeled).
    /// </summary>
    public static XmlMethodCall Parse(Expression target, string methodName, ParserContext context)
    {
        // Reaching the expression parser at all means `.modify()` was written
        // somewhere a value is expected; real refuses the mutator there before
        // anything else, the SET-option gate included (probe-confirmed).
        if (methodName.Equals("modify", StringComparison.Ordinal))
            throw SimulatedSqlException.XmlMutatorInValuePosition();

        // Evaluating an XQuery expression is one of the operations real gates
        // on the SET-option set, so a session holding any of them the wrong way
        // can't call one at all — not even against an xml variable with no
        // index in sight (Msg 1934, probe-confirmed for QUOTED_IDENTIFIER and
        // for NUMERIC_ROUNDABORT). `.nodes()` alone is exempt; a `.value()` on
        // the node it produced is not, so gating the other four methods
        // reproduces both halves.
        if (!context.Batch.CreateTimeBinding && !methodName.Equals("nodes", StringComparison.Ordinal)
            && Simulation.IncorrectSetOptionNames(context) is { } setOptions)
        {
            throw SimulatedSqlException.IncorrectSetOptions(context.Batch.CurrentStatement.StatementVerb, setOptions);
        }

        var isValue = methodName.Equals("value", StringComparison.Ordinal);
        var isNodesOrValueOrQueryOrExist = isValue
            || methodName.Equals("nodes", StringComparison.Ordinal)
            || methodName.Equals("query", StringComparison.Ordinal)
            || methodName.Equals("exist", StringComparison.Ordinal);

        context.MoveNextRequired();
        string? xqueryText = null;
        SqlType valueType = SqlType.Xml;
        int? valueMaxLength = null;
        if (context.Token is not Operator { Character: ')' })
        {
            var firstArg = Expression.Parse(context);
            if (isNodesOrValueOrQueryOrExist)
                xqueryText = ConstantString(firstArg, context, "XML method path");

            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                var nextArg = Expression.Parse(context);
                if (isValue)
                    (valueType, valueMaxLength) = ResolveValueType(ConstantString(nextArg, context, "value() target type"), context.Batch);
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // The argument is a compile-time literal, so the expression compiles
        // once here — which is where real settles its static XQuery
        // diagnostics too.
        var xquery = xqueryText is null ? null : XmlQueryEngine.Compile(xqueryText, methodName);
        return new XmlMethodCall(target, methodName, xquery, valueType, valueMaxLength);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // .nodes() is rowset-producing (handled in FROM/APPLY parse, never
        // here), so reaching Run means it appeared in scalar position.
        if (this.methodName.Equals("nodes", StringComparison.Ordinal))
            throw new NotSupportedException($"XML instance method '.{this.methodName}()' is not modeled.");

        var input = this.Target.Run(runtime);
        switch (this.methodName)
        {
            case "exist":
                return input.IsNull ? SqlValue.Null(SqlType.Bit) : SqlValue.FromBoolean(XmlQueryEngine.EvaluateExists(input.AsString, this.xquery!));
            case "query":
                return input.IsNull ? SqlValue.Null(SqlType.Xml) : SqlValue.FromXml(XmlQueryEngine.EvaluateQuery(input.AsString, this.xquery!));
            default:
                if (input.IsNull)
                    return SqlValue.Null(this.valueType);
                var selected = XmlQueryEngine.EvaluateScalar(input.AsString, this.xquery!);
                return selected is null
                    ? SqlValue.Null(this.valueType)
                    : Cast.ApplyCoercion(SqlValue.FromString(SqlType.NVarchar, selected), this.valueType, this.valueMaxLength);
        }
    }

    /// <summary>
    /// Static result type, used by projection schema inference: <c>value</c>
    /// returns its requested target type; <c>exist</c> returns <c>bit</c>;
    /// <c>nodes</c> / <c>query</c> surface as <c>xml</c>.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.methodName.Equals("value", StringComparison.Ordinal)
            ? this.valueType
            : this.methodName.Equals("exist", StringComparison.Ordinal)
                ? SqlType.Bit
                : SqlType.Xml;

    internal override string DebugDisplay() => $"({this.Target.DebugDisplay()}).{this.methodName}(…)";

    /// <summary>
    /// Evaluates <paramref name="argument"/> against an empty resolver to pull
    /// out its compile-time string value; a column / variable / runtime
    /// reference surfaces as <see cref="NotSupportedException"/>.
    /// </summary>
    internal static string ConstantString(Expression argument, ParserContext context, string role)
    {
        try
        {
            var value = argument.Run(new RuntimeContext(_ => throw new InvalidOperationException(), context.Batch));
            if (!value.IsNull && SqlType.IsStringCategory(value.Type))
                return value.AsString;
        }
        catch (InvalidOperationException)
        {
            // Falls through to the unsupported-shape throw below.
        }
        throw new NotSupportedException($"A non-literal {role} argument to an XML method is not modeled.");
    }

    /// <summary>
    /// Resolves a <c>value()</c> target-type literal (e.g. <c>nvarchar(30)</c>,
    /// <c>money</c>, <c>decimal(9, 4)</c>, <c>integer</c>) into a
    /// <see cref="SqlType"/> + max-length by re-tokenizing the literal and
    /// reusing <see cref="SqlType.GetByName"/>. <c>integer</c> is mapped to
    /// <c>int</c> (an XQuery type synonym <see cref="SqlType.GetByName"/>
    /// doesn't itself accept).
    /// </summary>
    private static (SqlType Type, int? MaxLength) ResolveValueType(string spec, BatchContext batch)
    {
        var collation = batch.CurrentDatabase.Collation;
        var index = 0;
        Token? NextToken()
        {
            Token? token;
            do
            {
                token = Tokenizer.NextToken(spec, ref index, collation);
            }
            while (token is Whitespace);
            return token;
        }

        if (NextToken() is not Name typeName)
            throw new NotSupportedException($"Unrecognized XML value() target type '{spec}'.");
        if (typeName.Span.Equals("integer", StringComparison.OrdinalIgnoreCase))
            return (SqlType.Int32, null);

        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (NextToken() is Operator { Character: '(' })
        {
            declaredMaxLength = NextToken() switch
            {
                Numeric { Value: { IsNull: false } length } => length.AsInt32,
                UnquotedString { ContextualKeyword: ContextualKeyword.Max } => SqlType.MaxLengthSentinel,
                _ => throw new NotSupportedException($"Unrecognized XML value() target type '{spec}'."),
            };
            if (NextToken() is Operator { Character: ',' })
            {
                if (NextToken() is not Numeric { Value: { IsNull: false } scale })
                    throw new NotSupportedException($"Unrecognized XML value() target type '{spec}'.");
                declaredScale = scale.AsInt32;
                _ = NextToken();
            }
        }
        return SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, columnName: null);
    }
}
