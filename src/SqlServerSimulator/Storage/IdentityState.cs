namespace SqlServerSimulator.Storage;

/// <summary>
/// Per-column runtime state for an <c>IDENTITY(seed, increment)</c> column:
/// configured seed/increment plus the high-water mark used to compute the
/// next auto-generated value and to answer <c>IDENT_CURRENT</c>.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server's identity semantics: each insert that omits the identity column
/// generates a new value by advancing the high-water mark by <see cref="Increment"/>.
/// An explicit insert (under <c>SET IDENTITY_INSERT ... ON</c>) advances the
/// high-water mark only when the explicit value is "past" the current mark in
/// the increment's direction; smaller values pass through without reseeding.
/// <c>IDENT_CURRENT</c> returns the high-water mark, falling back to <see cref="Seed"/>
/// when no value has yet been generated.
/// </para>
/// </remarks>
internal sealed class IdentityState(long seed, long increment, bool notForReplication = false)
{
    public readonly long Seed = seed;

    public readonly long Increment = increment;

    /// <summary>
    /// True when the column was declared <c>IDENTITY(seed, increment) NOT FOR
    /// REPLICATION</c>. Replication isn't modeled, so this carries no runtime
    /// effect (real SQL Server skips identity reseeding under replication
    /// agents) — it exists purely to round-trip through
    /// <c>sys.identity_columns.is_not_for_replication</c> /
    /// <c>COLUMNPROPERTY(…, 'IsIdNotForRepl')</c> and the BACPAC model.
    /// </summary>
    public readonly bool NotForReplication = notForReplication;

    private long? highWaterMark;

    private readonly Lock gate = new();

    /// <summary>
    /// The value <c>IDENT_CURRENT</c> reports: the last generated/observed
    /// identity, or the seed when no value has yet been generated.
    /// </summary>
    public long Current
    {
        get
        {
            lock (this.gate)
                return this.highWaterMark ?? this.Seed;
        }
    }

    /// <summary>
    /// Generates and reserves the next auto-incremented value, raising
    /// <see cref="OverflowException"/> without reserving it when it falls
    /// outside <paramref name="columnType"/> — real leaves <c>IDENT_CURRENT</c>
    /// at the last value that fit (probed 2026-09-24 against SQL Server 2025).
    /// </summary>
    public long GenerateNext(SqlType columnType)
    {
        lock (this.gate)
        {
            var next = this.highWaterMark is long last
                ? checked(last + this.Increment)
                : this.Seed;
            if (!Fits(next, columnType))
                throw new OverflowException();
            this.highWaterMark = next;
            return next;
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> can carry an identity: the four
    /// integer types, or <c>decimal</c> / <c>numeric</c> of scale 0.
    /// </summary>
    public static bool IsIdentityType(SqlType type) =>
        type is TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType or DecimalSqlType { scale: 0 };

    /// <summary>Whether <paramref name="value"/> lies in identity type <paramref name="type"/>'s range.</summary>
    public static bool Fits(Int128 value, SqlType type) => type switch
    {
        TinyIntSqlType => value >= byte.MinValue && value <= byte.MaxValue,
        SmallIntSqlType => value >= short.MinValue && value <= short.MaxValue,
        Int32SqlType => value >= int.MinValue && value <= int.MaxValue,
        BigIntSqlType => value >= long.MinValue && value <= long.MaxValue,
        DecimalSqlType d => Int128.Abs(value) < PowerOfTen(d.precision),
        _ => false,
    };

    private static Int128 PowerOfTen(int exponent)
    {
        Int128 power = 1;
        for (var i = 0; i < exponent; i++)
            power *= 10;
        return power;
    }

    /// <summary>
    /// Records an explicit value supplied under <c>IDENTITY_INSERT ON</c>;
    /// advances the high-water mark only when <paramref name="value"/> is
    /// past the current mark in the <see cref="Increment"/> direction.
    /// </summary>
    public void ObserveExplicit(long value)
    {
        lock (this.gate)
        {
            if (this.highWaterMark is not long current)
            {
                this.highWaterMark = value;
                return;
            }

            if (this.Increment >= 0 ? value > current : value < current)
                this.highWaterMark = value;
        }
    }

    /// <summary>
    /// Reads the current high-water mark for snapshot purposes — TRUNCATE's
    /// undo entry captures this so a rollback restores both the row data
    /// and the counter position. Returns <c>null</c> when no value has yet
    /// been generated.
    /// </summary>
    internal long? Snapshot()
    {
        lock (this.gate)
            return this.highWaterMark;
    }

    /// <summary>
    /// Overwrites the high-water mark, used by TRUNCATE to reset to
    /// "no values generated yet" (passing <c>null</c>) and by the matching
    /// undo entry to restore the snapshot on rollback. Distinct from
    /// <see cref="ObserveExplicit"/>, which only advances forward.
    /// </summary>
    internal void Restore(long? value)
    {
        lock (this.gate)
            this.highWaterMark = value;
    }
}
