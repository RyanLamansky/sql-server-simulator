using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Instance-method call on a <c>hierarchyid</c> value, written as
/// <c>expr.MethodName(args)</c>. Recognized inline in <see cref="Expression.Parse"/>'s
/// binary-operator loop when an expression is followed by <c>.&lt;known-method&gt;(</c>;
/// dispatch routes by method name into <see cref="HierarchyIdMethod"/>.
/// </summary>
/// <remarks>
/// The method-name set is a closed accept-list against AW's exercised surface:
/// <c>GetLevel</c>, <c>GetAncestor</c>, <c>GetDescendant</c>,
/// <c>IsDescendantOf</c>, <c>ToString</c>. The closed-list shape means an
/// unrelated column literally named (for example) <c>GetLevel</c> can collide
/// with the parser's dispatch — accepted as a known limitation given the
/// AW-minimum-viable bundle scope.
/// </remarks>
internal sealed class HierarchyIdMethodCall : Expression
{
    private readonly Expression target;
    private readonly HierarchyIdMethod method;
    private readonly Expression[] arguments;

    private HierarchyIdMethodCall(Expression target, HierarchyIdMethod method, Expression[] arguments)
    {
        this.target = target;
        this.method = method;
        this.arguments = arguments;
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> matches one of the modeled
    /// hierarchyid instance method names. Comparison is ordinal
    /// case-SENSITIVE (probe-confirmed against SQL Server 2025: hierarchyid
    /// methods go through CLR reflection — <c>.getlevel()</c> raises
    /// Msg 6506 even though identifier resolution elsewhere is CI). Used
    /// by the expression parser to decide whether to take the special
    /// method-call path or fall through to multipart-reference handling.
    /// </summary>
    public static bool IsKnownMethodName(string name) =>
        TryGetMethod(name, out _);

    private static bool TryGetMethod(string name, out HierarchyIdMethod method)
    {
        if (name.Equals("GetLevel", StringComparison.Ordinal)) { method = HierarchyIdMethod.GetLevel; return true; }
        if (name.Equals("GetAncestor", StringComparison.Ordinal)) { method = HierarchyIdMethod.GetAncestor; return true; }
        if (name.Equals("GetDescendant", StringComparison.Ordinal)) { method = HierarchyIdMethod.GetDescendant; return true; }
        if (name.Equals("IsDescendantOf", StringComparison.Ordinal)) { method = HierarchyIdMethod.IsDescendantOf; return true; }
        if (name.Equals("ToString", StringComparison.Ordinal)) { method = HierarchyIdMethod.ToStringMethod; return true; }
        method = default;
        return false;
    }

    /// <summary>
    /// Parses <c>expr.MethodName(args)</c>. On entry, cursor sits on the
    /// <c>(</c>; on return, cursor sits on the closing <c>)</c>.
    /// </summary>
    public static HierarchyIdMethodCall Parse(Expression target, string methodName, ParserContext context)
    {
        if (!TryGetMethod(methodName, out var method))
            throw new InvalidOperationException($"{methodName} is not a hierarchyid method.");

        var args = new List<Expression>();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            args.Add(Expression.Parse(context));
            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                args.Add(Expression.Parse(context));
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return new HierarchyIdMethodCall(target, method, [.. args]);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var receiver = this.target.Run(runtime);
        // .ToString() is a method on hierarchyid AND on geography / geometry;
        // the parser dispatches both shapes through this class because the
        // name overlaps. When the receiver turns out to be spatial at runtime,
        // return the instance's WKT directly, the same rendering
        // SpatialMethodCall produces.
        if (this.method == HierarchyIdMethod.ToStringMethod && receiver.Type is SpatialSqlType)
        {
            return receiver.IsNull ? SqlValue.Null(NVarcharSqlType.Get(-1, runtime.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault)) : SqlValue.FromNVarchar(receiver.AsString);
        }
        if (receiver.IsNull)
            return SqlValue.Null(this.ResultType(runtime.Batch));
        if (receiver.Type != SqlType.HierarchyId)
            throw SimulatedSqlException.InvalidHierarchyIdInput($"receiver is {receiver.Type}, not hierarchyid");

        var path = receiver.AsHierarchyId;
        return this.method switch
        {
            HierarchyIdMethod.GetLevel => SqlValue.FromInt16((short)path.Length),
            HierarchyIdMethod.GetAncestor => RunGetAncestor(path, runtime),
            HierarchyIdMethod.GetDescendant => RunGetDescendant(path, runtime),
            HierarchyIdMethod.IsDescendantOf => RunIsDescendantOf(path, runtime),
            HierarchyIdMethod.ToStringMethod => SqlValue.FromNVarchar(HierarchyIdSqlType.PathToString(path)),
            _ => throw new InvalidOperationException($"Unhandled method: {this.method}"),
        };
    }

    private SqlValue RunGetAncestor(int[][] path, RuntimeContext runtime)
    {
        if (this.arguments.Length != 1)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetAncestor expects one argument");
        var depthArg = this.arguments[0].Run(runtime);
        if (depthArg.IsNull)
            return SqlValue.Null(SqlType.HierarchyId);
        var depth = CoerceToInt32(depthArg, "GetAncestor");
        if (depth < 0)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetAncestor depth must be >= 0");
        if (depth > path.Length)
            return SqlValue.Null(SqlType.HierarchyId);
        var remaining = path.Length - depth;
        var ancestor = new int[remaining][];
        Array.Copy(path, ancestor, remaining);
        return SqlValue.FromHierarchyId(ancestor);
    }

    private SqlValue RunGetDescendant(int[][] selfPath, RuntimeContext runtime)
    {
        if (this.arguments.Length != 2)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetDescendant expects two arguments");
        var c1Val = this.arguments[0].Run(runtime);
        var c2Val = this.arguments[1].Run(runtime);
        var c1 = c1Val.IsNull ? null : RequireHierarchyId(c1Val, "GetDescendant child1");
        var c2 = c2Val.IsNull ? null : RequireHierarchyId(c2Val, "GetDescendant child2");

        // Both children must be direct descendants of self (their depth =
        // self.depth + 1) and their prefix must equal self.
        if (c1 is not null && !IsDirectChildOfSelf(selfPath, c1))
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetDescendant child1 is not a direct descendant of self");
        if (c2 is not null && !IsDirectChildOfSelf(selfPath, c2))
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetDescendant child2 is not a direct descendant of self");

        // No constraints: emit self + [1]
        if (c1 is null && c2 is null)
            return AppendSegment(selfPath, [1]);

        // Open-ended above c1: self.<lastLabel(c1) + 1>
        if (c1 is not null && c2 is null)
        {
            var seg = c1[^1];
            var lastLabel = seg[0];
            return AppendSegment(selfPath, [lastLabel + 1]);
        }

        // Open-ended below c2: self.<lastLabel(c2) - 1>
        if (c1 is null && c2 is not null)
        {
            var seg = c2[^1];
            var lastLabel = seg[0];
            return AppendSegment(selfPath, [lastLabel - 1]);
        }

        // Both children present: c1 must be strictly less than c2.
        var seg1 = c1![^1];
        var seg2 = c2![^1];
        var cmp = CompareLabels(seg1, seg2);
        if (cmp >= 0)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetDescendant requires child1 < child2");

        // Look at the last segment's main label (index 0). If they differ by
        // > 1, pick the integer midpoint (matches probe: `/1/`.GetDescendant(`/1/2/`, `/1/4/`) = `/1/3/`).
        if (seg1.Length == 1 && seg2.Length == 1)
        {
            // Both are simple integers (no sub-ordinals).
            if (seg2[0] - seg1[0] > 1)
                return AppendSegment(selfPath, [seg1[0] + 1]);
            // Adjacent → extend c1 with sub-ordinal 1: e.g. /1/.GetDescendant(/1/2/, /1/3/) = /1/2.1/
            return AppendSegment(selfPath, [seg1[0], 1]);
        }

        // For more complex sub-ordinal cases (rare under AW), conservatively
        // extend c1 with [+1] at the deepest sub-ordinal position. Real
        // SQL Server's algorithm here is more subtle but isn't exercised by
        // the AW baseline; the current rule produces a result strictly
        // greater than c1 and (typically) less than c2.
        var extended = new int[seg1.Length + 1];
        Array.Copy(seg1, extended, seg1.Length);
        extended[^1] = 1;
        return AppendSegment(selfPath, extended);
    }

    private SqlValue RunIsDescendantOf(int[][] selfPath, RuntimeContext runtime)
    {
        if (this.arguments.Length != 1)
            throw SimulatedSqlException.InvalidHierarchyIdInput("IsDescendantOf expects one argument");
        var otherVal = this.arguments[0].Run(runtime);
        if (otherVal.IsNull)
            return SqlValue.Null(SqlType.Bit);
        var other = RequireHierarchyId(otherVal, "IsDescendantOf argument");
        return SqlValue.FromBoolean(IsDescendantOrSelf(selfPath, other));
    }

    private static bool IsDescendantOrSelf(int[][] descendant, int[][] ancestor)
    {
        if (descendant.Length < ancestor.Length)
            return false;
        for (var i = 0; i < ancestor.Length; i++)
        {
            if (CompareLabels(descendant[i], ancestor[i]) != 0)
                return false;
        }
        return true;
    }

    private static bool IsDirectChildOfSelf(int[][] selfPath, int[][] child)
    {
        if (child.Length != selfPath.Length + 1)
            return false;
        for (var i = 0; i < selfPath.Length; i++)
        {
            if (CompareLabels(child[i], selfPath[i]) != 0)
                return false;
        }
        return true;
    }

    private static int CompareLabels(int[] left, int[] right)
    {
        var common = Math.Min(left.Length, right.Length);
        for (var i = 0; i < common; i++)
        {
            var cmp = left[i].CompareTo(right[i]);
            if (cmp != 0)
                return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static SqlValue AppendSegment(int[][] selfPath, int[] newSegment)
    {
        var extended = new int[selfPath.Length + 1][];
        Array.Copy(selfPath, extended, selfPath.Length);
        extended[^1] = newSegment;
        return SqlValue.FromHierarchyId(extended);
    }

    private static int[][] RequireHierarchyId(SqlValue value, string context) =>
        value.Type == SqlType.HierarchyId
            ? value.AsHierarchyId
            : throw SimulatedSqlException.InvalidHierarchyIdInput($"{context} must be hierarchyid, got {value.Type}");

    private static int CoerceToInt32(SqlValue value, string context) => value.Type switch
    {
        _ when value.Type == SqlType.Int32 => value.AsInt32,
        _ when value.Type == SqlType.SmallInt => value.AsInt16,
        _ when value.Type == SqlType.TinyInt => value.AsByte,
        _ when value.Type == SqlType.BigInt => checked((int)value.AsInt64),
        _ => throw SimulatedSqlException.InvalidHierarchyIdInput($"{context} requires an integer, got {value.Type}"),
    };

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.ResultType(batch);

    private SqlType ResultType(BatchContext batch) => this.method switch
    {
        HierarchyIdMethod.GetLevel => SqlType.SmallInt,
        HierarchyIdMethod.IsDescendantOf => SqlType.Bit,
        HierarchyIdMethod.ToStringMethod => NVarcharSqlType.Get(4000, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault),
        _ => SqlType.HierarchyId,
    };

    internal override string DebugDisplay() => $"{this.target.DebugDisplay()}.{this.method}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => true;

    internal override void VisitColumnReferences(Action<MultiPartName> visit)
    {
        this.target.VisitColumnReferences(visit);
        foreach (var a in this.arguments)
            a.VisitColumnReferences(visit);
    }
}

/// <summary>
/// The closed set of modeled hierarchyid instance methods.
/// <c>ToStringMethod</c> is named with a suffix to avoid colliding with
/// <see cref="object.ToString"/> in the surrounding C# scope.
/// </summary>
internal enum HierarchyIdMethod : byte
{
    GetLevel,
    GetAncestor,
    GetDescendant,
    IsDescendantOf,
    ToStringMethod,
}
