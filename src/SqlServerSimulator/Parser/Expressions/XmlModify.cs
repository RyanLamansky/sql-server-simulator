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
    /// </summary>
    public static XmlModify Parse(
        Expression instance,
        string instanceName,
        string methodName,
        ParserContext context,
        Func<string, SqlType>? resolveColumnType)
    {
        if (!methodName.Equals("modify", StringComparison.Ordinal))
            throw SimulatedSqlException.XmlNonMutatorInMutatorPosition(methodName);

        // Evaluating XQuery is one of the operations real gates on the
        // SET-option set, mutators included — the verb it names is the
        // statement's own, so `SET @x.modify(…)` reports SELECT and an UPDATE
        // reports UPDATE (probe-confirmed).
        if (!context.Batch.CreateTimeBinding && Simulation.IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions(context.Batch.CurrentStatement.StatementVerb, setOptions);

        context.MoveNextRequired();
        var xquery = XmlMethodCall.ConstantString(Expression.Parse(context), context, "XML method path");
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new XmlModify(instance, instanceName, XmlDml.Parse(xquery, context, resolveColumnType));
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
    /// time, matching real's binder.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = this.instance.GetSqlType(batch, resolveColumnType);
        return type is XmlSqlType ? type : throw SimulatedSqlException.CannotCallMethodsOn(type.SqlServerName);
    }

    internal override string DebugDisplay() => $"{this.instanceName}.modify(…)";

    internal override void VisitColumnReferences(Action<MultiPartName> visit) => this.instance.VisitColumnReferences(visit);
}
