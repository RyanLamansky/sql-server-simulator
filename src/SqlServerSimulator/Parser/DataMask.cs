using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// How one output column of a query is masked for a principal without
/// <c>UNMASK</c>: the function its values take and the masked table columns it
/// reads, whose <c>UNMASK</c> grants decide whether it applies at all. Settled
/// while the query compiles — it depends on the plan, not the principal — and
/// applied at the statement's output (<see cref="DataMasking"/>).
/// </summary>
/// <remarks>
/// The propagation rules, probed 2026-09-27 against SQL Server 2025: a bare
/// reference to a masked column carries the column's own function, through a
/// derived table, CTE, view, scalar subquery, parentheses, an alias, a CAST or
/// CONVERT to the column's own type, and <c>NULLIF</c>'s first operand; a
/// <c>CASE</c> or <c>IIF</c> whose masked arms agree on a function carries it
/// to every row, its unmasked arms included, as does a set operation's column;
/// any other expression reading a masked column — arithmetic, a string or
/// conversion function, <c>COALESCE</c> / <c>ISNULL</c>, an aggregate, a value
/// window function, <c>COLLATE</c>, masked arms that disagree — takes
/// <c>default()</c> of its own type. A predicate reads the real value: a
/// <c>WHERE</c>, a join, a <c>CASE WHEN</c> condition, a simple <c>CASE</c>'s
/// input, a window's <c>PARTITION BY</c> / <c>ORDER BY</c> all filter and
/// order on what is stored.
/// </remarks>
internal sealed class DataMask(MaskingFunction function, MaskSource[] sources)
{
    public readonly MaskingFunction Function = function;

    public readonly MaskSource[] Sources = sources;

    /// <summary>
    /// The mask of a masked table column read directly; null for a temp
    /// table's, which its creating session reads unmasked (probed 2026-09-27).
    /// </summary>
    public static DataMask? ForTableColumn(HeapTable table, int columnIndex)
    {
        var column = table.Columns[columnIndex];
        if (column.MaskingFunction is { } function)
            return table.Name.StartsWith('#') ? null : new(function, [new(table, columnIndex + 1)]);

        // A computed column over masked columns reads as default() of its own
        // type, persisted or not.
        if (column.Computed is not { } computed || table.Name.StartsWith('#'))
            return null;
        List<MaskSource>? sources = null;
        computed.VisitColumnReferences(name =>
        {
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (table.Columns[i].MaskingFunction is not null && Collation.Baseline.Equals(table.Columns[i].Name, name.Leaf))
                    (sources ??= []).Add(new(table, i + 1));
            }
        });
        return sources is null ? null : new(MaskingFunction.Default, [.. sources]);
    }

    /// <summary>
    /// The mask <paramref name="expression"/> projects, given each column
    /// reference's own; null when it reads no masked column. A scalar UDF's
    /// call reads what its result does (<see cref="UserFunctionCall.ReturnMask"/>)
    /// unless <paramref name="functionResults"/> is false.
    /// </summary>
    public static DataMask? Of(Expression expression, Func<MultiPartName, DataMask?> columnMask, Func<Expression, SqlType>? typeOf, bool functionResults = true)
    {
        while (true)
        {
            switch (expression)
            {
                case NamedExpression named:
                    expression = named.Inner;
                    continue;
                case Parenthesized parenthesized:
                    expression = parenthesized.Wrapped;
                    continue;
                case AssignmentExpression assignment:
                    expression = assignment.Source;
                    continue;
                case Reference reference:
                    return columnMask(reference.ReferencedName);
                case ScalarSubqueryExpression subquery:
                    return subquery.Inner.ColumnMasks is [{ } inner, ..] ? inner : null;
                case CaseExpression caseExpression:
                    return Combine(caseExpression.ValueArms, columnMask, typeOf, functionResults);
                case Iif iif:
                    return Combine(iif.ValueArms, columnMask, typeOf, functionResults);
                case NullIf nullIf:
                    return Of(nullIf.First, columnMask, typeOf, functionResults) ?? Taint(expression, columnMask, functionResults);
            }
            if (typeOf is not null
                && expression is { ConversionTarget: { } target, PureConversionOperand: { } operand }
                && Taint(operand, columnMask, functionResults) is not null
                && SameDeclaredType(typeOf(operand), target))
            {
                expression = operand;
                continue;
            }
            return Taint(expression, columnMask, functionResults);
        }
    }

    /// <summary>
    /// Masks that meet in one output column — a <c>CASE</c>'s arms, a set
    /// operation's branches: the function the masked ones agree on, else
    /// <c>default()</c>; unmasked arms don't count against agreement.
    /// </summary>
    public static DataMask? Merge(DataMask? left, DataMask? right) =>
        left is null ? right
        : right is null ? left
        : new(SameFunction(left.Function, right.Function) ? left.Function : MaskingFunction.Default, [.. left.Sources, .. right.Sources]);

    private static bool SameFunction(MaskingFunction left, MaskingFunction right) =>
        ReferenceEquals(left, right) || string.Equals(left.Definition, right.Definition, StringComparison.Ordinal);

    private static DataMask? Combine(Expression?[] arms, Func<MultiPartName, DataMask?> columnMask, Func<Expression, SqlType>? typeOf, bool functionResults)
    {
        DataMask? combined = null;
        foreach (var arm in arms)
        {
            if (arm is not null)
                combined = Merge(combined, Of(arm, columnMask, typeOf, functionResults));
        }
        return combined;
    }

    /// <summary>Whether two types are the same declared type, collation aside: <c>varchar(40)</c> and <c>varchar(40)</c>, not <c>varchar(10)</c>.</summary>
    internal static bool SameDeclaredType(SqlType left, SqlType right) =>
        ReferenceEquals(left, right) || (left.GetType() == right.GetType() && string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal));

    /// <summary>
    /// <c>default()</c> over every masked column <paramref name="root"/> reads
    /// for its value; null when it reads none. Predicates are not entered, and
    /// a node that reads some children only for ordering or comparison reports
    /// its value children alone (<see cref="Expression.MaskValueChildren"/>).
    /// </summary>
    private static DataMask? Taint(Expression root, Func<MultiPartName, DataMask?> columnMask, bool functionResults)
    {
        List<MaskSource>? sources = null;
        var shape = new NodeShape();
        var pending = new Stack<ExpressionNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is not Expression expression)
                continue;
            DataMask? found = null;
            switch (expression)
            {
                case Reference reference:
                    found = columnMask(reference.ReferencedName);
                    break;
                case ScalarSubqueryExpression subquery:
                    found = subquery.Inner.ColumnMasks is [{ } inner, ..] ? inner : null;
                    break;
                case VariableReference variable:
                    found = variable.Mask;
                    break;
                case UserFunctionCall call when functionResults:
                    found = call.ReturnMask;
                    break;
            }
            if (found is not null)
                (sources ??= []).AddRange(found.Sources);
            if (expression.MaskValueChildren is { } valueChildren)
            {
                foreach (var child in valueChildren)
                {
                    if (child is not null)
                        pending.Push(child);
                }
                continue;
            }
            shape.Clear();
            expression.Describe(shape);
            foreach (var local in shape.Locals)
            {
                if (local is Selection { ColumnMasks: { } innerMasks })
                {
                    foreach (var innerMask in innerMasks)
                    {
                        if (innerMask is not null)
                            (sources ??= []).AddRange(innerMask.Sources);
                    }
                }
            }
            foreach (var child in shape.ChildNodes)
            {
                if (child is Expression)
                    pending.Push(child);
            }
        }
        return sources is null ? null : new(MaskingFunction.Default, [.. sources]);
    }

    /// <summary>
    /// The masks of <paramref name="expressions"/>, or null when none reads a
    /// masked column. Skips the walk outright while the simulation has never
    /// declared a mask, so a query compiled anywhere else pays nothing.
    /// </summary>
    public static DataMask?[]? OfProjection(BatchContext batch, List<Expression> expressions, Func<MultiPartName, DataMask?> columnMask, Func<Expression, SqlType>? typeOf)
    {
        if (!batch.Connection.Simulation.DeclaresDataMasks)
            return null;
        DataMask?[]? masks = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            var mask = Of(expressions[i], columnMask, typeOf);
            if (expressions[i] is AssignmentExpression { VariableName: var variable } && batch.UdfFrame is { AnalyzesReturnMask: true })
                batch.GetVariableSlot(variable).Mask = mask;
            if (mask is not null)
            {
                MarkErrorScope(expressions[i], mask);
                (masks ??= new DataMask?[expressions.Count])[i] = mask;
                if (expressions[i] is AssignmentExpression assignment)
                    assignment.Mask = mask;
            }
        }
        return masks;
    }

    /// <summary>
    /// Hands <paramref name="mask"/> to each conversion and operator node
    /// computing <paramref name="root"/>'s value, so an error one raises can
    /// hide what it quotes (<see cref="DataMasking.Redacted"/>). Predicates
    /// aren't entered, as <see cref="Taint"/> doesn't enter them.
    /// </summary>
    private static void MarkErrorScope(Expression root, DataMask mask)
    {
        var shape = new NodeShape();
        var pending = new Stack<ExpressionNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case Cast cast:
                    cast.ErrorMask = mask;
                    break;
                case ConvertExpression convert:
                    convert.ErrorMask = mask;
                    break;
                case TwoSidedExpression twoSided:
                    twoSided.ErrorMask = mask;
                    break;
                case not Expression:
                    continue;
            }
            var expression = (Expression)node;
            if (expression.MaskValueChildren is { } valueChildren)
            {
                foreach (var child in valueChildren)
                {
                    if (child is not null)
                        pending.Push(child);
                }
                continue;
            }
            shape.Clear();
            expression.Describe(shape);
            foreach (var child in shape.ChildNodes)
            {
                if (child is Expression)
                    pending.Push(child);
            }
        }
    }
}

/// <summary>
/// One masked table column an output column reads: the table and the column's
/// 1-based position, the <c>minor_id</c> a column-level <c>UNMASK</c> grant
/// names. A table variable's column is checked at database scope alone.
/// </summary>
internal readonly struct MaskSource(HeapTable table, int columnOrdinal)
{
    public readonly HeapTable Table = table;

    public readonly int ColumnOrdinal = columnOrdinal;
}
