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
        // Any other argument reaches the method's nvarchar parameter converted,
        // a binary one reinterpreted as UTF-16 (probed 2026-10-07 against SQL
        // Server 2025: Parse(1) and Parse(0x58) fail on the strings '1' and 'X').
        var str = arg.Type.Category == SqlTypeCategory.String ? arg.AsString : arg.CoerceTo(SqlType.NVarchar).AsString;
        return SqlValue.FromHierarchyId(HierarchyIdSqlType.ParsePath(str));
    }

    /// <summary>
    /// <c>Parse</c>'s argument reaches an <c>nvarchar</c> parameter, so one
    /// that converts only explicitly (<c>xml</c>) is Msg 257 while compiling.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.argument is not null)
            _ = AssignmentRules.ArgumentType(this.argument, SqlType.NVarchar, batch, resolveColumnType);
        return SqlType.HierarchyId;
    }

    internal override string DebugDisplay() => $"hierarchyid::{this.method}({this.argument?.DebugDisplay() ?? ""})";

    internal override void Describe(NodeShape shape) => shape.Local(this.method).Child(this.argument);

    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.argument is not null && this.argument.ResultIsNullable(context);
}
