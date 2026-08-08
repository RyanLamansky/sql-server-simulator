using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The <c>xml</c> type's <c>.modify()</c> mutator, as an expression producing
/// the instance's post-edit value. Both places real accepts a mutator —
/// <c>SET @x.modify('…')</c> and an UPDATE's <c>SET col.modify('…')</c> —
/// desugar to an ordinary assignment of this expression to the same slot or
/// column, so the UPDATE pipeline's OUTPUT projection, trigger dispatch,
/// constraint enforcement and undo logging all see a plain new value.
/// </summary>
/// <remarks>
/// Reaching a value position instead raises Msg 8137 from
/// <see cref="XmlMethodCall"/>, which is where every other appearance of
/// <c>.modify()</c> lands.
/// </remarks>
internal sealed class XmlModify : Expression
{
    private readonly Expression instance;
    private readonly string instanceName;

    /// <summary>
    /// The XML-DML text as written, kept only while the compile is deferred.
    /// </summary>
    private readonly string pendingXQuery;
    private readonly Func<string, SqlType>? pendingResolveColumnType;

    /// <summary>
    /// The compiled statement, or null while the write target — and so the
    /// receiver's schema-collection binding — is still unknown. Bound exactly
    /// once, by <see cref="BindDeferredDml"/>.
    /// </summary>
    private XmlDml? dml;

    private XmlModify(
        Expression instance,
        string instanceName,
        string pendingXQuery,
        Func<string, SqlType>? pendingResolveColumnType,
        XmlDml? dml)
    {
        this.instance = instance;
        this.instanceName = instanceName;
        this.pendingXQuery = pendingXQuery;
        this.pendingResolveColumnType = pendingResolveColumnType;
        this.dml = dml;
    }

    /// <summary>
    /// Compiles the XML-DML text against the receiver's schema collection, for
    /// the statement shape that couldn't supply one while its SET list parsed.
    /// Idempotent: the forms that knew their target up front compiled at parse
    /// and this does nothing for them.
    /// </summary>
    /// <remarks>
    /// An alias-form <c>UPDATE a SET d.modify(…) FROM t AS a</c> names its
    /// target through the FROM clause, which parses <em>after</em> the SET
    /// list — so the collection that decides whether an element is a legal
    /// <c>replace value of</c> target isn't knowable there, and a typed column
    /// was refused with Msg 2356 on a statement real performs. Binding here
    /// keeps every diagnostic at compile time, where real raises them.
    /// </remarks>
    internal void BindDeferredDml(ParserContext context, Schemas.XmlSchemaCollection? collection, string receiverName) =>
        this.dml ??= XmlDml.Parse(
            this.pendingXQuery,
            context,
            this.pendingResolveColumnType,
            collection,
            XmlMethodCall.DisplayMethod(receiverName, "modify"));

    /// <summary>The compiled statement, which every execution path binds before reaching.</summary>
    private XmlDml Dml => this.dml ?? throw new InvalidOperationException(
        "The .modify() body was never bound — every UPDATE path must call BindDeferredDml once its target is known.");

    /// <summary>
    /// Parses <c>&lt;instance&gt;.&lt;method&gt;(…)</c> in a mutator position.
    /// The cursor enters on <c>(</c> and leaves on the token after <c>)</c>.
    /// A non-mutator method name here is Msg 8113, mirroring the Msg 8137
    /// <see cref="XmlMethodCall"/> raises for the opposite mistake.
    /// <paramref name="resolveColumnType"/> supplies types for
    /// <c>sql:column</c> references, and is null where no column scope exists.
    /// <paramref name="schemaCollection"/> is the receiver's
    /// <c>xml(&lt;collection&gt;)</c> binding when the caller already knows it;
    /// <paramref name="deferDml"/> says it can't know yet, which leaves the
    /// body uncompiled until <see cref="BindDeferredDml"/> supplies one — and
    /// with it <paramref name="receiverName"/>, the dotted name real prefixes
    /// this statement's diagnostics with (empty for a variable receiver, which
    /// carries none).
    /// </summary>
    public static XmlModify Parse(
        Expression instance,
        string instanceName,
        string methodName,
        ParserContext context,
        Func<string, SqlType>? resolveColumnType,
        Schemas.XmlSchemaCollection? schemaCollection = null,
        bool deferDml = false,
        string receiverName = "")
    {
        if (!methodName.Equals("modify", StringComparison.Ordinal))
            throw SimulatedSqlException.XmlNonMutatorInMutatorPosition(methodName);

        // Evaluating XQuery is one of the operations real gates on the
        // SET-option set, mutators included — the verb it names is the
        // statement's own, so `SET @x.modify(…)` reports SELECT and an UPDATE
        // reports UPDATE (probe-confirmed).
        if (!context.Batch.CreateTimeBinding && Simulation.IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions(context.Batch.CurrentStatement.StatementVerb, setOptions);

        // A typed receiver contributes its schema collection: an element whose
        // declared type holds a value is a legal `replace value of` target,
        // where the same element over untyped xml is Msg 2356.
        var collection = schemaCollection ?? XmlMethodCall.ResolveTargetSchemaCollection(instance, context);

        context.MoveNextRequired();
        var xquery = XmlMethodCall.ConstantString(Expression.Parse(context), context, "XML method path");
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new XmlModify(
            instance,
            instanceName,
            xquery,
            resolveColumnType,
            deferDml
                ? null
                : XmlDml.Parse(xquery, context, resolveColumnType, collection, XmlMethodCall.DisplayMethod(receiverName, "modify")));
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var input = this.instance.Run(runtime);
        return input.Type is not XmlSqlType ? throw SimulatedSqlException.CannotCallMethodsOn(input.Type.SqlServerName)
            : input.IsNull ? throw SimulatedSqlException.XmlMutatorOnNullValue(this.instanceName)
            : SqlValue.FromXml(this.Dml.Apply(input.AsString, runtime));
    }

    /// <summary>
    /// The edited instance keeps its type. Resolving the target's own type
    /// here is what reports Msg 258 for a non-xml UPDATE column at compile
    /// time, matching real's binder — and the statement calls this with its
    /// whole scope, which is why the XML-DML text's <c>sql:column</c>
    /// references bind here rather than while the SET list parses (the FROM
    /// clause they may name comes after it).
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = this.instance.GetSqlType(batch, resolveColumnType);
        if (type is not XmlSqlType)
            throw SimulatedSqlException.CannotCallMethodsOn(type.SqlServerName);
        foreach (var accessor in this.Dml.ValueAccessors)
        {
            if (accessor.IsColumn)
                _ = resolveColumnType(XmlDml.ColumnNameOf(accessor.Name));
        }

        return type;
    }

    internal override string DebugDisplay() => $"{this.instanceName}.modify(…)";

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) => this.instance.VisitColumnReferences(visit);
}
