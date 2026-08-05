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
/// <item><description>Restricted contexts are gated at parse via
/// <see cref="ParserContext.NextValueForRejection"/>, which carries which of
/// real's nine refusals applies — see <see cref="NextValueForScope"/>, whose
/// declaration order is real's own precedence order.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class NextValueFor : Expression
{
    /// <summary>The sequence this reference advances.</summary>
    internal readonly Sequence Sequence;

    public NextValueFor(ParserContext context, MultiPartName sequenceName)
    {
        ThrowIfRejectedHere(context.NextValueForRejection);
        if (!context.Batch.TryResolveSequence(sequenceName, out var resolved))
        {
            // Real SQL Server distinguishes "object name doesn't resolve" (Msg 208)
            // from "name resolves but to a non-sequence object" (Msg 11726). Test the
            // latter first via the generic table resolver — if it finds a regular
            // object, Msg 11726 wins; otherwise Msg 208 from InvalidObjectName.
            // A synonym is a non-sequence object here even when its base IS a
            // sequence: probe-confirmed that real refuses NEXT VALUE FOR through
            // a synonym with the same Msg 11726.
            if (context.Batch.TryResolveSynonym(sequenceName, out _) || context.Batch.TryResolveTable(sequenceName, out _))
                throw SimulatedSqlException.ObjectIsNotASequence(sequenceName.ToString());
            throw SimulatedSqlException.InvalidObjectName(sequenceName);
        }
        this.Sequence = resolved;
        // Record the reference for any collector in scope (INSERT's Msg 11731
        // gate); collecting here catches a reference at any nesting depth.
        context.SequenceCollector?.Add(resolved);
    }

    /// <summary>
    /// Raises the refusal the scope names. Real settles every one of these
    /// while parsing, so the throw here refuses the whole batch and no
    /// sequence value is drawn (probe-confirmed: a sequence's
    /// <c>current_value</c> is unmoved after a rejected batch).
    /// </summary>
    private static void ThrowIfRejectedHere(NextValueForScope scope)
    {
        if (scope == NextValueForScope.Allowed)
            return;

        throw scope switch
        {
            NextValueForScope.Nested => SimulatedSqlException.NextValueForNotAllowedNested(),
            NextValueForScope.Aggregate => SimulatedSqlException.NextValueForNotAllowedInAggregate(),
            NextValueForScope.Deduplicating => SimulatedSqlException.NextValueForNotAllowedWithDedup(),
            NextValueForScope.OrderedStatement => SimulatedSqlException.NextValueForNotAllowedWithOrderBy(),
            NextValueForScope.Clause => SimulatedSqlException.NextValueForNotAllowedHere(),
            NextValueForScope.RowLimited => SimulatedSqlException.NextValueForNotAllowedWithRowLimit(),
            NextValueForScope.Conditional => SimulatedSqlException.NextValueForNotAllowedInConditional(),
            NextValueForScope.MergeAction => SimulatedSqlException.NextValueForNotAllowedInMergeAction(),
            _ => SimulatedSqlException.NextValueForNotAllowedInThisContext(),
        };
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var batch = runtime.Batch;
        if (batch.SequenceRowCache.TryGetValue(this.Sequence, out var entry) && entry.Stamp == batch.CurrentRowStamp)
            return entry.Value;
        // Advancing the sequence is both a side effect and a per-row-varying
        // value, so an enclosing uncorrelated subquery declines to replay its
        // result for the rest of the statement.
        batch.Connection.VolatileEvaluations++;
        var value = this.Sequence.Advance();
        batch.SequenceRowCache[this.Sequence] = (batch.CurrentRowStamp, value);
        return value;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Sequence.DeclaredType;

    internal override string DebugDisplay() => $"NEXT VALUE FOR {this.Sequence.FullName}";
}
