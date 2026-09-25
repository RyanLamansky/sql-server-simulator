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
/// The method-name set is a closed accept-list:
/// <c>GetLevel</c>, <c>GetAncestor</c>, <c>GetDescendant</c>,
/// <c>GetReparentedValue</c>, <c>IsDescendantOf</c>, <c>ToString</c>. The closed-list shape means an
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

    /// <remarks>
    /// A <c>switch</c> over string constants is an ordinal match, which is
    /// the comparison this accept-list wants, and it reaches the answer in
    /// one length-and-character dispatch rather than one compare per name —
    /// worth the shape here because <see cref="Expression.ParsePostfix"/>
    /// asks for every <c>.</c>-qualified name it parses, nearly all of which
    /// are ordinary multipart references that match nothing.
    /// </remarks>
    private static bool TryGetMethod(string name, out HierarchyIdMethod method)
    {
        switch (name)
        {
            case "GetAncestor": method = HierarchyIdMethod.GetAncestor; return true;
            case "GetDescendant": method = HierarchyIdMethod.GetDescendant; return true;
            case "GetLevel": method = HierarchyIdMethod.GetLevel; return true;
            case "GetReparentedValue": method = HierarchyIdMethod.GetReparentedValue; return true;
            case "IsDescendantOf": method = HierarchyIdMethod.IsDescendantOf; return true;
            case "ToString": method = HierarchyIdMethod.ToStringMethod; return true;
            default: method = default; return false;
        }
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
            HierarchyIdMethod.GetReparentedValue => RunGetReparentedValue(path, runtime),
            HierarchyIdMethod.ToStringMethod => SqlValue.FromNVarchar(HierarchyIdSqlType.PathToString(path)),
            _ => throw new InvalidOperationException($"Unhandled method: {this.method}"),
        };
    }

    private SqlValue RunGetAncestor(long[][] path, RuntimeContext runtime)
    {
        if (this.arguments.Length != 1)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetAncestor expects one argument");
        var depthArg = this.arguments[0].Run(runtime);
        if (depthArg.IsNull)
            return SqlValue.Null(SqlType.HierarchyId);
        var depth = depthArg.CoerceTo(SqlType.Int32).AsInt32;
        if (depth < 0)
            throw SimulatedSqlException.HierarchyIdNegativeAncestor();
        if (depth > path.Length)
            return SqlValue.Null(SqlType.HierarchyId);
        var remaining = path.Length - depth;
        var ancestor = new long[remaining][];
        Array.Copy(path, ancestor, remaining);
        return SqlValue.FromHierarchyId(ancestor);
    }

    private SqlValue RunGetDescendant(long[][] selfPath, RuntimeContext runtime)
    {
        if (this.arguments.Length != 2)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetDescendant expects two arguments");
        var c1Val = this.arguments[0].Run(runtime);
        var c2Val = this.arguments[1].Run(runtime);
        var c1 = c1Val.IsNull ? null : AsPath(c1Val);
        var c2 = c2Val.IsNull ? null : AsPath(c2Val);

        // Both children must be direct descendants of self (their depth =
        // self.depth + 1) and their prefix must equal self.
        if (c1 is not null && !IsDirectChildOfSelf(selfPath, c1))
            throw SimulatedSqlException.HierarchyIdDescendantNotAChild("child1", HierarchyIdSqlType.PathToString(c1), HierarchyIdSqlType.PathToString(selfPath));
        if (c2 is not null && !IsDirectChildOfSelf(selfPath, c2))
            throw SimulatedSqlException.HierarchyIdDescendantNotAChild("child2", HierarchyIdSqlType.PathToString(c2), HierarchyIdSqlType.PathToString(selfPath));

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
            throw SimulatedSqlException.HierarchyIdDescendantOutOfOrder(HierarchyIdSqlType.PathToString(c1), HierarchyIdSqlType.PathToString(c2));

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
        var extended = new long[seg1.Length + 1];
        Array.Copy(seg1, extended, seg1.Length);
        extended[^1] = 1;
        return AppendSegment(selfPath, extended);
    }

    private SqlValue RunIsDescendantOf(long[][] selfPath, RuntimeContext runtime)
    {
        if (this.arguments.Length != 1)
            throw SimulatedSqlException.InvalidHierarchyIdInput("IsDescendantOf expects one argument");
        var otherVal = this.arguments[0].Run(runtime);
        if (otherVal.IsNull)
            return SqlValue.Null(SqlType.Bit);
        return SqlValue.FromBoolean(IsDescendantOrSelf(selfPath, AsPath(otherVal)));
    }

    /// <summary>
    /// <c>GetReparentedValue(oldRoot, newRoot)</c>: the receiver's path with its
    /// <c>oldRoot</c> prefix replaced by <c>newRoot</c>. NULL for a NULL
    /// argument; an <c>oldRoot</c> the receiver doesn't descend from (or equal)
    /// is real's 24009 (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    private SqlValue RunGetReparentedValue(long[][] selfPath, RuntimeContext runtime)
    {
        if (this.arguments.Length != 2)
            throw SimulatedSqlException.InvalidHierarchyIdInput("GetReparentedValue expects two arguments");
        var oldRootVal = this.arguments[0].Run(runtime);
        var newRootVal = this.arguments[1].Run(runtime);
        if (oldRootVal.IsNull || newRootVal.IsNull)
            return SqlValue.Null(SqlType.HierarchyId);
        var oldRoot = AsPath(oldRootVal);
        var newRoot = AsPath(newRootVal);
        if (!IsDescendantOrSelf(selfPath, oldRoot))
            throw SimulatedSqlException.HierarchyIdReparentNotAnAncestor(HierarchyIdSqlType.PathToString(oldRoot), HierarchyIdSqlType.PathToString(selfPath));
        var reparented = new long[newRoot.Length + selfPath.Length - oldRoot.Length][];
        Array.Copy(newRoot, reparented, newRoot.Length);
        Array.Copy(selfPath, oldRoot.Length, reparented, newRoot.Length, selfPath.Length - oldRoot.Length);
        return SqlValue.FromHierarchyId(reparented);
    }

    private static bool IsDescendantOrSelf(long[][] descendant, long[][] ancestor)
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

    private static bool IsDirectChildOfSelf(long[][] selfPath, long[][] child)
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

    private static int CompareLabels(long[] left, long[] right)
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

    private static SqlValue AppendSegment(long[][] selfPath, long[] newSegment)
    {
        var extended = new long[selfPath.Length + 1][];
        Array.Copy(selfPath, extended, selfPath.Length);
        extended[^1] = newSegment;
        return SqlValue.FromHierarchyId(extended);
    }

    /// <summary>
    /// A hierarchyid argument's path, converted as an assignment would — a
    /// string parses (<c>@h.GetDescendant('/1/3/', NULL)</c>), and a type that
    /// can't convert was refused while binding.
    /// </summary>
    private static long[][] AsPath(SqlValue value) => value.CoerceTo(SqlType.HierarchyId).AsHierarchyId;

    /// <summary>
    /// Binds each argument to its parameter's type as an assignment would, so
    /// a type that can't convert is real's Msg 206 and a string that can't
    /// read as an integer its Msg 245 (probed 2026-09-25).
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        SqlType parameter = this.method == HierarchyIdMethod.GetAncestor ? SqlType.Int32 : SqlType.HierarchyId;
        foreach (var argument in this.arguments)
            _ = AssignmentRules.ArgumentType(argument, parameter, batch, resolveColumnType);
        return this.ResultType(batch);
    }

    private SqlType ResultType(BatchContext batch) => this.method switch
    {
        HierarchyIdMethod.GetLevel => SqlType.SmallInt,
        HierarchyIdMethod.IsDescendantOf => SqlType.Bit,
        HierarchyIdMethod.ToStringMethod => NVarcharSqlType.Get(4000, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault),
        _ => SqlType.HierarchyId,
    };

    internal override string DebugDisplay() => $"{this.target.DebugDisplay()}.{this.method}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    internal override void Describe(NodeShape shape) => shape.Local(this.method).Child(this.target).Children(this.arguments);

    internal override bool ResultIsNullable(NullabilityContext context) => true;
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
    GetReparentedValue,
    IsDescendantOf,
    ToStringMethod,
}
