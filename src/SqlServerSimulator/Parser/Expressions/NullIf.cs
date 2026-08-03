using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>NULLIF(a, b)</c>: returns NULL when the two arguments are equal,
/// otherwise the first argument. Equivalent to
/// <c>CASE WHEN a = b THEN NULL ELSE a END</c>. Result type is fixed to
/// the first argument's type (probe-confirmed: <c>NULLIF(int, decimal)</c>
/// returns int regardless of which arm wins). Equality uses the same
/// promote-and-compare rule as simple-form CASE / <c>=</c>: NULL on either
/// side yields UNKNOWN, falling through to the ELSE arm and returning the
/// first argument (which is itself NULL when the NULL is on the left).
/// <para>The one place the result is <b>narrower</b> than the first argument's
/// own type is an <c>int</c>-typed integer <b>literal</b> there — see
/// <see cref="NarrowedLiteralType"/>.</para>
/// </summary>
internal sealed class NullIf : Expression
{
    private readonly Expression a;
    private readonly Expression b;

    /// <summary>
    /// The narrowed result type when the first argument is an int-typed
    /// integer literal, else <see langword="null"/>. Depends only on the
    /// syntax tree, so it settles at construction and both
    /// <see cref="Run"/> and <see cref="GetSqlType"/> read the same answer.
    /// </summary>
    private readonly SqlType? narrowedLiteralType;

    private SqlType? cachedResultType;

    public NullIf(ParserContext context)
    {
        this.a = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.b = Parse(context.MoveNextRequiredReturnSelf());
        this.narrowedLiteralType = NarrowedLiteralType(this.a);
    }

    /// <summary>
    /// SQL Server sizes an <c>int</c>-typed integer <b>literal</b> in
    /// <c>NULLIF</c>'s first slot down to the narrowest integer type that
    /// holds its value — <c>tinyint</c> for <c>0</c>..<c>255</c>,
    /// <c>smallint</c> for <c>-32768</c>..<c>32767</c>, <c>int</c> otherwise
    /// — so <c>NULLIF(60, 76)</c> is <c>tinyint</c>, <c>NULLIF(-3, 78)</c> and
    /// <c>NULLIF(300, 4)</c> are <c>smallint</c>, and <c>NULLIF(99999999, 4)</c>
    /// stays <c>int</c>. Probe-confirmed against SQL Server 2025, including
    /// through <c>SELECT … INTO</c>, whose destination column is declared at
    /// the narrowed type.
    /// <para>The rule reads the <b>first argument alone</b>: the second
    /// contributes nothing, whether it is a wider literal
    /// (<c>NULLIF(1, 2147483648)</c> → <c>tinyint</c>), a column, a variable
    /// or a type that doesn't even compare. And it is <c>NULLIF</c>'s own —
    /// the <c>CASE</c> it is defined as, and every sibling value-selecting
    /// form (<c>COALESCE</c> / <c>ISNULL</c> / <c>IIF</c> / <c>CHOOSE</c> /
    /// <c>GREATEST</c> / <c>LEAST</c>), all leave <c>60</c> at <c>int</c>.</para>
    /// <para>Only a written literal narrows, so <c>NULLIF(CAST(60 AS int), 76)</c>
    /// and <c>NULLIF(60 + 0, 76)</c> stay <c>int</c>, and a literal that isn't
    /// int-typed keeps its own type: <c>NULLIF(60.0, 76)</c> is
    /// <c>numeric(3, 1)</c> and <c>NULLIF(2147483648, 1)</c> is
    /// <c>numeric(10, 0)</c>. The <c>-2147483648</c> constant fold reaches
    /// here already folded, so it narrows against int's range like any other
    /// int literal.</para>
    /// </summary>
    private static SqlType? NarrowedLiteralType(Expression first) => IntegerLiteralValue(first) switch
    {
        >= 0 and <= byte.MaxValue => SqlType.TinyInt,
        >= short.MinValue and <= short.MaxValue => SqlType.SmallInt,
        >= int.MinValue and <= int.MaxValue => SqlType.Int32,
        _ => null,
    };

    public override SqlValue Run(RuntimeContext runtime)
    {
        var av = this.a.Run(runtime);
        var bv = this.b.Run(runtime);
        var equal = BooleanExpression.CompareValuesPromoted(av, bv, "equal to", static (l, r) => l.Equals(r));
        if (equal == true)
            return SqlValue.Null(this.narrowedLiteralType ?? this.cachedResultType ?? av.Type);
        // The literal narrowing is a type change, so the surviving value has to
        // follow it — the row encoder rejects a value whose type isn't the one
        // the projection schema declared.
        return this.narrowedLiteralType is { } narrowed && !av.IsNull ? av.CoerceTo(narrowed) : av;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.narrowedLiteralType ?? this.a.GetSqlType(batch, resolveColumnType);
        this.cachedResultType = t;
        return t;
    }

    // NULLIF(a, b) returns a (or NULL), so the result carries a's name.
    internal override bool ResultReportsNumeric => this.a.ResultReportsNumeric;

    // The NULL arm of the CASE this desugars to survives unless real folds the
    // whole call, so `NULLIF(1, 2)` projects NOT NULL (the arms differ, leaving
    // the constant 1) while `NULLIF(1, 1)` and any column-valued spelling stay
    // nullable — probe-confirmed against SQL Server 2025.
    internal override bool ResultIsNullable(NullabilityContext context) =>
        !context.TryFold(this, out var folded) || folded.IsNull;

    internal override string DebugDisplay() => $"NULLIF({this.a.DebugDisplay()}, {this.b.DebugDisplay()})";
}
