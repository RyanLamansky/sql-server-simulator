using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Captures <c>NEXT VALUE FOR [schema.]sequence [OVER (ORDER BY ...)]</c> at
/// parse time. Resolves the target <see cref="Sequence"/> through
/// <see cref="BatchContext.TryResolveSequence"/>; runtime evaluation reads
/// (and possibly advances) the sequence with same-row dedup via
/// <see cref="BatchContext.SequenceRowCache"/> keyed on
/// <see cref="BatchContext.CurrentRowStamp"/>.
/// </summary>
/// <remarks>
/// <para>
/// Probe-confirmed semantics (SQL Server 2025):
/// <list type="bullet">
/// <item><description>First <c>NEXT VALUE FOR</c> returns <c>start_value</c>
/// (not <c>start + increment</c>) — handled by initializing
/// <see cref="Sequence.CurrentValue"/> = <see cref="Sequence.StartValue"/>.</description></item>
/// <item><description>Multiple <c>NEXT VALUE FOR seq</c> instances in one
/// row of one statement return the same value — handled by the per-row
/// stamp cache.</description></item>
/// <item><description>Across rows (e.g. <c>SELECT next FROM 3-row-table</c>),
/// values advance by <c>increment</c>.</description></item>
/// <item><description>NULL handling: a sequence never emits NULL; the
/// <c>OVER</c> clause is parsed-and-ignored (the simulator iterates in a
/// single deterministic order regardless).</description></item>
/// <item><description>No-cycle exhaustion raises Msg 11728 from
/// <see cref="Sequence.Advance"/>.</description></item>
/// <item><description>Restricted contexts (TOP / OVER / OUTPUT / ON / WHERE
/// / GROUP BY / HAVING / ORDER BY) raise Msg 11720 — gated at parse via
/// <see cref="ParserContext.RejectNextValueFor"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class NextValueFor : Expression
{
    private readonly Sequence sequence;

    public NextValueFor(ParserContext context, MultiPartName sequenceName)
    {
        if (context.RejectNextValueFor)
            throw SimulatedSqlException.NextValueForNotAllowedHere();
        if (!context.Batch.TryResolveSequence(sequenceName, out var resolved))
        {
            // Real SQL Server distinguishes "object name doesn't resolve" (Msg 208)
            // from "name resolves but to a non-sequence object" (Msg 11726). Test the
            // latter first via the generic table resolver — if it finds a regular
            // object, Msg 11726 wins; otherwise Msg 208 from InvalidObjectName.
            if (context.Batch.TryResolveTable(sequenceName, out _))
                throw SimulatedSqlException.ObjectIsNotASequence(sequenceName.ToString());
            throw SimulatedSqlException.InvalidObjectName(sequenceName);
        }
        this.sequence = resolved;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var batch = runtime.Batch;
        if (batch.SequenceRowCache.TryGetValue(this.sequence, out var entry) && entry.Stamp == batch.CurrentRowStamp)
            return entry.Value;
        var value = this.sequence.Advance();
        batch.SequenceRowCache[this.sequence] = (batch.CurrentRowStamp, value);
        return value;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.sequence.DeclaredType;

    internal override string DebugDisplay() => $"NEXT VALUE FOR {this.sequence.FullName}";
}
