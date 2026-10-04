using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One user-defined sequence object. Created via <c>CREATE SEQUENCE
/// [schema.]name [AS &lt;type&gt;] [START WITH n] [INCREMENT BY n] [MINVALUE n]
/// [MAXVALUE n] [CYCLE|NO CYCLE] [CACHE n|NO CACHE]</c>; dropped via
/// <c>DROP SEQUENCE [IF EXISTS] [schema.]name</c>; mutated via
/// <c>ALTER SEQUENCE</c>; consumed via <c>NEXT VALUE FOR [schema.]name</c>.
/// Lives in its owning <see cref="Schema"/>'s <c>Sequences</c> dict and
/// shares the regular object namespace (Msg 2714 on cross-kind collision).
/// </summary>
/// <remarks>
/// <para>
/// Sequence values are tracked in <see cref="Int128"/> regardless of the
/// declared type, which holds every one of them — <c>decimal(38, 0)</c>'s
/// 38 digits included — while a step past either bound still fits the
/// overflow check in <see cref="Advance"/>.
/// <see cref="CurrentValue"/> tracks the next value to emit; first
/// <c>NEXT VALUE FOR</c> returns <see cref="CurrentValue"/> unchanged and
/// then advances it by <see cref="Increment"/> (probe-confirmed against
/// SQL Server 2025 — the start_value IS the first emitted value).
/// </para>
/// <para>
/// Cache options (<c>CACHE n</c> / <c>NO CACHE</c>) don't batch allocation
/// — the simulator is in-process so the optimization that backs real SQL
/// Server's CACHE semantics doesn't apply — and are kept for the Msg 11729
/// warning (<see cref="CacheExceedsAvailableValues"/>) and for
/// <c>sys.sequences</c>' <c>is_cached</c> / <c>cache_size</c>.
/// </para>
/// </remarks>
internal sealed class Sequence(
    Schema schema,
    string name,
    int objectId,
    DateTime createDate,
    SqlType declaredType,
    Int128 startValue,
    Int128 increment,
    Int128 minValue,
    Int128 maxValue,
    bool cycle)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    public override string ObjectTypeCode => "SO";
    public override string ObjectTypeDescription => "SEQUENCE_OBJECT";

    /// <summary>
    /// The declared scalar type. Constrained at CREATE SEQUENCE time to the
    /// integer family (tinyint / smallint / int / bigint) or
    /// <c>decimal(p, 0)</c> / <c>numeric(p, 0)</c>. Surfaces in
    /// <c>sys.sequences.system_type_id</c> / <c>user_type_id</c> and is the
    /// declared type of the <c>NEXT VALUE FOR</c> expression in projection
    /// schema.
    /// </summary>
    public readonly SqlType DeclaredType = declaredType;

    /// <summary>
    /// The sequence's start value, as <c>sys.sequences.start_value</c> reports
    /// it and as a bare <c>ALTER SEQUENCE … RESTART</c> resets to.
    /// <c>RESTART WITH n</c> moves it to n (probe-confirmed), so it tracks the
    /// most recent declared-or-restarted origin rather than staying pinned to
    /// the CREATE-time value.
    /// </summary>
    public Int128 StartValue = startValue;

    public Int128 Increment = increment;
    public Int128 MinValue = minValue;
    public Int128 MaxValue = maxValue;
    public bool Cycle = cycle;

    /// <summary>
    /// The declared cache size: <see langword="null"/> for the default, 0 for
    /// <c>NO CACHE</c>.
    /// </summary>
    public long? CacheSize;

    /// <summary>
    /// Whether the declaration spelled the type <c>numeric</c>, which
    /// <c>sys.sequences</c> reports as its own type (108) and a drawn value
    /// carries as its <c>sql_variant</c> base type.
    /// </summary>
    public bool SpelledNumeric;

    /// <summary>
    /// The next value to emit from <c>NEXT VALUE FOR</c>. Initially equals
    /// <see cref="StartValue"/>; first <c>NEXT VALUE FOR</c> returns this
    /// value and then advances it by <see cref="Increment"/>. When advance
    /// would cross the [<see cref="MinValue"/>, <see cref="MaxValue"/>]
    /// bound: cycles back to MinValue/MaxValue if <see cref="Cycle"/>;
    /// otherwise the sequence is exhausted (next call raises Msg 11728).
    /// </summary>
    public Int128 CurrentValue = startValue;

    /// <summary>
    /// True once the sequence has exhausted its no-cycle range. Sticky — only
    /// <c>ALTER SEQUENCE … RESTART WITH</c> clears it. Surfaces in
    /// <c>sys.sequences.is_exhausted</c>.
    /// </summary>
    public bool IsExhausted;

    /// <summary>
    /// The most recent value emitted by <c>NEXT VALUE FOR</c> in this process,
    /// or <c>null</c> if the sequence has never generated a value. Surfaces in
    /// <c>sys.sequences.last_used_value</c> (sql_variant, NULL until first use;
    /// probe-confirmed a bacpac-restored sequence reports NULL here even when
    /// <see cref="CurrentValue"/> is advanced — last_used_value is per-instance
    /// runtime state, not persisted). <c>ALTER SEQUENCE … RESTART</c> clears it.
    /// </summary>
    public Int128? LastUsedValue;

    /// <summary>
    /// <c>sys.sequences.current_value</c> as a sql_variant: the most recent
    /// value emitted, or — before any has been — the position the next
    /// <c>NEXT VALUE FOR</c> will return.
    /// <para>Probe-confirmed against SQL Server 2025 across all four states of
    /// a <c>START WITH 10 INCREMENT BY 5</c> sequence: fresh reports 10,
    /// issuing 10 leaves it at 10 (not the 15 that comes next),
    /// <c>RESTART WITH 100</c> reports 100, and issuing 100 leaves it at 100.
    /// So it is <see cref="LastUsedValue"/> when set, else
    /// <see cref="CurrentValue"/> — which holds the pending start / restart
    /// position and is what a freshly imported sequence carries.</para>
    /// <para>Projecting <see cref="CurrentValue"/> alone reported one
    /// increment ahead of real after any use, disagreeing with
    /// <c>last_used_value</c> where real has the two equal.</para>
    /// </summary>
    public SqlValue CurrentValueAsVariant => this.AsDeclaredVariant(this.LastUsedValue ?? this.CurrentValue);

    /// <summary>
    /// <c>sys.sequences.last_used_value</c> as a sql_variant: NULL when the
    /// sequence has never generated a value, else the last emitted value
    /// wrapped in the sequence's declared scalar type.
    /// </summary>
    public SqlValue LastUsedValueAsVariant => this.LastUsedValue is { } value
        ? this.AsDeclaredVariant(value)
        : SqlValue.Null(SqlType.SqlVariant);

    /// <summary>
    /// Wraps a value as a sql_variant carrying the sequence's
    /// declared scalar type — the projection form for the
    /// <c>start_value</c> / <c>increment</c> / <c>minimum_value</c> /
    /// <c>maximum_value</c> / <c>current_value</c> columns of
    /// <c>sys.sequences</c>, each a sql_variant in real SQL Server.
    /// </summary>
    public SqlValue AsDeclaredVariant(Int128 value) => this.DeclaredType is DecimalSqlType && !this.SpelledNumeric
        ? SqlValue.FromVariantNamedDecimal(this.WrapAsDeclaredType(value))
        : SqlValue.FromVariant(this.WrapAsDeclaredType(value));

    /// <summary>
    /// Whether the declared cache is longer than the values left from the
    /// position the next draw takes — real's Msg 11729, which it sends from the
    /// <c>CREATE</c> or <c>ALTER SEQUENCE</c> that leaves the sequence so, never
    /// from a draw (probed 2026-10-04 against SQL Server 2025: a <c>tinyint</c>
    /// sequence drawn from 100 to its end sends nothing). The default cache is
    /// 50 values, and <c>NO CACHE</c>, a cache of 1 and <c>CYCLE</c> never warn.
    /// </summary>
    public bool CacheExceedsAvailableValues()
    {
        if (this.CacheSize is 0 or 1 || this.Cycle || this.IsExhausted)
            return false;
        var bound = this.Increment > 0 ? this.MaxValue : this.MinValue;
        // The distance between two decimal(38, 0) bounds can pass Int128.
        var available = (((System.Numerics.BigInteger)bound - (System.Numerics.BigInteger)this.CurrentValue) / (System.Numerics.BigInteger)this.Increment) + 1;
        return available < (this.CacheSize ?? 50);
    }

    /// <summary>
    /// Moves the next position to follow the last value drawn under the
    /// options an <c>ALTER SEQUENCE</c> without <c>RESTART</c> left, which is
    /// what real does: the next draw is the last one plus the new increment,
    /// wrapping or exhausting against the new bounds, and an exhausted sequence
    /// whose new options leave room draws again (probed 2026-10-04 against SQL
    /// Server 2025). A sequence nothing has been drawn from keeps its position.
    /// </summary>
    public void RepositionAfterAlter()
    {
        if (this.LastUsedValue is not { } last)
            return;
        var next = (System.Numerics.BigInteger)last + (System.Numerics.BigInteger)this.Increment;
        if (next > (System.Numerics.BigInteger)this.MaxValue || next < (System.Numerics.BigInteger)this.MinValue)
        {
            if (this.Cycle)
            {
                this.CurrentValue = this.Increment > 0 ? this.MinValue : this.MaxValue;
                this.IsExhausted = false;
            }
            else
            {
                this.IsExhausted = true;
            }
            return;
        }
        this.CurrentValue = (Int128)next;
        this.IsExhausted = false;
    }

    /// <summary>
    /// Reserves <paramref name="size"/> consecutive values for
    /// <c>sp_sequence_get_range</c>, reporting the first, the last and how
    /// many times the range wrapped. A no-cycle range past the bound is Msg
    /// 11732 and leaves the sequence as it was; a cycling one wraps to the
    /// opposite bound as often as it needs (probed 2026-10-04 against SQL
    /// Server 2025: <c>tinyint</c> from 250 for 20 values ends at 13 having
    /// wrapped once).
    /// </summary>
    public (Int128 First, Int128 Last, int Cycles) DrawRange(long size)
    {
        if (this.IsExhausted)
            throw SimulatedSqlException.SequenceRangeExceedsLimit(this.Name);
        var increment = (System.Numerics.BigInteger)this.Increment;
        var first = (System.Numerics.BigInteger)this.CurrentValue;
        var ascending = this.Increment > 0;
        var bound = (System.Numerics.BigInteger)(ascending ? this.MaxValue : this.MinValue);
        var wrapStart = (System.Numerics.BigInteger)(ascending ? this.MinValue : this.MaxValue);
        var toBound = ((bound - first) / increment) + 1;
        System.Numerics.BigInteger last;
        var cycles = 0;
        if (size <= toBound)
        {
            last = first + ((size - 1) * increment);
        }
        else if (!this.Cycle)
        {
            throw SimulatedSqlException.SequenceRangeExceedsLimit(this.Name);
        }
        else
        {
            var perCycle = ((bound - wrapStart) / increment) + 1;
            var beyond = size - toBound - 1;
            cycles = (int)(1 + (beyond / perCycle));
            last = wrapStart + (beyond % perCycle * increment);
        }
        this.LastUsedValue = (Int128)last;
        var next = last + increment;
        if (ascending ? next > bound : next < bound)
        {
            if (this.Cycle)
                this.CurrentValue = (Int128)wrapStart;
            else
                this.IsExhausted = true;
        }
        else
        {
            this.CurrentValue = (Int128)next;
        }
        return ((Int128)first, (Int128)last, cycles);
    }

    /// <summary>
    /// Computes and reserves the next value for emission. Caller is
    /// responsible for the per-row cache check before calling — this method
    /// always advances. Raises Msg 11728 when no-cycle and already exhausted.
    /// </summary>
    public SqlValue Advance()
    {
        if (this.IsExhausted)
            throw SimulatedSqlException.SequenceExhausted(this.Name);

        var emit = this.CurrentValue;
        this.LastUsedValue = emit;
        var next = unchecked(this.CurrentValue + this.Increment);
        // Detect wrap-around: ascending sequence going past MaxValue, descending past MinValue.
        var ascending = this.Increment > 0;
        var wrapped = ascending
            ? (next > this.MaxValue || next < this.CurrentValue)
            : (next < this.MinValue || next > this.CurrentValue);
        if (wrapped)
        {
            if (this.Cycle)
            {
                this.CurrentValue = ascending ? this.MinValue : this.MaxValue;
            }
            else
            {
                this.IsExhausted = true;
            }
        }
        else
        {
            this.CurrentValue = next;
        }
        return WrapAsDeclaredType(emit);
    }

    /// <summary>
    /// Wraps a value as the sequence's declared type. The integer family uses
    /// the matching narrow value; decimal types carry it at scale 0.
    /// </summary>
    internal SqlValue WrapAsDeclaredType(Int128 value) => this.DeclaredType switch
    {
        TinyIntSqlType => SqlValue.FromByte((byte)value),
        SmallIntSqlType => SqlValue.FromInt16((short)value),
        Int32SqlType => SqlValue.FromInt32((int)value),
        BigIntSqlType => SqlValue.FromInt64((long)value),
        DecimalSqlType d => SqlValue.FromDecimal(d, Decimal38.FromParts((UInt128)Int128.Abs(value), Int128.IsNegative(value), 0)),
        _ => throw new InvalidOperationException($"Unsupported sequence declared type {this.DeclaredType}."),
    };

    public string FullName => $"{this.Schema.Name}.{this.Name}";
}
