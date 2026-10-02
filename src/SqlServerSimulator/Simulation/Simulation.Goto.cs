using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

/// <summary>
/// Where a <c>label:</c> sits: the cursor position a <c>GOTO</c> resumes
/// dispatch at, plus the two nesting counts that decide which dispatch loop
/// services the jump and whether it is legal at all.
/// </summary>
/// <param name="checkpoint">The cursor position immediately after the label.</param>
/// <param name="blockDepth">
/// How many <c>BEGIN…END</c> blocks (<c>BEGIN TRY</c> / <c>BEGIN CATCH</c>
/// included) enclose it — which is the <see cref="BatchContext.BlockDepth"/> a
/// dispatch loop over it runs at.
/// </param>
/// <param name="tryScopes">The <c>TRY</c> / <c>CATCH</c> scopes enclosing it, outermost first, each by the order it opened in.</param>
internal sealed class LabelTarget(ParserContext.Checkpoint checkpoint, int blockDepth, int[] tryScopes)
{
    public readonly ParserContext.Checkpoint Checkpoint = checkpoint;
    public readonly int BlockDepth = blockDepth;
    public readonly int[] TryScopes = tryScopes;
}

public sealed partial class Simulation
{
    /// <summary>
    /// Collects every label a batch (or module body) declares, and validates
    /// every <c>GOTO</c> in it against that set, before the first statement
    /// runs. Real does the same pass while compiling, which is why an
    /// unreachable <c>GOTO nosuchlabel</c> aborts a batch whose earlier
    /// <c>PRINT</c> never produces output, and why a duplicate label is
    /// refused with no <c>GOTO</c> referencing it at all (both
    /// probe-confirmed against SQL Server 2025, 2026-08-08).
    /// </summary>
    /// <remarks>
    /// <para>The scan is a token walk, so it is skipped outright unless the
    /// batch's raw text carries something a label or a <c>GOTO</c> needs — see
    /// <see cref="ParserContext.MightCarryLabelsOrGoto"/>.</para>
    ///
    /// <para>A label is an <em>unquoted</em> identifier followed by a single
    /// <c>:</c> at parenthesis depth zero: real refuses the delimited spelling
    /// (<c>[my label]:</c> is Msg 102), the <c>::</c> of
    /// <c>hierarchyid::Parse</c> / <c>SCHEMA::x</c> is two adjacent operators,
    /// and the one other bare colon in the grammar — <c>JSON_OBJECT('a': 1)</c>
    /// — is always inside parentheses.</para>
    ///
    /// <para>TRY / CATCH scopes are tracked as a stack of ids so the
    /// jump-into-a-scope refusal (Msg 1026) can be settled here too: a label
    /// whose scope stack is not a prefix of the <c>GOTO</c>'s sits inside a
    /// scope the jump would enter — a <c>CATCH</c> from its own <c>TRY</c> body
    /// included (probed 2026-10-02 against SQL Server 2025).</para>
    /// </remarks>
    internal static void ScanBatchLabels(BatchContext batch)
    {
        var context = batch.Parser;
        batch.Labels = BatchContext.NoLabels;
        if (!context.MightCarryLabelsOrGoto)
            return;
        // A batch defining a procedure, function or trigger is that module's
        // body, whose own bind scans it and attributes what it finds to the
        // module (probed 2026-10-02 against SQL Server 2025).
        if (batch.ProcFrame is null && batch.UdfFrame is null && batch.TriggerFrame is null && OpensWithModuleDefinition(context))
            return;

        var entry = context.SaveCheckpoint();
        try
        {
            Dictionary<string, LabelTarget>? labels = null;
            List<(string Name, int[] TryScopes, int Line)>? gotos = null;
            // One stack for both nesting questions: a 'c' entry is a CASE
            // (whose END is not a block's), 'b' a BEGIN…END block, 't' a
            // BEGIN TRY / BEGIN CATCH, numbered so a TRY and its CATCH are
            // different scopes. 'b' and 't' are exactly the constructs that
            // open a nested dispatch loop.
            var open = new List<(char Kind, int Scope)>();
            var scopesOpened = 0;
            var parenDepth = 0;

            while (context.Token is not null)
            {
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        parenDepth++;
                        break;
                    case Operator { Character: ')' }:
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case ReservedKeyword { Keyword: Keyword.Case }:
                        open.Add(('c', 0));
                        break;
                    case ReservedKeyword { Keyword: Keyword.Begin }:
                        if (PeekAfterBeginOrEnd(context) is var after && after != BeginKind.Transaction)
                            open.Add(after == BeginKind.TryOrCatch ? ('t', ++scopesOpened) : ('b', 0));
                        break;
                    case ReservedKeyword { Keyword: Keyword.End }:
                        if (open.Count > 0)
                            open.RemoveAt(open.Count - 1);
                        break;
                    case ReservedKeyword { Keyword: Keyword.Goto } gotoKeyword:
                        if (context.GetNextOptional() is UnquotedString target)
                        {
                            gotos ??= [];
                            gotos.Add((target.Value, TryScopes(open), gotoKeyword.LineNumber));
                        }
                        break;
                    case UnquotedString candidate when parenDepth == 0:
                        {
                            var afterName = context.SaveCheckpoint();
                            if (IsSingleColon(context))
                            {
                                // Cursor now sits on the first token after the
                                // label, which is where a jump resumes.
                                labels ??= new(context.CurrentDatabase.Collation);
                                var declared = new LabelTarget(
                                    context.SaveCheckpoint(),
                                    Count(open, 'b') + Count(open, 't'),
                                    TryScopes(open));
                                if (!labels.TryAdd(candidate.Value, declared))
                                    throw AtLine(SimulatedSqlException.DuplicateLabel(candidate.Value), candidate.LineNumber);
                                continue;
                            }
                            context.RestoreCheckpoint(afterName);
                        }
                        break;
                }
                context.MoveNextOptional();
            }

            if (gotos is not null)
            {
                foreach (var (name, tryScopes, line) in gotos)
                {
                    if (labels is null || !labels.TryGetValue(name, out var declared))
                        throw AtLine(SimulatedSqlException.UndeclaredLabel(name), context.LastLine);
                    // A label whose TRY / CATCH scopes aren't all the jump's
                    // own sits inside one the jump would enter — a TRY's
                    // CATCH from its TRY body included.
                    if (declared.TryScopes.Length > tryScopes.Length || !tryScopes.AsSpan(0, declared.TryScopes.Length).SequenceEqual(declared.TryScopes))
                        throw AtLine(SimulatedSqlException.GotoCannotJumpIntoTryOrCatch(), line);
                }
            }

            if (labels is not null)
                batch.Labels = labels;
        }
        finally
        {
            context.RestoreCheckpoint(entry);
        }

        // The scan runs ahead of any statement's dispatch, so it stamps the
        // line itself: the duplicate label's, the offending GOTO's for Msg
        // 1026, and the batch's last for a missing label, which real meets
        // once it has read to the end (probed 2026-10-02 against SQL Server
        // 2025).
        SimulatedSqlException AtLine(SimulatedSqlException error, int line)
        {
            error.ResolveDiagnostics(line, batch.LineOffset, batch.ErrorProcedureName);
            return error;
        }
    }

    /// <summary>
    /// Whether the cursor opens <c>CREATE</c>, <c>ALTER</c> or <c>CREATE OR
    /// ALTER</c> of a procedure, function or trigger. Leaves the cursor where
    /// it found it.
    /// </summary>
    private static bool OpensWithModuleDefinition(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter })
            return false;
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        if (next is ReservedKeyword { Keyword: Keyword.Or } && context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Alter })
            next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next is ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc or Keyword.Function or Keyword.Trigger };
    }

    /// <summary>What a <c>BEGIN</c> opens.</summary>
    private enum BeginKind
    {
        /// <summary>A <c>BEGIN…END</c> statement block.</summary>
        Block,

        /// <summary><c>BEGIN TRY</c> / <c>BEGIN CATCH</c>.</summary>
        TryOrCatch,

        /// <summary><c>BEGIN [DISTRIBUTED] TRAN[SACTION]</c> — no block at all.</summary>
        Transaction,
    }

    /// <summary>
    /// Classifies the <c>BEGIN</c> at the cursor from the word after it,
    /// leaving the cursor where it found it.
    /// </summary>
    private static BeginKind PeekAfterBeginOrEnd(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var kind = context.GetNextOptional() switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Try or ContextualKeyword.Catch } => BeginKind.TryOrCatch,
            ReservedKeyword { Keyword: Keyword.Transaction or Keyword.Tran or Keyword.Distributed } => BeginKind.Transaction,
            _ => BeginKind.Block,
        };
        context.RestoreCheckpoint(checkpoint);
        return kind;
    }

    private static int Count(List<(char Kind, int Scope)> open, char kind)
    {
        var total = 0;
        foreach (var (entryKind, _) in open)
        {
            if (entryKind == kind)
                total++;
        }
        return total;
    }

    private static int[] TryScopes(List<(char Kind, int Scope)> open)
    {
        var scopes = new int[Count(open, 't')];
        var next = 0;
        foreach (var (kind, scope) in open)
        {
            if (kind == 't')
                scopes[next++] = scope;
        }
        return scopes;
    }

    /// <summary>
    /// Consumes a lone <c>:</c> from the cursor, leaving it on the token after
    /// — the shape that follows a label's name. A <c>::</c> (the
    /// <c>hierarchyid::Parse</c> / <c>SCHEMA::x</c> separator, two adjacent
    /// operators) reads false and leaves the cursor mid-pair for the caller to
    /// restore.
    /// </summary>
    private static bool IsSingleColon(ParserContext context)
    {
        if (context.GetNextOptional() is not Operator { Character: ':' })
            return false;
        return context.GetNextOptional() is not Operator { Character: ':' };
    }

    /// <summary>
    /// Parses <c>GOTO label</c>. The label is validated by
    /// <see cref="ScanBatchLabels"/> while the batch compiles, so all that
    /// remains here is to raise the signal the dispatch loop unwinds on.
    /// </summary>
    private static void ParseGotoStatement(BatchContext batch)
    {
        var context = batch.Parser;
        if (context.GetNextRequired() is not UnquotedString target)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        if (!batch.IsSkipping)
            batch.PendingGotoLabel = target.Value;
    }

    /// <summary>
    /// Consumes a <c>label:</c> declaration, which does nothing when execution
    /// simply flows through it.
    /// </summary>
    private static void ParseLabelDeclaration(ParserContext context)
    {
        context.MoveNextRequired(); // the ':'
        context.MoveNextOptional();
    }

    /// <summary>
    /// Whether the statement at the cursor is a <c>label:</c> declaration —
    /// an unquoted identifier followed by a single colon. Leaves the cursor
    /// where it found it.
    /// </summary>
    private static bool IsLabelDeclaration(ParserContext context)
    {
        if (context.Token is not UnquotedString)
            return false;
        var checkpoint = context.SaveCheckpoint();
        var isLabel = IsSingleColon(context);
        context.RestoreCheckpoint(checkpoint);
        return isLabel;
    }
}
