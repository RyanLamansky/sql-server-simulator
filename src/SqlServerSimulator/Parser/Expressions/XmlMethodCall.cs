using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
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
    /// <summary>
    /// Whether an xml method call sits anywhere in <paramref name="root"/>'s
    /// own scope (a subquery binds in its own and isn't entered).
    /// </summary>
    internal static bool AppearsIn(ExpressionNode root)
    {
        var found = false;
        root.Walk((node, _) =>
        {
            found |= node is XmlMethodCall;
            return !found;
        });
        return found;
    }

    /// <summary>The xml-valued expression the method is invoked on.</summary>
    public readonly Expression Target;

    /// <summary>
    /// The XML schema collection the target is bound to, or null when the
    /// target is untyped <c>xml</c>. Read by the <c>.nodes()</c> FROM source,
    /// which stamps it on the node column it produces so a <c>.value()</c>
    /// against that column stays typed — the chain AdventureWorks'
    /// <c>Person.vAdditionalContactInfo</c> reads through.
    /// </summary>
    public readonly XmlSchemaCollection? TargetSchemaCollection;

    /// <summary>
    /// The dotted receiver name real prefixes this call's XQuery diagnostics
    /// with, or empty for a receiver that carries none. Read by the
    /// <c>.nodes()</c> FROM source, which stamps it on the row column it
    /// produces so a downstream <c>.value()</c> reports the originating
    /// column rather than the node source's own alias.
    /// </summary>
    public readonly string ReceiverName;

    private readonly string methodName;
    private readonly XmlMethod method;

    /// <summary>
    /// The XQuery argument as written: what <see cref="Describe"/> reports,
    /// since the compiled <see cref="xquery"/> has no equality of its own.
    /// </summary>
    private readonly string? xqueryText;

    private readonly XmlQueryExpr? xquery;
    private readonly SqlType valueType;
    private readonly int? valueMaxLength;

    /// <summary>
    /// The <c>sql:variable</c> / <c>sql:column</c> accessors the expression
    /// names, each with the slot its value fills before evaluation.
    /// </summary>
    private readonly XmlSqlAccessorRef[] accessors;

    private XmlMethodCall(Expression target, string methodName, XmlMethod method, string? xqueryText, XmlQueryExpr? xquery, SqlType valueType, int? valueMaxLength, XmlSchemaCollection? targetSchemaCollection, string receiverName, XmlSqlAccessorRef[] accessors)
    {
        this.accessors = accessors;
        this.Target = target;
        this.ReceiverName = receiverName;
        this.methodName = methodName;
        this.method = method;
        this.xqueryText = xqueryText;
        this.xquery = xquery;
        this.valueType = valueType;
        this.valueMaxLength = valueMaxLength;
        this.TargetSchemaCollection = targetSchemaCollection;
    }

    /// <summary>True when this is a <c>.nodes()</c> call (rowset-producing).</summary>
    public bool IsNodes => this.method == XmlMethod.Nodes;

    /// <summary>The compiled XQuery argument, built at parse time.</summary>
    public XmlQueryExpr XQuery => this.xquery ?? throw new InvalidOperationException("XML method has no captured XQuery argument.");

    /// <summary>
    /// Returns true if <paramref name="name"/> matches one of the five XML
    /// instance method names. Used by the expression parser to take the
    /// method-call path instead of multipart-reference dispatch.
    /// </summary>
    public static bool IsKnownMethodName(string name) =>
        TryGetMethod(name, out _);

    /// <summary>
    /// Maps a written method name to its dispatch discriminator. Real spells
    /// these lowercase and matches them ordinally, which is what a
    /// <c>switch</c> over string constants does — in one dispatch rather than
    /// one compare per name, which matters because the expression parser asks
    /// for every <c>.</c>-qualified name it meets.
    /// </summary>
    private static bool TryGetMethod(string name, out XmlMethod method)
    {
        switch (name)
        {
            case "exist": method = XmlMethod.Exist; return true;
            case "modify": method = XmlMethod.Modify; return true;
            case "nodes": method = XmlMethod.Nodes; return true;
            case "query": method = XmlMethod.Query; return true;
            case "value": method = XmlMethod.Value; return true;
            default: method = default; return false;
        }
    }

    /// <summary>
    /// Parses <c>expr.MethodName(args)</c>. Cursor enters on <c>(</c>; on
    /// return cursor sits on the closing <c>)</c>. The first argument (XQuery
    /// path) and, for <c>value</c>, the second (target SQL type) are captured
    /// as compile-time string literals; anything else — a variable, an
    /// expression, <c>NULL</c> — is Msg 8172 while the batch compiles.
    /// </summary>
    public static XmlMethodCall Parse(Expression target, string methodName, ParserContext context)
    {
        if (!TryGetMethod(methodName, out var method))
            throw new InvalidOperationException($"{methodName} is not an XML instance method.");

        // Reaching the expression parser at all means `.modify()` was written
        // somewhere a value is expected; real refuses the mutator there before
        // anything else, the SET-option gate included (probe-confirmed).
        if (method == XmlMethod.Modify)
            throw SimulatedSqlException.XmlMutatorInValuePosition();

        // Evaluating an XQuery expression is one of the operations real gates
        // on the SET-option set, so a session holding any of them the wrong way
        // can't call one at all — not even against an xml variable with no
        // index in sight (Msg 1934, probe-confirmed for QUOTED_IDENTIFIER and
        // for NUMERIC_ROUNDABORT). `.nodes()` alone is exempt; a `.value()` on
        // the node it produced is not, so gating the other four methods
        // reproduces both halves.
        if (!context.Batch.CreateTimeBinding && method != XmlMethod.Nodes
            && Simulation.IncorrectSetOptionNames(context) is { } setOptions)
        {
            throw SimulatedSqlException.IncorrectSetOptions(context.Batch.CurrentStatement.StatementVerb, setOptions);
        }

        _ = context.IndexedViewShapeCollector?.UsesXmlMethod = true;
        var isValue = method == XmlMethod.Value;

        // `value` takes two arguments and the other three one; any other
        // count is Msg 174 before either is read (probed 2026-10-02 against
        // SQL Server 2025).
        var arity = isValue ? 2 : 1;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (BuiltInArity.CountArguments(context) is >= 0 and var count && count != arity)
            throw SimulatedSqlException.FunctionRequiresNArguments(methodName, arity);
        context.RestoreCheckpoint(checkpoint);

        context.MoveNextRequired();
        string? xqueryText = null;
        SqlType valueType = SqlType.Xml;
        int? valueMaxLength = null;
        if (context.Token is not Operator { Character: ')' })
        {
            // Each of the four methods that reach here takes the XQuery
            // expression as its first argument; `.modify()`, the one that
            // doesn't, was refused above.
            var firstArg = Expression.Parse(context);
            xqueryText = ConstantString(firstArg, methodName, 1);

            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                var nextArg = Expression.Parse(context);
                if (isValue)
                    (valueType, valueMaxLength) = ResolveValueType(ConstantString(nextArg, methodName, 2), context.Batch);
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // The argument is a compile-time literal, so the expression compiles
        // once here — which is where real settles its static XQuery
        // diagnostics too. A typed receiver contributes its schema
        // collection's singleton element names, which is the one input the
        // static cardinality rules read out of the binding.
        var collection = ResolveTargetSchemaCollection(target, context);
        var receiverName = ResolveReceiverName(target, context);
        var display = DisplayMethod(receiverName, methodName);
        var accessorScope = new XmlSqlAccessorScope((isColumn, name) => ResolveAccessorType(isColumn, name, context, display));
        var xquery = xqueryText is null
            ? null
            : XmlQueryEngine.Compile(xqueryText, methodName, collection?.GetStaticTyping(), display, accessorScope, context.XmlNamespaces, ReceiverNodeType(target, context));
        return new XmlMethodCall(target, methodName, method, xqueryText, xquery, valueType, valueMaxLength, collection, receiverName, [.. accessorScope.Accessors]);
    }

    /// <summary>
    /// Binds one <c>sql:variable</c> / <c>sql:column</c> while the expression
    /// compiles and answers the XQuery type its value carries — <c>xs:int</c>
    /// for an <c>int</c>, <c>xs:string</c> for the character types — which is
    /// what types a comparison or arithmetic over it (probed 2026-09-28
    /// against SQL Server 2025). A name that isn't a variable (Msg 9519), a
    /// variable never declared (Msg 9501), a column the scope doesn't hold
    /// (Msg 207 / 107), an <c>xml</c> value (Msg 9342) and a type with no
    /// mapping (Msg 9344) are all compile-time refusals.
    /// </summary>
    private static string ResolveAccessorType(bool isColumn, string name, ParserContext context, string display)
    {
        SqlType type;
        if (!isColumn)
        {
            if (name.Length < 2 || name[0] != '@')
                throw SimulatedSqlException.XQuerySqlVariableNameInvalid(name);
            if (!context.Batch.Variables.TryGetValue(name[1..], out var slot))
                throw SimulatedSqlException.XQuerySqlVariableNotFound(name);
            type = slot.DeclaredType;
        }
        else
        {
            var column = XmlDml.ColumnNameOf(name);
            if (context.ScopeSources is not { } sources)
                throw SimulatedSqlException.InvalidColumnName(column);
            var (sourceIndex, columnIndex) = Selection.FindSourceColumn(sources, column);
            if (sourceIndex < 0)
            {
                throw column.ImmediateQualifier is { } qualifier
                    && !Array.Exists(sources, source => source.Qualifier is { } written && BuiltInToken.Equals(written, qualifier))
                    ? SimulatedSqlException.ColumnPrefixDoesNotMatch(qualifier)
                    : SimulatedSqlException.InvalidColumnName(column);
            }
            type = sources[sourceIndex].Columns[columnIndex].Type;
        }

        if (type is XmlSqlType)
            throw SimulatedSqlException.XQuerySqlAccessorXmlNotAllowed(display);
        return XmlAtomicTypes.SqlTypeName(type) ?? throw SimulatedSqlException.XQuerySqlAccessorTypeNotSupported(display, type.SqlServerName);
    }

    /// <summary>
    /// Reads each accessor's value for the current row into a fresh scope, or
    /// null when the expression names none. A NULL value binds the empty
    /// sequence.
    /// </summary>
    internal XmlVariableScope? BuildAccessorScope(RuntimeContext runtime)
    {
        if (this.accessors.Length == 0)
            return null;
        var scope = new XmlVariableScope();
        foreach (var accessor in this.accessors)
        {
            var value = accessor.IsColumn
                ? runtime.ResolveColumn(XmlDml.ColumnNameOf(accessor.Name))
                : runtime.Batch.Variables[accessor.Name[1..]].Value;
            scope.Write(accessor.Slot, value.IsNull ? [] : [XmlAtomicTypes.FromSql(value)]);
        }
        return scope;
    }

    /// <summary>
    /// What real writes between the brackets of an XQuery diagnostic raised by
    /// a method call on <paramref name="target"/>: the receiver's dotted name
    /// followed by the method, as in <c>XQuery [dbo.xr.d.value()]</c>. Empty
    /// for a receiver real names nothing for — a variable, a literal, an
    /// expression — whose diagnostics read <c>XQuery [value()]</c>.
    /// </summary>
    /// <remarks>
    /// The source half is the object <em>as the FROM clause wrote it</em> with
    /// any alias ignored (<c>xr</c>, <c>dbo.xr</c>, <c>ProbeScratch.dbo.xr</c>,
    /// <c>#tt</c>, <c>@t</c>, a synonym by the synonym's own name), falling back
    /// to the alias for a source that has no object name of its own — a derived
    /// table, a CTE, a table value constructor. That is
    /// <see cref="FromSource.WrittenObjectName"/>'s rule exactly, which the
    /// GROUP BY containment diagnostics already read for the same reason.
    /// </remarks>
    internal static string ResolveReceiverName(Expression target, ParserContext context)
    {
        if (target is not Reference reference || context.ScopeSources is not { } sources)
            return string.Empty;
        var (sourceIndex, columnIndex) = Selection.FindSourceColumn(sources, reference.ReferencedName);
        if (sourceIndex < 0)
            return string.Empty;
        var source = sources[sourceIndex];
        if (source.XmlReceiverName is { } inherited)
            return inherited;
        return (source.WrittenObjectName ?? source.Qualifier) is { } name
            ? $"{name}.{source.ColumnNames[columnIndex]}"
            : string.Empty;
    }

    /// <summary>
    /// The static type of the node a <c>.nodes()</c> row column stands on when
    /// it is an attribute; null for any other receiver.
    /// </summary>
    private static string? ReceiverNodeType(Expression target, ParserContext context)
    {
        if (target is not Reference reference || context.ScopeSources is not { } sources)
            return null;
        var (sourceIndex, columnIndex) = Selection.FindSourceColumn(sources, reference.ReferencedName);
        return sourceIndex < 0 ? null : sources[sourceIndex].Columns[columnIndex].XmlNodeStaticType;
    }

    /// <summary>
    /// Joins a receiver name to a method name the way real's bracket reads —
    /// <c>dbo.xr.d.value</c>, or the bare <c>value</c> for an unnamed receiver.
    /// </summary>
    internal static string DisplayMethod(string receiverName, string method) =>
        receiverName.Length == 0 ? method : $"{receiverName}.{method}";

    /// <summary>
    /// Finds the XML schema collection the receiver is bound to, or null for
    /// an untyped receiver. Three receivers carry a binding: a column of a
    /// source in scope (including the node column a <c>.nodes()</c> source
    /// produced, which inherits its own target's binding), a local variable
    /// declared <c>xml(&lt;collection&gt;)</c>, and a <c>CAST</c> /
    /// <c>CONVERT</c> to one. Everything else — a literal, a conversion to
    /// plain <c>xml</c>, an expression — is untyped, as it is on real.
    /// </summary>
    internal static XmlSchemaCollection? ResolveTargetSchemaCollection(Expression target, ParserContext context)
    {
        switch (target)
        {
            case VariableReference variable:
                return context.Batch.GetVariableSlot(variable.VariableName).XmlSchemaCollection;
            case Cast { TargetCollection: { } castCollection }:
                return castCollection;
            case ConvertExpression { TargetCollection: { } convertCollection }:
                return convertCollection;
        }
        if (target is not Reference reference || context.ScopeSources is not { } sources)
            return null;
        var (sourceIndex, columnIndex) = Selection.FindSourceColumn(sources, reference.ReferencedName);
        return sourceIndex < 0 ? null : sources[sourceIndex].Columns[columnIndex].XmlSchemaCollection;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // .nodes() is rowset-producing (handled in FROM/APPLY parse, never
        // here), so reaching Run means it appeared in scalar position.
        if (this.method == XmlMethod.Nodes)
            throw new NotSupportedException($"XML instance method '.{this.methodName}()' is not modeled.");

        var input = this.Target.Run(runtime);
        switch (this.method)
        {
            case XmlMethod.Exist:
                return input.IsNull ? SqlValue.Null(SqlType.Bit) : SqlValue.FromBoolean(XmlQueryEngine.EvaluateExists(input.AsString, this.xquery!, this.BuildAccessorScope(runtime), runtime.Batch.XmlNodeDocuments));
            case XmlMethod.Query:
                return input.IsNull ? SqlValue.Null(SqlType.Xml) : SqlValue.FromXml(XmlQueryEngine.EvaluateQuery(input.AsString, this.xquery!, this.BuildAccessorScope(runtime), runtime.Batch.XmlNodeDocuments));
            default:
                if (input.IsNull)
                    return SqlValue.Null(this.valueType);
                var selected = XmlQueryEngine.EvaluateScalar(input.AsString, this.xquery!, this.BuildAccessorScope(runtime), runtime.Batch.XmlNodeDocuments);
                return selected is null ? SqlValue.Null(this.valueType)
                    : this.valueType is VarbinarySqlType or BinarySqlType or RowVersionSqlType ? Base64Value(selected, this.valueType)
                    : Cast.ApplyCoercion(SqlValue.FromString(SqlType.NVarchar, selected), this.valueType, this.valueMaxLength);
        }
    }

    /// <summary>
    /// A binary target reads the value as base64, as <c>xs:base64Binary</c>
    /// does, and text that isn't base64 as NULL; a fixed-length target pads
    /// (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static SqlValue Base64Value(string text, SqlType target)
    {
        var bytes = new byte[(text.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(text, bytes, out var written))
            return SqlValue.Null(target);
        var value = SqlValue.FromVarbinary(bytes[..written]);
        return target is RowVersionSqlType ? value.CoerceTo(SqlType.GetBinary(8)).CoerceTo(target) : value.CoerceTo(target);
    }

    /// <summary>
    /// Static result type, used by projection schema inference: <c>value</c>
    /// returns its requested target type; <c>exist</c> returns <c>bit</c>;
    /// <c>nodes</c> / <c>query</c> surface as <c>xml</c>.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.method switch
        {
            XmlMethod.Value => this.valueType,
            XmlMethod.Exist => SqlType.Bit,
            // A rowset method read as a scalar is no method at all to real
            // (probed 2026-10-02 against SQL Server 2025).
            XmlMethod.Nodes => throw SimulatedSqlException.NotAValidFunctionPropertyOrField(this.methodName),
            _ => SqlType.Xml,
        };

    internal override string DebugDisplay() => $"({this.Target.DebugDisplay()}).{this.methodName}(…)";

    internal override void Describe(NodeShape shape) => shape.Local(this.method).LocalExact(this.xqueryText).Local(this.valueType).Local(this.valueMaxLength).Child(this.Target);

    /// <summary>
    /// The text of an argument real requires to be a string literal, through
    /// any parentheses; anything else is Msg 8172 naming the argument's
    /// <paramref name="position"/>.
    /// </summary>
    internal static string ConstantString(Expression argument, string methodName, int position)
    {
        while (argument is Parenthesized parenthesized)
            argument = parenthesized.Wrapped;
        return argument is Value { IsLiteral: true, Constant: { IsNull: false } constant } && SqlType.IsStringCategory(constant.Type)
            ? constant.AsString
            : throw SimulatedSqlException.XmlMethodArgumentNotStringLiteral(position, methodName);
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

        // What real can't read as one scalar type — a word it doesn't know, a
        // list, the legacy LOBs, xml, sql_variant, the CLR types — is Msg 9500
        // naming the text as written (probed 2026-10-02 against SQL Server
        // 2025).
        var invalid = SimulatedSqlException.XmlValueTypeInvalid(spec);
        if (NextToken() is not Name typeName)
            throw invalid;
        if (typeName.Span.Equals("integer", StringComparison.OrdinalIgnoreCase))
            return NextToken() is null ? (SqlType.Int32, null) : throw invalid;

        int? declaredMaxLength = null;
        int? declaredScale = null;
        var next = NextToken();
        if (next is Operator { Character: '(' })
        {
            declaredMaxLength = NextToken() switch
            {
                Numeric { Value: { IsNull: false } length } => length.AsInt32,
                UnquotedString { ContextualKeyword: ContextualKeyword.Max } => SqlType.MaxLengthSentinel,
                _ => throw invalid,
            };
            next = NextToken();
            if (next is Operator { Character: ',' })
            {
                if (NextToken() is not Numeric { Value: { IsNull: false } scale })
                    throw invalid;
                declaredScale = scale.AsInt32;
                next = NextToken();
            }
            if (next is not Operator { Character: ')' })
                throw invalid;
            next = NextToken();
        }
        if (next is not null)
            throw invalid;

        (SqlType Type, int? MaxLength) resolved;
        try
        {
            resolved = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, TypeSpecSite.Cast, columnName: null);
        }
        catch (SimulatedSqlException ex) when (ex.Number == 243)
        {
            throw invalid;
        }
        if (resolved.Type is XmlSqlType or TextSqlType or NTextSqlType or ImageSqlType or SqlVariantSqlType or HierarchyIdSqlType or SpatialSqlType or ClrUdtSqlType)
            throw invalid;

        // A character type written without a length is one character long, as
        // in a declaration.
        return declaredMaxLength is not null ? resolved : resolved.Type switch
        {
            VarcharSqlType v => (VarcharSqlType.Get(1, v.Collation, v.Coercibility), 1),
            NVarcharSqlType nv => (NVarcharSqlType.Get(1, nv.Collation, nv.Coercibility), 1),
            _ => resolved,
        };
    }
}

/// <summary>
/// The five <c>xml</c> instance methods, resolved from the written name once
/// at parse so evaluation dispatches on a discriminator rather than on text.
/// </summary>
internal enum XmlMethod : byte
{
    Exist,
    Modify,
    Nodes,
    Query,
    Value,
}
