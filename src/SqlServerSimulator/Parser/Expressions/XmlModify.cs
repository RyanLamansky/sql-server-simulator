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
    private readonly XmlDml dml;

    private XmlModify(Expression instance, string instanceName, XmlDml dml)
    {
        this.instance = instance;
        this.instanceName = instanceName;
        this.dml = dml;
    }

    /// <summary>
    /// Parses <c>&lt;instance&gt;.&lt;method&gt;(…)</c> in a mutator position.
    /// The cursor enters on <c>(</c> and leaves on the token after <c>)</c>.
    /// A non-mutator method name here is Msg 8113, mirroring the Msg 8137
    /// <see cref="XmlMethodCall"/> raises for the opposite mistake.
    /// <paramref name="resolveColumnType"/> supplies types for
    /// <c>sql:column</c> references, and is null where no column scope exists.
    /// <paramref name="schemaCollection"/> is the receiver's
    /// <c>xml(&lt;collection&gt;)</c> binding when the caller already knows it
    /// (an UPDATE names its target ahead of the SET list, whose columns are in
    /// no FROM scope yet); null falls back to resolving it off the receiver.
    /// </summary>
    public static XmlModify Parse(
        Expression instance,
        string instanceName,
        string methodName,
        ParserContext context,
        Func<string, SqlType>? resolveColumnType,
        Schemas.XmlSchemaCollection? schemaCollection = null)
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
        return new XmlModify(instance, instanceName, XmlDml.Parse(xquery, context, resolveColumnType, collection));
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var input = this.instance.Run(runtime);
        return input.Type is not XmlSqlType ? throw SimulatedSqlException.CannotCallMethodsOn(input.Type.SqlServerName)
            : input.IsNull ? throw SimulatedSqlException.XmlMutatorOnNullValue(this.instanceName)
            : SqlValue.FromXml(this.dml.Apply(input.AsString, runtime));
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
        foreach (var accessor in this.dml.ValueAccessors)
        {
            if (accessor.IsColumn)
                _ = resolveColumnType(XmlDml.ColumnNameOf(accessor.Name));
        }

        return type;
    }

    internal override string DebugDisplay() => $"{this.instanceName}.modify(…)";

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) => this.instance.VisitColumnReferences(visit);
}
