using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// The DONE token real SQL Server gives each statement, which the TDS endpoint
// renders from the outcome stream: what kind a statement names there, and the
// count-less outcomes a statement producing none of its own sends in its place.
partial class Simulation
{
    /// <summary>
    /// The DONE kind a statement names, read off its leading tokens: a
    /// <see cref="StatementDoneKind"/> value, <see cref="StatementDoneKind.NoDone"/>
    /// for one that sends none, or null for a compound statement — an
    /// <c>IF</c>, <c>WHILE</c>, block, <c>TRY</c>, procedure call — whose
    /// DONE tokens its parts send. <c>SET</c>, <c>DECLARE</c> and
    /// <c>RETURN</c> start here and are refined by their parsers.
    /// </summary>
    private static ushort? StatementDoneKindOf(ParserContext context) => context.Token switch
    {
        Operator { Character: '(' } => StatementDoneKind.Select,
        UnquotedString { ContextualKeyword: ContextualKeyword.Throw } => StatementDoneKind.RaisError,
        ReservedKeyword { Keyword: var keyword } => keyword switch
        {
            Keyword.Alter => AlterDoneKind(PeekAfterLead(context)),
            Keyword.Begin => PeekAfterLead(context) is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed }
                ? StatementDoneKind.BeginTransaction
                : null,
            Keyword.Break or Keyword.Continue or Keyword.Goto => StatementDoneKind.Goto,
            // BULK INSERT's own code isn't captured; it closes as an INSERT.
            Keyword.Bulk => StatementDoneKind.Insert,
            Keyword.Checkpoint => StatementDoneKind.Checkpoint,
            Keyword.Close => StatementDoneKind.CloseCursor,
            Keyword.Commit => StatementDoneKind.Commit,
            Keyword.Create => CreateDoneKind(context),
            Keyword.Dbcc => StatementDoneKind.Dbcc,
            Keyword.Deallocate => StatementDoneKind.DeallocateCursor,
            // A cursor declaration is a SELECT-kind statement; a variable's is
            // one only when it initializes, which its parser reports.
            Keyword.Declare => PeekAfterLead(context) is AtPrefixedString ? StatementDoneKind.NoDone : StatementDoneKind.Select,
            Keyword.Delete => StatementDoneKind.Delete,
            Keyword.Drop => DropDoneKind(PeekAfterLead(context)),
            Keyword.Fetch or Keyword.Select => StatementDoneKind.Select,
            Keyword.Insert => StatementDoneKind.Insert,
            Keyword.Merge => StatementDoneKind.Merge,
            Keyword.Open => StatementDoneKind.OpenCursor,
            Keyword.Print => StatementDoneKind.Print,
            Keyword.RaisError => StatementDoneKind.RaisError,
            Keyword.ReadText => StatementDoneKind.ReadText,
            Keyword.Reconfigure => StatementDoneKind.Reconfigure,
            Keyword.Return => StatementDoneKind.Return,
            Keyword.Rollback => StatementDoneKind.Rollback,
            Keyword.Save => StatementDoneKind.Save,
            Keyword.Set => StatementDoneKind.NoDone,
            Keyword.Truncate => StatementDoneKind.Truncate,
            Keyword.Update => PeekAfterLead(context) is ReservedKeyword { Keyword: Keyword.Statistics }
                ? StatementDoneKind.UpdateStatistics
                : StatementDoneKind.Update,
            Keyword.UpdateText => StatementDoneKind.UpdateText,
            Keyword.Use => StatementDoneKind.Use,
            Keyword.WaitFor => StatementDoneKind.WaitFor,
            Keyword.With => CommonTableExpressionDoneKind(context),
            Keyword.WriteText => StatementDoneKind.WriteText,
            _ => null,
        },
        _ => null,
    };

    private static Token? PeekAfterLead(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next;
    }

    private static ushort CreateDoneKind(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var created = context.GetNextOptional();
            // CREATE OR ALTER names what CREATE would.
            if (created is ReservedKeyword { Keyword: Keyword.Or })
            {
                _ = context.GetNextOptional();
                created = context.GetNextOptional();
            }
            return created switch
            {
                ReservedKeyword { Keyword: Keyword.Clustered or Keyword.Index or Keyword.NonClustered or Keyword.Primary or Keyword.Statistics or Keyword.Unique }
                    => StatementDoneKind.CreateIndex,
                ReservedKeyword { Keyword: Keyword.Database } => StatementDoneKind.CreateDatabase,
                ReservedKeyword { Keyword: Keyword.Default } => StatementDoneKind.CreateDefault,
                ReservedKeyword { Keyword: Keyword.Function or Keyword.Proc or Keyword.Procedure } => StatementDoneKind.CreateProcedure,
                ReservedKeyword { Keyword: Keyword.Rule } => StatementDoneKind.CreateRule,
                ReservedKeyword { Keyword: Keyword.Table } => StatementDoneKind.CreateTable,
                ReservedKeyword { Keyword: Keyword.Trigger } => StatementDoneKind.CreateTrigger,
                ReservedKeyword { Keyword: Keyword.View } => StatementDoneKind.CreateView,
                UnquotedString { ContextualKeyword: ContextualKeyword.Xml } => context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Schema }
                    ? StatementDoneKind.CreateXmlSchemaCollection
                    : StatementDoneKind.CreateIndex,
                UnquotedString { ContextualKeyword: ContextualKeyword.Spatial } => StatementDoneKind.CreateIndex,
                UnquotedString { Span: var word } when word.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase) || word.Equals("JSON", StringComparison.OrdinalIgnoreCase)
                    => StatementDoneKind.CreateIndex,
                _ => StatementDoneKind.NoDone,
            };
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    private static ushort AlterDoneKind(Token? altered) => altered switch
    {
        ReservedKeyword { Keyword: Keyword.Database } => StatementDoneKind.AlterDatabase,
        ReservedKeyword { Keyword: Keyword.Function or Keyword.Proc or Keyword.Procedure } => StatementDoneKind.CreateProcedure,
        ReservedKeyword { Keyword: Keyword.Index } => StatementDoneKind.AlterIndex,
        ReservedKeyword { Keyword: Keyword.Table } => StatementDoneKind.AlterTable,
        ReservedKeyword { Keyword: Keyword.Trigger } => StatementDoneKind.CreateTrigger,
        ReservedKeyword { Keyword: Keyword.View } => StatementDoneKind.CreateView,
        _ => StatementDoneKind.NoDone,
    };

    private static ushort DropDoneKind(Token? dropped) => dropped switch
    {
        ReservedKeyword { Keyword: Keyword.Database } => StatementDoneKind.DropDatabase,
        ReservedKeyword { Keyword: Keyword.Default } => StatementDoneKind.DropDefault,
        ReservedKeyword { Keyword: Keyword.Function } => StatementDoneKind.DropFunction,
        ReservedKeyword { Keyword: Keyword.Index or Keyword.Statistics } => StatementDoneKind.DropIndex,
        ReservedKeyword { Keyword: Keyword.Proc or Keyword.Procedure } => StatementDoneKind.DropProcedure,
        ReservedKeyword { Keyword: Keyword.Rule } => StatementDoneKind.DropRule,
        ReservedKeyword { Keyword: Keyword.Table } => StatementDoneKind.DropTable,
        ReservedKeyword { Keyword: Keyword.Trigger } => StatementDoneKind.DropTrigger,
        ReservedKeyword { Keyword: Keyword.View } => StatementDoneKind.DropView,
        UnquotedString { ContextualKeyword: ContextualKeyword.Sequence } => StatementDoneKind.DropSequence,
        UnquotedString { Span: var word } when word.Equals("SYNONYM", StringComparison.OrdinalIgnoreCase) => StatementDoneKind.DropSynonym,
        _ => StatementDoneKind.NoDone,
    };

    /// <summary>
    /// The kind of the statement a <c>WITH</c> prefix leads: the first query
    /// or DML verb outside the prefix's parentheses.
    /// </summary>
    private static ushort CommonTableExpressionDoneKind(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var depth = 0;
            while (context.MoveNext())
            {
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        continue;
                    case Operator { Character: ')' }:
                        depth--;
                        continue;
                    case ReservedKeyword { Keyword: var verb } when depth == 0:
                        switch (verb)
                        {
                            case Keyword.Delete:
                                return StatementDoneKind.Delete;
                            case Keyword.Insert:
                                return StatementDoneKind.Insert;
                            case Keyword.Merge:
                                return StatementDoneKind.Merge;
                            case Keyword.Select:
                                return StatementDoneKind.Select;
                            case Keyword.Update:
                                return StatementDoneKind.Update;
                        }
                        continue;
                }
            }
            return StatementDoneKind.Select;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// The count-less outcome that stands for a statement's own DONE token —
    /// a <c>SET</c>, <c>BEGIN TRANSACTION</c>, <c>PRINT</c>, an <c>IF</c>
    /// condition — carrying the kind real names there and, for a statement
    /// real counts as one row (<c>SET @v = …</c>), that count as a
    /// returned-row count, which no client folds into <c>RecordsAffected</c>.
    /// </summary>
    internal static SimulatedNonQuery StatementDone(BatchContext batch, ushort kind, int count = -1) =>
        new(count, countsRowsReturned: count >= 0)
        {
            DoneKind = kind,
            InModule = batch.ProcFrame is not null || batch.TriggerFrame is not null,
            CountSuppressed = batch.Connection.NoCount,
            TransactionEventMark = batch.Connection.TransactionEventsRecorded,
        };

    /// <summary>
    /// Whether the statement at the cursor calls a procedure — an
    /// <c>EXEC</c> of one or of dynamic SQL, or a batch's implicit call —
    /// whose scope closes with a DONEPROC even when the call fails.
    /// </summary>
    private static bool IsProcedureCall(ParserContext context, bool atBatchStart) => context.Token switch
    {
        ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute } => PeekAfterLead(context) is not ReservedKeyword { Keyword: Keyword.As },
        Name or AtPrefixedString => atBatchStart,
        _ => false,
    };

    /// <summary>
    /// Gives a statement's own outcomes the kind it names in its DONE, and
    /// when it produced none and <paramref name="standInForNone"/> asks,
    /// sends one standing for that DONE. Returns whether it had one of its own.
    /// </summary>
    private static bool FrameStatement(BatchContext batch, List<SimulatedStatementOutcome> outcomes, bool standInForNone)
    {
        var kind = batch.CurrentStatement.DoneKind;
        var inModule = batch.ProcFrame is not null || batch.TriggerFrame is not null;
        var own = false;
        foreach (var outcome in outcomes)
        {
            if (outcome is not (SimulatedQueryResult or SimulatedNonQuery))
                continue;
            own = true;
            if (outcome.DoneKind == StatementDoneKind.NoDone)
            {
                outcome.DoneKind = kind;
                outcome.InModule = inModule;
                outcome.TransactionEventMark = batch.Connection.TransactionEventsRecorded;
            }
        }
        if (!own && standInForNone && kind != StatementDoneKind.NoDone)
            outcomes.Add(StatementDone(batch, kind, batch.CurrentStatement.DoneCount));
        return own;
    }

    /// <summary>
    /// The marker closing a procedure or dynamic-SQL scope that ran to its
    /// end, returning <paramref name="returnStatus"/>, as its caller
    /// <paramref name="caller"/> sends it.
    /// </summary>
    internal static SimulatedProcScopeBoundary ScopeExit(BatchContext caller, int returnStatus) =>
        new(isEnter: false, returnStatus: returnStatus)
        {
            DoneKind = StatementDoneKind.Execute,
            InModule = caller.ProcFrame is not null || caller.TriggerFrame is not null,
            CountSuppressed = caller.Connection.NoCount,
            TransactionEventMark = caller.Connection.TransactionEventsRecorded,
        };

    /// <summary>
    /// How many procedure or dynamic-SQL scopes <paramref name="outcomes"/>
    /// opened and didn't close — the ones an error abandoned.
    /// </summary>
    private static int OpenProcScopes(List<SimulatedStatementOutcome> outcomes)
    {
        var open = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome is SimulatedProcScopeBoundary boundary)
                open += boundary.IsEnter ? 1 : -1;
        }
        return open;
    }

    /// <summary>
    /// Closes, into <paramref name="closing"/>, the scopes an error abandoned
    /// among <paramref name="produced"/> — at least one for a procedure call,
    /// whose DONEPROC real sends even when the call failed before its body
    /// began — with no return status, and with the error bit unless a
    /// <c>TRY</c> caught the error (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private static void CloseAbandonedProcScopes(BatchContext batch, List<SimulatedStatementOutcome> produced, List<SimulatedStatementOutcome> closing, bool isCall, bool endedByError)
    {
        var open = Math.Max(OpenProcScopes(produced), isCall ? 1 : 0);
        var inModule = batch.ProcFrame is not null || batch.TriggerFrame is not null;
        for (var i = 0; i < open; i++)
        {
            closing.Add(new SimulatedProcScopeBoundary(isEnter: false, endedByError: endedByError)
            {
                DoneKind = StatementDoneKind.Execute,
                InModule = inModule,
                CountSuppressed = batch.Connection.NoCount,
                TransactionEventMark = batch.Connection.TransactionEventsRecorded,
            });
        }
    }
}
