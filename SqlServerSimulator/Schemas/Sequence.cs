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
/// Sequence values are tracked in <see cref="long"/> regardless of the
/// declared type — int / bigint / smallint / tinyint all fit, and
/// <c>decimal(p, 0)</c> values are bounded by their precision (largest
/// supported is <c>decimal(18, 0)</c> for safety, since long can hold up to
/// 19 decimal digits; CLAUDE.md mentions decimal values requiring more than
/// 28 significant digits aren't modeled and the same applies here).
/// <see cref="CurrentValue"/> tracks the next value to emit; first
/// <c>NEXT VALUE FOR</c> returns <see cref="CurrentValue"/> unchanged and
/// then advances it by <see cref="Increment"/> (probe-confirmed against
/// SQL Server 2025 — the start_value IS the first emitted value).
/// </para>
/// <para>
/// Cache options (<c>CACHE n</c> / <c>NO CACHE</c>) are accepted at parse
/// but ignored — the simulator is in-process so the batched-allocation
/// optimization that backs real SQL Server's CACHE semantics doesn't apply.
/// <c>is_cached</c> reports true (the SQL Server default) and
/// <c>cache_size</c> reports <see cref="DBNull"/> through
/// <c>sys.sequences</c>, matching the real server's behavior when no
/// explicit cache size is set.
/// </para>
/// </remarks>
internal sealed class Sequence(
    Schema schema,
    string name,
    int objectId,
    DateTime createDate,
    SqlType declaredType,
    long startValue,
    long increment,
    long minValue,
    long maxValue,
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

    /// <summary>The declared start value — captured for sys.sequences but otherwise unused after construction; ALTER … RESTART WITH overrides into <see cref="CurrentValue"/>.</summary>
    public long StartValue = startValue;

    public long Increment = increment;
    public long MinValue = minValue;
    public long MaxValue = maxValue;
    public bool Cycle = cycle;

    /// <summary>
    /// The next value to emit from <c>NEXT VALUE FOR</c>. Initially equals
    /// <see cref="StartValue"/>; first <c>NEXT VALUE FOR</c> returns this
    /// value and then advances it by <see cref="Increment"/>. When advance
    /// would cross the [<see cref="MinValue"/>, <see cref="MaxValue"/>]
    /// bound: cycles back to MinValue/MaxValue if <see cref="Cycle"/>;
    /// otherwise the sequence is exhausted (next call raises Msg 11728).
    /// </summary>
    public long CurrentValue = startValue;

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
    public long? LastUsedValue;

    /// <summary>
    /// <c>sys.sequences.last_used_value</c> as a sql_variant: NULL when the
    /// sequence has never generated a value, else the last emitted value
    /// wrapped in the sequence's declared scalar type.
    /// </summary>
    public SqlValue LastUsedValueAsVariant => this.LastUsedValue is { } value
        ? SqlValue.FromVariant(this.WrapAsDeclaredType(value))
        : SqlValue.Null(SqlType.SqlVariant);

    /// <summary>
    /// Computes and reserves the next value for emission. Caller is
    /// responsible for the per-row cache check before calling — this method
    /// always advances. Raises Msg 11728 when no-cycle and already exhausted.
    /// </summary>
    public SqlValue Advance()
    {
        if (this.IsExhausted)
            throw SimulatedSqlException.SequenceExhausted(this.FullName);

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
    /// Wraps a <see cref="long"/> as the sequence's declared type. The
    /// integer family uses the matching narrow value; decimal types route
    /// through <see cref="SqlValue.FromDecimal"/> with scale 0.
    /// </summary>
    private SqlValue WrapAsDeclaredType(long value) => this.DeclaredType switch
    {
        TinyIntSqlType => SqlValue.FromByte((byte)value),
        SmallIntSqlType => SqlValue.FromInt16((short)value),
        Int32SqlType => SqlValue.FromInt32((int)value),
        BigIntSqlType => SqlValue.FromInt64(value),
        DecimalSqlType d => SqlValue.FromDecimal(d, value),
        _ => throw new InvalidOperationException($"Unsupported sequence declared type {this.DeclaredType}."),
    };

    public string FullName => $"{this.Schema.Name}.{this.Name}";
}
