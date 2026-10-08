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
/// <item><description>NULL handling: a sequence never emits NULL. An
/// <c>OVER (ORDER BY …)</c> orders the draws (<see cref="OverRank"/>).</description></item>
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

    /// <summary>The record this reference left for its query spec to settle, or null when it was refused or accepted where it parsed.</summary>
    internal readonly DeferredNextValueRef? Deferred;

    /// <summary>Whether the reference is a column default's, which draws as part of the table's own write and so checks nothing of its own.</summary>
    private readonly bool inDefault;

    /// <summary>
    /// The name a table variable's default gave a sequence that was missing as
    /// the declaration ran, which <see cref="Sequence"/> stands in for: drawing
    /// is Msg 208 at state 211. Null for a sequence that resolved.
    /// </summary>
    private readonly MultiPartName? missingAtDraw;

    /// <summary>
    /// The <c>ROW_NUMBER()</c> over this reference's <c>OVER (ORDER BY …)</c>,
    /// set once the clause parses: the row it ranks k takes the statement's
    /// k-th draw (probed 2026-10-04 against SQL Server 2025). Null without an
    /// <c>OVER</c>, or where no query block ranks rows.
    /// </summary>
    internal WindowExpression? OverRank;

    public NextValueFor(ParserContext context, MultiPartName sequenceName)
    {
        var scope = context.NextValueForRejection;
        SimulatedSqlException? pendingRefusal = null;
        if (context.DeferNextValueRefusals
            && scope is NextValueForScope.Allowed or NextValueForScope.Clause or NextValueForScope.RowLimited or NextValueForScope.Conditional)
        {
            // Real settles the statement-level refusals (ORDER BY, OFFSET)
            // ahead of these three, and those clauses are read after the
            // reference; the spec judges it once it has them all.
            this.Deferred = new DeferredNextValueRef(scope) { OverBody = context.InOverBody, InTop = context.InTopCount };
            (context.DeferredNextValueRefs ??= []).Add(this.Deferred);
        }
        else if (scope == NextValueForScope.Nested && context.Batch.IsSkipping)
        {
            // A nested query reading what the batch has yet to create defers
            // with its statement (see StatementContext.PendingCompileRefusal);
            // skipped as the batch runs, the statement is one the compile
            // deferred, which nothing compiles now.
            if (context.Batch.CreateTimeBinding)
            {
                pendingRefusal = RefusalFor(scope);
                pendingRefusal.ResolveDiagnostics(context.Token?.LineNumber ?? context.Batch.CurrentStatement.StartLine, context.Batch.LineOffset, context.Batch.ErrorProcedureName);
                context.Batch.CurrentStatement.PendingCompileRefusal ??= pendingRefusal;
            }
        }
        else if (scope == NextValueForScope.Nested)
        {
            // Met running, the statement is one the compile deferred.
            var refused = RefusalFor(scope);
            refused.RefusedRecompilingDeferred = true;
            throw refused;
        }
        else
        {
            ThrowIfRejectedHere(scope);
        }
        if (context.DefaultOfTableVariable && context.InDefaultClause && !context.Batch.TryResolveSequence(sequenceName, out _) && !NamesAnotherObject(context.Batch, sequenceName))
        {
            // A table variable's default looks its sequence up before judging
            // the name's database part, and a sequence missing then ends the
            // batch's compile without a word (BatchContext.SilentCompileEnd);
            // one dropped after the batch compiled is looked for again as the
            // default draws, Msg 208 at state 211 then (probed 2026-10-08
            // against SQL Server 2025).
            if (context.Batch.CreateTimeBinding)
                context.Batch.SilentCompileEnd ??= (SimulatedSqlException.TableVariableDefaultSequenceMissing(), context.Batch.CreateTimeBindErrors?.Count ?? 0);
            this.Sequence = new Sequence(context.Batch.Parser.CurrentDatabase.Schemas[Database.DefaultSchemaName], sequenceName.Leaf, 0, default, SqlType.BigInt, 1, 1, long.MinValue, long.MaxValue, cycle: false);
            this.missingAtDraw = sequenceName;
            this.inDefault = true;
            return;
        }
        if (context.InDefaultClause && sequenceName.Count >= 3 && sequenceName[sequenceName.Count - 3] is { Length: > 0 })
            throw SimulatedSqlException.SequenceDatabaseNameInDefault();
        if (pendingRefusal is not null && !context.Batch.TryResolveSequence(sequenceName, out _))
        {
            // The sequence's own absence defers nothing of the refusal, which
            // waits only on the statement's sources (probed 2026-10-06 against
            // SQL Server 2025), so the walk reads on over a stand-in.
            this.Sequence = new Sequence(context.Batch.Parser.CurrentDatabase.Schemas[Database.DefaultSchemaName], sequenceName.Leaf, 0, default, SqlType.BigInt, 1, 1, long.MinValue, long.MaxValue, cycle: false);
            return;
        }
        if (context.DefaultResolvesInTempdb && context.InDefaultClause)
        {
            this.Sequence = ResolveInTempdb(context.Batch, sequenceName);
            this.inDefault = true;
            return;
        }
        if (!context.Batch.TryResolveSequence(sequenceName, out var resolved))
        {
            // Real SQL Server distinguishes "object name doesn't resolve" (Msg 208)
            // from "name resolves but to a non-sequence object" (Msg 11726). Test the
            // latter first via the generic table resolver — if it finds a regular
            // object, Msg 11726 wins; otherwise Msg 208 from InvalidObjectName.
            // A synonym is a non-sequence object here even when its base IS a
            // sequence: probe-confirmed that real refuses NEXT VALUE FOR through
            // a synonym with the same Msg 11726.
            // A missing sequence is Msg 208 at state 211 wherever it is named
            // (probed 2026-10-08 against SQL Server 2025).
            if (NamesAnotherObject(context.Batch, sequenceName))
                throw SimulatedSqlException.ObjectIsNotASequence(sequenceName.ToString());
            throw SimulatedSqlException.InvalidObjectName(sequenceName, 211);
        }
        this.Sequence = resolved;
        this.inDefault = context.InDefaultClause;
        context.Batch.BeginImplicitTransaction();
        // Record the reference for any collector in scope (INSERT's Msg 11731
        // gate); collecting here catches a reference at any nesting depth.
        context.SequenceCollector?.Add(resolved);
    }

    /// <summary>Whether <paramref name="sequenceName"/> names an object other than a sequence, which is Msg 11726.</summary>
    private static bool NamesAnotherObject(BatchContext batch, MultiPartName sequenceName) =>
        batch.TryResolveSynonym(sequenceName, out _) || batch.TryResolveTable(sequenceName, out _)
        || (batch.TryResolveSchema(sequenceName, out var schema) && schema.HasNameInSharedNamespace(sequenceName.Leaf));

    /// <summary>
    /// The sequence a <c>#temp</c> table's default names, which real looks
    /// up in tempdb, its schema defaulting to <c>dbo</c>: Msg 208 at state 211
    /// for one that only the current database holds (probed 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    private static Sequence ResolveInTempdb(BatchContext batch, MultiPartName sequenceName)
    {
        var tempdb = batch.Connection.Simulation.Databases[Simulation.TempdbDatabaseName];
        var schemaName = sequenceName.ImmediateQualifier is { Length: > 0 } qualifier ? qualifier : Database.DefaultSchemaName;
        if (!tempdb.Schemas.TryGetValue(schemaName, out var schema) || !schema.Sequences.TryGetValue(sequenceName.Leaf, out var sequence))
            throw SimulatedSqlException.InvalidObjectName(sequenceName, 211);
        batch.AcquireStatementLock(sequence.SchemaLock, LockMode.SchemaStability);
        return sequence;
    }

    /// <summary>
    /// Raises the refusal the scope names. Real settles every one of these
    /// while parsing, so the throw here refuses the whole batch and no
    /// sequence value is drawn (probe-confirmed: a sequence's
    /// <c>current_value</c> is unmoved after a rejected batch).
    /// </summary>
    private static void ThrowIfRejectedHere(NextValueForScope scope)
    {
        if (scope != NextValueForScope.Allowed)
            throw RefusalFor(scope);
    }

    /// <summary>The refusal real raises for a reference under <paramref name="scope"/>.</summary>
    internal static SimulatedSqlException RefusalFor(NextValueForScope scope) => scope switch
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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var batch = runtime.Batch;
        if (this.missingAtDraw is { } missing)
            throw SimulatedSqlException.InvalidObjectName(missing, 211);
        if (this.OverRank is { } rank)
            return this.DrawRanked(runtime, rank);
        if (batch.SequenceRowCache?.TryGetValue(this.Sequence, out var entry) == true && entry.Stamp == batch.CurrentRowStamp)
            return entry.Value;
        if (batch.SequenceValuesByRow?.TryGetValue((this.Sequence, batch.CurrentRowStamp), out var retained) == true)
            return retained;
        // Advancing the sequence is both a side effect and a per-row-varying
        // value, so an enclosing uncorrelated subquery declines to replay its
        // result for the rest of the statement.
        batch.Connection.VolatileEvaluations++;
        // Drawing a value takes UPDATE on the sequence, checked once per
        // statement and chained inside a module like any reference (probed
        // 2026-10-04 against SQL Server 2025).
        if (!this.inDefault && !batch.Connection.Security.EffectiveIsDbo)
            PermissionEnforcement.CheckSequenceUpdate(batch, this.Sequence);
        // Advancing is a write, refused in a read-only database when a value
        // is actually drawn (probed 2026-09-25 against SQL Server 2025).
        this.Sequence.Schema.Database.RejectWriteWhenReadOnly();
        var value = this.Sequence.Advance();
        (batch.SequenceRowCache ??= [])[this.Sequence] = (batch.CurrentRowStamp, value);
        if (batch.SequenceValuesByRow is { } byRow)
            byRow[(this.Sequence, batch.CurrentRowStamp)] = value;
        return value;
    }

    /// <summary>
    /// The value for a row <paramref name="rank"/> places k-th: the statement's
    /// k-th draw from the sequence, drawing up to it when the rows arrive out of
    /// rank order.
    /// </summary>
    private SqlValue DrawRanked(RuntimeContext runtime, WindowExpression rank)
    {
        var batch = runtime.Batch;
        var position = (int)rank.Run(runtime).AsInt64;
        var draws = batch.CurrentStatement.OrderedSequenceDraws ??= [];
        if (!draws.TryGetValue(this.Sequence, out var drawn))
            draws[this.Sequence] = drawn = [];
        while (drawn.Count < position)
        {
            batch.Connection.VolatileEvaluations++;
            if (drawn.Count == 0)
            {
                if (!batch.Connection.Security.EffectiveIsDbo)
                    PermissionEnforcement.CheckSequenceUpdate(batch, this.Sequence);
                this.Sequence.Schema.Database.RejectWriteWhenReadOnly();
            }
            drawn.Add(this.Sequence.Advance());
        }
        return drawn[position - 1];
    }

    internal override bool ResultReportsNumeric => this.Sequence.SpelledNumeric;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Sequence.DeclaredType;

    // A sequence always yields a value, and real describes the column NOT NULL
    // (probed 2026-10-02 against SQL Server 2025).
    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => $"NEXT VALUE FOR {this.Sequence.FullName}";

    internal override void Describe(NodeShape shape) => shape.Local(this.Sequence);
}
