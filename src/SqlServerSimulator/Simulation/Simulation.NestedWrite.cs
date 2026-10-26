using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Composable DML: an INSERT … SELECT reading the rows a nested INSERT,
// UPDATE, DELETE or MERGE sends through its OUTPUT clause — see
// docs/claude/dml.md#composable-dml.
partial class Simulation
{
    /// <summary>
    /// Parses the DML statement at the cursor, nested as an <c>INSERT …
    /// SELECT</c>'s source, to its split point, and answers its plan unrun —
    /// null where its parse ended before reaching one, as a skip-mode parse
    /// may. The cursor ends on the parenthesis closing the statement. It
    /// parses as a statement of its own, the enclosing query's scope set
    /// aside: no name in it reads the outer query, which has no other source.
    /// </summary>
    internal static DmlStatementPlan? ParseNestedWrite(ParserContext context)
    {
        var capture = new NestedWriteCapture();
        using var nested = ParserScope.Enter(ref context.NestedWrite, capture);
        using var sink = ParserScope.Enter(ref context.FromSourceColumnSink, null);
        using var outerTypes = ParserScope.Enter(ref context.OuterTypeResolver, null);
        using var outerMasks = ParserScope.Enter(ref context.OuterMaskResolver, null);
        using var scopeSources = ParserScope.Enter(ref context.ScopeSources, null);
        using var scopeJoins = ParserScope.Enter(ref context.ScopeJoins, null);
        using var aggregates = ParserScope.Enter(ref context.AggregateCollector, null);
        using var windows = ParserScope.Enter(ref context.WindowCollector, null);
        _ = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Insert } => ParseInsert(context),
            ReservedKeyword { Keyword: Keyword.Update } => ParseUpdate(context),
            ReservedKeyword { Keyword: Keyword.Delete } => ParseDelete(context),
            _ => ParseMerge(context),
        };
        return capture.Plan;
    }

    /// <summary>
    /// The rows a statement's nested write sends through its <c>OUTPUT</c>,
    /// running <paramref name="plan"/> the first time the statement's source
    /// reads them in an execution and answering the same rows after. The
    /// write runs inside the outer statement's own atomic scope, written
    /// before its rows go on, and the frame state its run settles — what the
    /// statement's DONE names, the client <c>OUTPUT</c> it noted — stays the
    /// outer statement's.
    /// </summary>
    internal static List<byte[]> RunNestedWrite(BatchContext batch, DmlStatementPlan plan)
    {
        var statement = batch.CurrentStatement;
        if (statement.NestedWriteRun is { } ran)
            return ran.Rows;
        var (doneKind, doneCount, outputShape) = (statement.DoneKind, statement.DoneCount, statement.ClientOutputShape);
        statement.RunsNestedWrite = true;
        SimulatedStatementOutcome outcome;
        try
        {
            outcome = plan.Run(batch.Parser);
        }
        finally
        {
            statement.RunsNestedWrite = false;
            (statement.DoneKind, statement.DoneCount, statement.ClientOutputShape) = (doneKind, doneCount, outputShape);
        }
        List<byte[]> rows = outcome is SimulatedSqlResultSet output ? [.. output.RowBytes] : [];
        statement.NestedWriteRun = (rows, batch.Connection.LastIdentity, batch.Connection.ScopeIdentity);
        return rows;
    }

    /// <summary>
    /// Refuses an <c>INSERT</c> target real refuses beside a nested write, as
    /// the statement binds (probed 2026-10-26 against SQL Server 2025): one
    /// with an enabled trigger (Msg 355), then one on either side of a
    /// foreign key (Msg 356).
    /// </summary>
    private static void RefuseNestedWriteTarget(BatchContext batch, HeapTable target)
    {
        if (Array.Exists(TriggersAttachedTo(batch, target), static trigger => !trigger.IsDisabled))
            throw SimulatedSqlException.NestedWriteTargetHasTriggers(target.Name);
        if (target.OutgoingForeignKeys.Count != 0 || target.IncomingForeignKeys.Count != 0)
            throw SimulatedSqlException.NestedWriteTargetInForeignKey(target.Name);
    }

    /// <summary>
    /// Puts back the identity values a statement's nested write left, which
    /// <c>@@IDENTITY</c> and <c>SCOPE_IDENTITY()</c> report after the
    /// statement in place of those its outer <c>INSERT</c> drew.
    /// </summary>
    private static void RestoreNestedWriteIdentity(BatchContext batch)
    {
        if (batch.CurrentStatement.NestedWriteRun is not { } ran)
            return;
        batch.Connection.LastIdentity = ran.LastIdentity;
        batch.Connection.ScopeIdentity = ran.ScopeIdentity;
    }
}
