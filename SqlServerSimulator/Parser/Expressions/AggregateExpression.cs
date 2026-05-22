using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator for the aggregate functions the simulator models.
/// </summary>
internal enum AggregateKind
{
    Count,
    CountBig,
    Sum,
    Avg,
    Max,
    Min,
    Stdev,
    StdevP,
    Var,
    VarP,
    StringAgg,
    ChecksumAgg,
    ApproxCountDistinct,
}

/// <summary>
/// SQL aggregate function call (<c>COUNT</c>, <c>SUM</c>, <c>AVG</c>,
/// <c>MAX</c>, <c>MIN</c>, the statistical family, <c>STRING_AGG</c>,
/// <c>CHECKSUM_AGG</c>, <c>APPROX_COUNT_DISTINCT</c>). Aggregates can't be
/// evaluated row-by-row; the Selection executor detects them in the
/// projection list, creates per-group <see cref="Aggregator"/>
/// state, streams input rows through it, and binds the materialized
/// <see cref="SqlValue"/> back here via <see cref="BindResult"/> before
/// projecting the output row. <see cref="Run"/> just returns the bound
/// value; calling it before binding is a usage error.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Operand"/> is null only for <c>COUNT(*)</c> and
/// <c>COUNT_BIG(*)</c>. <see cref="Separator"/> is non-null only for
/// <c>STRING_AGG</c>. The mutable <see cref="cachedResult"/> is local to
/// one query — Expression instances aren't shared across queries, and
/// query execution is single-threaded, so the mutation doesn't violate
/// any global invariant.
/// </para>
/// </remarks>
internal sealed class AggregateExpression : Expression
{
    public readonly AggregateKind Kind;

    public readonly Expression? Operand;

    public readonly Expression? Separator;

    public readonly bool Distinct;

    /// <summary>
    /// Items from a postfix <c>WITHIN GROUP (ORDER BY ...)</c> clause; null
    /// when no ORDER BY was supplied. Set exactly once during parse, after the
    /// aggregate is constructed and registered (the postfix follows the
    /// closing <c>)</c> of the function call). Only <c>STRING_AGG</c> accepts
    /// this clause; <see cref="Expression.Parse"/>'s outer loop raises
    /// Msg 10757 for any other aggregate kind before reaching the setter.
    /// </summary>
    public IReadOnlyList<OrderBySpec>? OrderBy;

    private SqlValue cachedResult;

    private bool resultBound;

    private AggregateExpression(AggregateKind kind, Expression? operand, bool distinct, Expression? separator)
    {
        this.Kind = kind;
        this.Operand = operand;
        this.Distinct = distinct;
        this.Separator = separator;
    }

    /// <summary>
    /// SQL Server's lowercase function name for this aggregate kind, used in
    /// error messages that quote the offending function (Msg 10757, etc.).
    /// </summary>
    internal string LowerName => this.Kind switch
    {
        AggregateKind.Count => "count",
        AggregateKind.CountBig => "count_big",
        AggregateKind.Sum => "sum",
        AggregateKind.Avg => "avg",
        AggregateKind.Max => "max",
        AggregateKind.Min => "min",
        AggregateKind.Stdev => "stdev",
        AggregateKind.StdevP => "stdevp",
        AggregateKind.Var => "var",
        AggregateKind.VarP => "varp",
        AggregateKind.StringAgg => "string_agg",
        AggregateKind.ChecksumAgg => "checksum_agg",
        AggregateKind.ApproxCountDistinct => "approx_count_distinct",
        _ => throw new InvalidOperationException($"Unknown aggregate kind {this.Kind}."),
    };

    /// <summary>
    /// Convenience overload that auto-registers the new instance with the
    /// parser context's aggregate collector (when one is in scope, e.g.
    /// during a Selection projection / HAVING parse). Lets the executor
    /// learn what aggregates appear without re-walking the expression
    /// trees.
    /// </summary>
    private static AggregateExpression Register(ParserContext context, AggregateExpression expression)
    {
        context.AggregateCollector?.Add(expression);
        return expression;
    }

    /// <summary>
    /// Binds the aggregator's final result so that the next
    /// <see cref="Run"/> call returns it. The Selection executor calls this
    /// once per group after streaming all input rows through the matching
    /// <see cref="Aggregator"/>.
    /// </summary>
    internal void BindResult(SqlValue value)
    {
        this.cachedResult = value;
        this.resultBound = true;
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        this.resultBound ? this.cachedResult : throw new InvalidOperationException("AggregateExpression.Run was called before its result was bound; this indicates the Selection executor didn't recognize it as an aggregate.");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Kind switch
    {
        AggregateKind.Count => SqlType.Int32,
        AggregateKind.CountBig or AggregateKind.ApproxCountDistinct => SqlType.BigInt,
        AggregateKind.ChecksumAgg => SqlType.Int32,
        AggregateKind.Stdev or AggregateKind.StdevP or AggregateKind.Var or AggregateKind.VarP => SqlType.Float,
        AggregateKind.Max or AggregateKind.Min or AggregateKind.StringAgg => this.Operand!.GetSqlType(batch, resolveColumnType),
        AggregateKind.Sum => DeriveSumResultType(this.Operand!.GetSqlType(batch, resolveColumnType)),
        AggregateKind.Avg => DeriveAvgResultType(this.Operand!.GetSqlType(batch, resolveColumnType)),
        _ => throw new InvalidOperationException($"Unknown aggregate kind {this.Kind}."),
    };

    /// <summary>
    /// Maps <c>SUM</c>'s operand type to its result type per SQL Server's
    /// rules: integer family widens to <see cref="SqlType.Int32"/> for
    /// tinyint/smallint and stays at the operand type for int/bigint;
    /// decimal becomes <c>decimal(38, s)</c> preserving scale; float and
    /// money pass through. Probed against SQL Server 2025 — int does NOT
    /// auto-widen to bigint, so an overflowing sum raises Msg 8115.
    /// </summary>
    private static SqlType DeriveSumResultType(SqlType operandType) => operandType switch
    {
        var t when t == SqlType.TinyInt || t == SqlType.SmallInt => SqlType.Int32,
        var t when t == SqlType.Int32 => SqlType.Int32,
        var t when t == SqlType.BigInt => SqlType.BigInt,
        var t when t == SqlType.Float => SqlType.Float,
        var t when t == SqlType.Real => SqlType.Real,
        var t when t == SqlType.Money => SqlType.Money,
        var t when t == SqlType.SmallMoney => SqlType.Money,
        DecimalSqlType d => SqlType.GetDecimal(38, d.scale),
        _ => throw new NotSupportedException($"SUM not supported for {operandType}."),
    };

    /// <summary>
    /// Maps <c>AVG</c>'s operand type to its result type per SQL Server's
    /// rules: integer family rounds-toward-zero in the operand's own type
    /// (<c>AVG(int)</c> → int, truncating); decimal widens to
    /// <c>decimal(38, max(s, 6))</c>; float / real / money pass through.
    /// </summary>
    private static SqlType DeriveAvgResultType(SqlType operandType) => operandType switch
    {
        var t when t == SqlType.TinyInt || t == SqlType.SmallInt => SqlType.Int32,
        var t when t == SqlType.Int32 => SqlType.Int32,
        var t when t == SqlType.BigInt => SqlType.BigInt,
        var t when t == SqlType.Float => SqlType.Float,
        var t when t == SqlType.Real => SqlType.Real,
        var t when t == SqlType.Money => SqlType.Money,
        var t when t == SqlType.SmallMoney => SqlType.SmallMoney,
        DecimalSqlType d => SqlType.GetDecimal(38, (byte)Math.Max((int)d.scale, 6)),
        _ => throw new NotSupportedException($"AVG not supported for {operandType}."),
    };

    /// <summary>
    /// Parses an aggregate function call entered with
    /// <see cref="ParserContext.Token"/> at the first argument (the caller
    /// — <see cref="Expression.ResolveBuiltIn"/> — has already consumed the
    /// opening <c>(</c>). Handles the kind-specific argument shapes:
    /// <c>*</c> for COUNT-family star variants, optional <c>DISTINCT</c>
    /// keyword, two-arg <c>STRING_AGG</c>. Leaves the token at the closing
    /// <c>)</c>; the caller advances past it.
    /// </summary>
    public static AggregateExpression Parse(ParserContext context, AggregateKind kind)
    {
        if (kind == AggregateKind.StringAgg)
            return ParseStringAgg(context);

        // COUNT(*), COUNT_BIG(*) — the only aggregates that accept a bare `*`.
        if (kind is AggregateKind.Count or AggregateKind.CountBig
            && context.Token is Operator { Character: '*' })
        {
            context.MoveNextRequired();
            return Register(context, new AggregateExpression(kind, operand: null, distinct: false, separator: null));
        }

        var distinct = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Distinct })
        {
            distinct = true;
            context.MoveNextRequired();
        }

        // APPROX_COUNT_DISTINCT is implicitly distinct regardless of keyword.
        if (kind == AggregateKind.ApproxCountDistinct)
            distinct = true;

        var operand = Expression.Parse(context);
        return Register(context, new AggregateExpression(kind, operand, distinct, separator: null));
    }

    private static AggregateExpression ParseStringAgg(ParserContext context)
    {
        var operand = Expression.Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var separator = Expression.Parse(context);
        return Register(context, new AggregateExpression(AggregateKind.StringAgg, operand, distinct: false, separator: separator));
    }

    internal override string DebugDisplay()
    {
        var name = this.Kind switch
        {
            AggregateKind.Count => "COUNT",
            AggregateKind.CountBig => "COUNT_BIG",
            AggregateKind.Sum => "SUM",
            AggregateKind.Avg => "AVG",
            AggregateKind.Max => "MAX",
            AggregateKind.Min => "MIN",
            AggregateKind.Stdev => "STDEV",
            AggregateKind.StdevP => "STDEVP",
            AggregateKind.Var => "VAR",
            AggregateKind.VarP => "VARP",
            AggregateKind.StringAgg => "STRING_AGG",
            AggregateKind.ChecksumAgg => "CHECKSUM_AGG",
            AggregateKind.ApproxCountDistinct => "APPROX_COUNT_DISTINCT",
            _ => this.Kind.ToString(),
        };
        var distinct = this.Distinct ? "DISTINCT " : "";
        var operand = this.Operand?.DebugDisplay() ?? "*";
        var separator = this.Separator is null ? "" : $", {this.Separator.DebugDisplay()}";
        return $"{name}({distinct}{operand}{separator})";
    }
}
