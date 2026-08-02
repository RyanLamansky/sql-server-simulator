using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Static-method call on the <c>hierarchyid</c> type: <c>hierarchyid::Parse(str)</c>
/// or <c>hierarchyid::GetRoot()</c>. Recognized inline in <see cref="Expression.Parse"/>'s
/// binary-operator loop when a bare <see cref="Reference"/> named
/// <c>hierarchyid</c> is followed by the <c>::</c> token pair.
/// </summary>
internal sealed class HierarchyIdStaticCall : Expression
{
    private readonly string method;
    private readonly Expression? argument;

    private HierarchyIdStaticCall(string method, Expression? argument)
    {
        this.method = method;
        this.argument = argument;
    }

    /// <summary>
    /// Parses the body following <c>hierarchyid::</c>. Cursor enters on the
    /// method name token; on return, cursor sits on the closing <c>)</c>
    /// (matching the rest of the expression parser's contract).
    /// </summary>
    public static new HierarchyIdStaticCall Parse(ParserContext context)
    {
        var methodName = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        Expression? arg = null;
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            arg = Expression.Parse(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        return methodName.Equals("Parse", StringComparison.Ordinal)
            ? arg is null
                ? throw SimulatedSqlException.SyntaxErrorNear(context)
                : new HierarchyIdStaticCall("Parse", arg)
            : methodName.Equals("GetRoot", StringComparison.Ordinal)
                ? arg is not null
                    ? throw SimulatedSqlException.SyntaxErrorNear(context)
                    : new HierarchyIdStaticCall("GetRoot", null)
                : throw new NotSupportedException($"hierarchyid::{methodName} is not modeled.");
    }

    public override SqlValue Run(RuntimeContext runtime) => this.method switch
    {
        "GetRoot" => SqlValue.FromHierarchyId([]),
        "Parse" => RunParse(runtime),
        _ => throw new InvalidOperationException($"Unhandled hierarchyid static method: {this.method}"),
    };

    private SqlValue RunParse(RuntimeContext runtime)
    {
        var arg = this.argument!.Run(runtime);
        if (arg.IsNull)
            return SqlValue.Null(SqlType.HierarchyId);
        var str = arg.Type.Category == SqlTypeCategory.String
            ? arg.AsString
            : throw SimulatedSqlException.InvalidHierarchyIdInput(arg.Type.ToString()!);
        return SqlValue.FromHierarchyId(HierarchyIdSqlType.ParsePath(str));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.HierarchyId;

    internal override string DebugDisplay() => $"hierarchyid::{this.method}({this.argument?.DebugDisplay() ?? ""})";

    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.argument is not null && this.argument.ResultIsNullable(context);

    internal override void VisitColumnReferences(Action<MultiPartName> visit) =>
        this.argument?.VisitColumnReferences(visit);
}
