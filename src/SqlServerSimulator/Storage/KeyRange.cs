using System.Diagnostics;

namespace SqlServerSimulator.Storage;

/// <summary>
/// One key-space interval of a table's leading key tuple — the resource a
/// key-range lock names. SQL Server anchors a range lock on an index key
/// and lets it cover the gap below that key; the simulator names the
/// interval directly, so a range is <c>(Lower, Upper)</c> over the
/// <see cref="Ordinals"/> tuple, compared lexicographically, with per-side
/// inclusivity and either side optionally unbounded.
/// <para>
/// A bound tuple may be <b>shorter</b> than <see cref="Ordinals"/>: it pins
/// only the components it names, and every deeper value sits inside it. That
/// is what expresses a predicate bounding a leading prefix — <c>a = 1</c>
/// against a key on <c>(a, b)</c> is the one-component interval
/// <c>[(1), (1)]</c>, and <c>a = 1 AND b &gt; 2</c> is <c>((1, 2), (1)]</c>,
/// which admits every <c>b</c> above 2 while excluding every other <c>a</c>.
/// </para>
/// <para>
/// <see cref="Commons"/> holds, per ordinal, the promoted type that ordinal's
/// bounds and a probing row's column value are compared in —
/// <see cref="SqlType.Promote"/> of the column's declared type against the
/// predicate's value type, so widening a stored column value into it is always
/// lossless.
/// </para>
/// <para>
/// Equality (which interns the range in <see cref="HeapTable.KeyRangeLocks"/>)
/// covers the ordinals and both bounds but not <see cref="Commons"/>: two
/// ranges whose bounds compare equal cover the same values whatever type
/// unified them, and keying on the type as well would mint a fresh
/// <see cref="LockResource"/> per parse of the same predicate.
/// </para>
/// </summary>
internal readonly struct KeyRange : IEquatable<KeyRange>
{
    /// <summary>Storage ordinals this range constrains, in key order.</summary>
    public readonly int[] Ordinals;

    /// <summary>Per-ordinal type its bounds and every probe value compare in.</summary>
    public readonly SqlType[] Commons;

    /// <summary>
    /// Lower-bound tuple, empty when the range runs to negative infinity and
    /// never longer than <see cref="Ordinals"/>.
    /// </summary>
    public readonly SqlValue[] Lower;

    /// <summary>
    /// Upper-bound tuple, empty when the range runs to positive infinity and
    /// never longer than <see cref="Ordinals"/>.
    /// </summary>
    public readonly SqlValue[] Upper;

    /// <summary>Whether <see cref="Lower"/> is itself inside the range.</summary>
    public readonly bool LowerInclusive;

    /// <summary>Whether <see cref="Upper"/> is itself inside the range.</summary>
    public readonly bool UpperInclusive;

    public KeyRange(
        int[] ordinals,
        SqlType[] commons,
        SqlValue[] lower,
        bool lowerInclusive,
        SqlValue[] upper,
        bool upperInclusive)
    {
        Debug.Assert(ordinals.Length > 0, "A key range names at least one column.");
        Debug.Assert(commons.Length == ordinals.Length, "One comparison type per ranged ordinal.");
        Debug.Assert(lower.Length <= ordinals.Length, "A bound tuple can't be deeper than the ranged ordinals.");
        Debug.Assert(upper.Length <= ordinals.Length, "A bound tuple can't be deeper than the ranged ordinals.");
        this.Ordinals = ordinals;
        this.Commons = commons;
        this.Lower = lower;
        this.Upper = upper;
        this.LowerInclusive = lowerInclusive;
        this.UpperInclusive = upperInclusive;
    }

    /// <summary>
    /// True when <paramref name="probe"/> — one value per entry of
    /// <see cref="Ordinals"/>, each decoded straight from a row image — falls
    /// inside this range. A NULL in a component the comparison reaches is
    /// never inside (no comparison predicate is true for it, so no row
    /// carrying one can be a phantom for the reader that took this range). A
    /// value that won't widen into its <see cref="Commons"/> entry, or won't
    /// compare against the bounds, reports <c>true</c>: the caller treats an
    /// undecidable probe as a conflict rather than letting a possible phantom
    /// through.
    /// </summary>
    public bool Contains(ReadOnlySpan<SqlValue> probe)
    {
        try
        {
            if (this.Lower.Length != 0)
            {
                if (!this.TryCompare(probe, this.Lower, out var low))
                    return false;
                if (low < 0 || (low == 0 && !this.LowerInclusive))
                    return false;
            }
            if (this.Upper.Length != 0)
            {
                if (!this.TryCompare(probe, this.Upper, out var high))
                    return false;
                if (high > 0 || (high == 0 && !this.UpperInclusive))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or SimulatedSqlException)
        {
            return true;
        }
    }

    // Lexicographic comparison of the probe tuple against one bound tuple. A
    // bound that runs out before the ordinals do compares equal from there on,
    // which is what makes a shorter bound pin only its own components. False
    // when a component the walk reaches is NULL — the row can't satisfy the
    // predicate the range came from, so it isn't inside.
    private bool TryCompare(ReadOnlySpan<SqlValue> probe, SqlValue[] bound, out int result)
    {
        result = 0;
        for (var i = 0; i < bound.Length; i++)
        {
            if (probe[i].IsNull)
                return false;
            var value = probe[i].CoerceTo(this.Commons[i]);
            if (value.IsNull)
                return false;
            result = value.CompareTo(bound[i]);
            if (result != 0)
                return true;
        }
        return true;
    }

    public bool Equals(KeyRange other) =>
        this.LowerInclusive == other.LowerInclusive
        && this.UpperInclusive == other.UpperInclusive
        && this.Ordinals.AsSpan().SequenceEqual(other.Ordinals)
        && BoundsEqual(this.Lower, other.Lower)
        && BoundsEqual(this.Upper, other.Upper);

    public override bool Equals(object? obj) => obj is KeyRange other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.LowerInclusive);
        hash.Add(this.UpperInclusive);
        foreach (var ordinal in this.Ordinals)
            hash.Add(ordinal);
        hash.Add(this.Lower.Length);
        foreach (var value in this.Lower)
            hash.Add(value);
        hash.Add(this.Upper.Length);
        foreach (var value in this.Upper)
            hash.Add(value);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The <c>resource_description</c> <c>sys.dm_tran_locks</c> reports for
    /// this range: the ranged storage ordinals, then the interval in standard
    /// bracket notation with <c>*</c> for an unbounded side and for a
    /// component a shorter bound tuple leaves open. Real SQL Server prints a
    /// hash of the anchoring index key there instead, so this doesn't
    /// byte-match — it names the same thing readably.
    /// </summary>
    public override string ToString() =>
        $"{string.Join(',', this.Ordinals)}:{(this.LowerInclusive && this.Lower.Length != 0 ? '[' : '(')}"
        + $"{this.Render(this.Lower)},{this.Render(this.Upper)}"
        + $"{(this.UpperInclusive && this.Upper.Length != 0 ? ']' : ')')}";

    private static bool BoundsEqual(SqlValue[] left, SqlValue[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }
        return true;
    }

    // Bound text for ToString: `*` for an unbounded side, the lone value for a
    // single-column range, and a parenthesized tuple otherwise, padded with `*`
    // wherever a shorter bound leaves a component open.
    private string Render(SqlValue[] bound)
    {
        if (bound.Length == 0)
            return "*";
        if (this.Ordinals.Length == 1)
            return RenderValue(bound[0]);
        var parts = new string[this.Ordinals.Length];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = i < bound.Length ? RenderValue(bound[i]) : "*";
        return $"({string.Join(',', parts)})";
    }

    // The CLR projection of one bound value. A type with no object projection
    // falls back to its own name — the description is diagnostic, never parsed
    // back.
    private static string RenderValue(SqlValue bound)
    {
        try
        {
            return bound.ToObject()?.ToString() ?? "NULL";
        }
        catch (NotSupportedException)
        {
            return bound.Type.ToString() ?? "?";
        }
    }
}
