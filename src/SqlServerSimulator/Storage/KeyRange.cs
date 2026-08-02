using System.Diagnostics;

namespace SqlServerSimulator.Storage;

/// <summary>
/// One key-space interval of a single table column — the resource a
/// key-range lock names. SQL Server anchors a range lock on an index key
/// and lets it cover the gap below that key; the simulator names the
/// interval directly, so a range is <c>(Lower, Upper)</c> with per-side
/// inclusivity and either side optionally unbounded.
/// <para>
/// <see cref="Common"/> is the promoted type both the range bounds and a
/// probing row's column value are compared in — <see cref="SqlType.Promote"/>
/// of the column's declared type against the predicate's value type, so
/// widening a stored column value into it is always lossless.
/// </para>
/// <para>
/// Equality (which interns the range in <see cref="HeapTable.KeyRangeLocks"/>)
/// covers the ordinal and both bounds but not <see cref="Common"/>: two
/// ranges whose bounds compare equal cover the same values whatever type
/// unified them, and keying on the type as well would mint a fresh
/// <see cref="LockResource"/> per parse of the same predicate.
/// </para>
/// </summary>
internal readonly struct KeyRange : IEquatable<KeyRange>
{
    private const int OrdinalMask = 0x3FF;
    private const int HasLowerBit = 1 << 10;
    private const int LowerInclusiveBit = 1 << 11;
    private const int HasUpperBit = 1 << 12;
    private const int UpperInclusiveBit = 1 << 13;

    // Ordinal plus the four bound flags in one 16-bit word: a storage ordinal
    // indexes a table capped at 1024 columns, so ten bits hold it and the flags
    // ride above. Equality and hashing then compare the ordinal and all four
    // flags as a single integer.
    private readonly ushort packed;

    /// <summary>Type both bounds and every probe value are compared in.</summary>
    public readonly SqlType Common;

    /// <summary>Lower bound, meaningful only when <see cref="HasLower"/>.</summary>
    public readonly SqlValue Lower;

    /// <summary>Upper bound, meaningful only when <see cref="HasUpper"/>.</summary>
    public readonly SqlValue Upper;

    public KeyRange(
        int ordinal,
        SqlType common,
        bool hasLower,
        SqlValue lower,
        bool lowerInclusive,
        bool hasUpper,
        SqlValue upper,
        bool upperInclusive)
    {
        // A tripwire for the ten-bit ordinal field: the column cap that bounds
        // it is SQL Server's, not something the simulator enforces, so a
        // widened table would silently alias one range onto another's resource.
        Debug.Assert((uint)ordinal <= OrdinalMask, $"Storage ordinal {ordinal} does not fit the packed ordinal field.");
        this.packed = (ushort)(
            (ordinal & OrdinalMask)
            | (hasLower ? HasLowerBit : 0)
            | (lowerInclusive ? LowerInclusiveBit : 0)
            | (hasUpper ? HasUpperBit : 0)
            | (upperInclusive ? UpperInclusiveBit : 0));
        this.Common = common;
        this.Lower = lower;
        this.Upper = upper;
    }

    /// <summary>Storage ordinal of the column this range constrains.</summary>
    public int Ordinal => this.packed & OrdinalMask;

    /// <summary>False when the range runs to negative infinity.</summary>
    public bool HasLower => (this.packed & HasLowerBit) != 0;

    /// <summary>Whether <see cref="Lower"/> is itself inside the range.</summary>
    public bool LowerInclusive => (this.packed & LowerInclusiveBit) != 0;

    /// <summary>False when the range runs to positive infinity.</summary>
    public bool HasUpper => (this.packed & HasUpperBit) != 0;

    /// <summary>Whether <see cref="Upper"/> is itself inside the range.</summary>
    public bool UpperInclusive => (this.packed & UpperInclusiveBit) != 0;

    /// <summary>
    /// The degenerate range covering exactly one value — what an equality
    /// predicate (<c>col = @v</c>, or one arm of an <c>IN</c> list) locks.
    /// </summary>
    public static KeyRange Point(int ordinal, SqlType common, SqlValue value) =>
        new(ordinal, common, hasLower: true, value, lowerInclusive: true, hasUpper: true, value, upperInclusive: true);

    /// <summary>
    /// True when <paramref name="probe"/> — a column value decoded straight
    /// from a row image — falls inside this range. A NULL is never inside
    /// (no comparison predicate is true for it, so no row carrying one can
    /// be a phantom for the reader that took this range). A value that
    /// won't widen into <see cref="Common"/>, or won't compare against the
    /// bounds, reports <c>true</c>: the caller treats an undecidable probe
    /// as a conflict rather than letting a possible phantom through.
    /// </summary>
    public bool Contains(SqlValue probe)
    {
        if (probe.IsNull)
            return false;
        try
        {
            var value = probe.CoerceTo(this.Common);
            if (value.IsNull)
                return false;
            if (this.HasLower)
            {
                var low = value.CompareTo(this.Lower);
                if (low < 0 || (low == 0 && !this.LowerInclusive))
                    return false;
            }
            if (this.HasUpper)
            {
                var high = value.CompareTo(this.Upper);
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

    public bool Equals(KeyRange other) =>
        this.packed == other.packed
        && (!this.HasLower || this.Lower.Equals(other.Lower))
        && (!this.HasUpper || this.Upper.Equals(other.Upper));

    public override bool Equals(object? obj) => obj is KeyRange other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.packed);
        if (this.HasLower)
            hash.Add(this.Lower);
        if (this.HasUpper)
            hash.Add(this.Upper);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The <c>resource_description</c> <c>sys.dm_tran_locks</c> reports for
    /// this range: the column's storage ordinal, then the interval in
    /// standard bracket notation with <c>*</c> for an unbounded side. Real
    /// SQL Server prints a hash of the anchoring index key there instead,
    /// so this doesn't byte-match — it names the same thing readably.
    /// </summary>
    public override string ToString() =>
        $"{this.Ordinal}:{(this.LowerInclusive && this.HasLower ? '[' : '(')}"
        + $"{Render(this.HasLower, this.Lower)},{Render(this.HasUpper, this.Upper)}"
        + $"{(this.UpperInclusive && this.HasUpper ? ']' : ')')}";

    // Bound text for ToString: the CLR projection of the value, or `*` for an
    // unbounded side. A type with no object projection falls back to its own
    // name — the description is diagnostic, never parsed back.
    private static string Render(bool present, SqlValue bound)
    {
        if (!present)
            return "*";
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
