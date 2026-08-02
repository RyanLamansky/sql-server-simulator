using System.Collections.Concurrent;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// The collation an expression carries once a producing operator failed to
/// settle one — SQL Server's <c>No collation</c> coercibility label made
/// concrete. It rides on the string <see cref="SqlType"/> alongside
/// <see cref="Coercibility.NoCollation"/> and remembers the conflicting pair
/// plus the operator that produced it, which is what the downstream
/// Msg 451 / 446 / 456 messages name.
/// </summary>
/// <remarks>
/// Every member delegates to <paramref name="inner"/> — the left-hand operand's
/// collation — so a marker that escapes the modeled consumer set degrades to
/// ordinary comparison behavior rather than crashing. Instances intern per
/// (right, left, operator) triple because a producing operator runs per row and
/// the result type it builds is an intern-cache key.
/// </remarks>
/// <param name="inner">The collation whose comparison behavior stands in while the conflict is unresolved.</param>
/// <param name="rightName">The right-hand operand's collation name, which real names first.</param>
/// <param name="leftName">The left-hand operand's collation name.</param>
/// <param name="operatorName">The producing operator, spelled as real spells it (<c>add</c> / <c>concat</c> / <c>CASE</c> / <c>UNION ALL</c>).</param>
internal sealed class UnresolvedCollation(Collation inner, string rightName, string leftName, string operatorName) : Collation
{
    /// <summary>The right-hand operand's collation name — the one real names first in every conflict message.</summary>
    public readonly string RightName = rightName;

    /// <summary>The left-hand operand's collation name.</summary>
    public readonly string LeftName = leftName;

    /// <summary>The operator that couldn't settle the pair, spelled as real spells it in the message.</summary>
    public readonly string OperatorName = operatorName;

    private readonly Collation inner = inner;

    private static readonly ConcurrentDictionary<(string Right, string Left, string Operator), UnresolvedCollation> interned = new();

    /// <summary>
    /// The interned marker for a conflict between <paramref name="right"/> and
    /// <paramref name="left"/> produced by <paramref name="operatorName"/>.
    /// </summary>
    internal static UnresolvedCollation For(Collation right, Collation left, string operatorName) =>
        interned.GetOrAdd(
            (right.Name, left.Name, operatorName),
            static (_, state) => new UnresolvedCollation(state.Left, state.Right.Name, state.Left.Name, state.Operator),
            (Right: right, Left: left, Operator: operatorName));

    /// <summary>
    /// Rewraps <paramref name="type"/> so it carries this marker at
    /// <see cref="Coercibility.NoCollation"/>. Non-string types are returned
    /// unchanged (they have nothing to mark).
    /// </summary>
    internal SqlType Mark(SqlType type) => type.WithCollation(this, Coercibility.NoCollation);

    /// <summary>
    /// The marker <paramref name="type"/> carries, or <see langword="null"/>
    /// when its collation is settled. The pair
    /// (<see cref="Coercibility.NoCollation"/>, <see cref="UnresolvedCollation"/>)
    /// is always set together, but the type check is what narrows for callers.
    /// </summary>
    internal static UnresolvedCollation? On(SqlType type) =>
        type.Coercibility == Coercibility.NoCollation ? type.Collation as UnresolvedCollation : null;

    /// <summary>
    /// The collation the string-producing operators — <c>+</c>, <c>||</c>,
    /// <c>CASE</c>'s arm unification and <c>UNION ALL</c>'s per-column
    /// unification — settle on for a two-operand step, stamped onto
    /// <paramref name="resultType"/>. Real's rules, all probe-confirmed
    /// against SQL Server 2025:
    /// <list type="bullet">
    /// <item>Two <see cref="Coercibility.Explicit"/> operands that disagree are
    /// the operator's own <b>Msg 468</b>, whatever the result family.</item>
    /// <item>An operand that already carries an unresolved collation makes the
    /// result carry it too — except where that operand is a code-page-bearing
    /// <c>varchar</c>, which can't be converted without one and takes
    /// <b>Msg 456</b> naming the <em>producing</em> operator.</item>
    /// <item>Otherwise an unresolvable pair yields the marker for an
    /// <c>nvarchar</c> result and <b>Msg 457</b> for a <c>varchar</c> one.</item>
    /// </list>
    /// </summary>
    /// <param name="resultType">The type the operator produces, before its collation is stamped.</param>
    /// <param name="left">The left operand's type.</param>
    /// <param name="right">The right operand's type.</param>
    /// <param name="operatorName">This operator's name as real spells it in the message.</param>
    internal static SqlType Settle(SqlType resultType, SqlType left, SqlType right, string operatorName)
    {
        if ((On(left) ?? On(right)) is { } inherited)
        {
            var carrier = On(left) is null ? right : left;
            return SqlType.IsNationalStringCategory(carrier)
                ? inherited.Mark(resultType)
                : throw SimulatedSqlException.UnresolvedCollationReachedImplicitConversion(
                    carrier, resultType, inherited.RightName, inherited.LeftName, inherited.OperatorName);
        }

        if (Collation.Resolve(left, right) is { } resolved)
            return resultType.WithCollation(resolved.Collation, resolved.Coercibility);

        var rightName = right.Collation!.Name;
        var leftName = left.Collation!.Name;
        return left.Coercibility == Coercibility.Explicit && right.Coercibility == Coercibility.Explicit
            ? throw SimulatedSqlException.CollationConflict(rightName, leftName, operatorName)
            : SqlType.IsNationalStringCategory(resultType)
                ? For(right.Collation, left.Collation, operatorName).Mark(resultType)
                : throw SimulatedSqlException.UnresolvedCollationInImplicitConversion(
                    resultType, rightName, leftName, operatorName);
    }

    /// <summary>
    /// Raises <b>Msg 4191</b> naming <paramref name="operationName"/> when
    /// <paramref name="type"/> still carries an unresolved collation. The gate
    /// every operation that needs a definite collation to do its work runs —
    /// the string scalars, the comparison operators, <c>LIKE</c>, and the
    /// <c>MAX</c> / <c>MIN</c> / <c>STRING_AGG</c> aggregates.
    /// </summary>
    internal static void Require(SqlType type, string operationName)
    {
        if (type.Coercibility == Coercibility.NoCollation)
            throw SimulatedSqlException.UnresolvedCollationForOperation(operationName);
    }

    /// <summary>
    /// Raises <b>Msg 456</b> when an unresolved collation reaches an assignment
    /// target — an <c>INSERT … SELECT</c> source column, a <c>SELECT @v = …</c>
    /// item, an <c>UPDATE</c>'s <c>SET</c> value. Which family raises is the
    /// <em>source</em>'s: the target settles an <c>nvarchar</c> conflict
    /// silently, while a <c>varchar</c> whose collation never resolved has
    /// bytes in no known code page and is refused wherever it lands, an
    /// <c>nvarchar</c> destination included (probe-confirmed against SQL
    /// Server 2025).
    /// </summary>
    /// <remarks>
    /// The message names the destination type, which this seam doesn't carry —
    /// the source's own name stands in, so the wording is exact for the
    /// same-family assignment and names the wrong destination for the
    /// cross-family one.
    /// </remarks>
    internal static void RequireAssignable(SqlType type)
    {
        if (!SqlType.IsNationalStringCategory(type) && On(type) is { } conflict)
        {
            throw SimulatedSqlException.UnresolvedCollationReachedImplicitConversion(
                type, type, conflict.RightName, conflict.LeftName, conflict.OperatorName);
        }
    }

    /// <summary>
    /// <see cref="Require(SqlType, string)"/> over a pair, for the two-operand
    /// sites (comparison, <c>LIKE</c>, the searching scalars) where either side
    /// can be the one carrying the conflict.
    /// </summary>
    internal static void Require(SqlType left, SqlType right, string operationName)
    {
        Require(left, operationName);
        Require(right, operationName);
    }

    public override string Name => this.inner.Name;

    public override string Description => this.inner.Description;

    public override bool CaseSensitive => this.inner.CaseSensitive;

    public override int Compare(string? x, string? y) => this.inner.Compare(x, y);

    public override bool Equals(string? x, string? y) => this.inner.Equals(x, y);

    public override int GetHashCode(string obj) => this.inner.GetHashCode(obj);

    // No storage substitution: a marker's comparison behavior is a fallback for
    // a value that shouldn't reach a comparer at all, so the binary-collation
    // varchar bodies buy nothing here and swapping one in would allocate a
    // second marker per column-pin.
    internal override Collation ForVarcharStorage() => this;

    internal override bool IsSupplementaryCharacterAware => this.inner.IsSupplementaryCharacterAware;

    internal override Encoding StorageEncoding => this.inner.StorageEncoding;

    internal override int AnsiCodePage => this.inner.AnsiCodePage;
}
