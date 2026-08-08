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
/// <param name="tryDepth">How many of those are <c>TRY</c> / <c>CATCH</c> scopes.</param>
internal sealed class LabelTarget(ParserContext.Checkpoint checkpoint, int blockDepth, int tryDepth)
{
    public readonly ParserContext.Checkpoint Checkpoint = checkpoint;
    public readonly int BlockDepth = blockDepth;
    public readonly int TryDepth = tryDepth;
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
    /// scope the jump would enter.</para>
    /// </remarks>
    internal static void ScanBatchLabels(BatchContext batch)
    {
        var context = batch.Parser;
        batch.Labels = BatchContext.NoLabels;
        if (!context.MightCarryLabelsOrGoto)
            return;

        var entry = context.SaveCheckpoint();
        try
        {
            Dictionary<string, LabelTarget>? labels = null;
            List<(string Name, int TryDepth)>? gotos = null;
            // One stack for both nesting questions: a 'c' entry is a CASE
            // (whose END is not a block's), 'b' a BEGIN…END block, 't' a
            // BEGIN TRY / BEGIN CATCH. 'b' and 't' are exactly the constructs
            // that open a nested dispatch loop.
            var open = new List<char>();
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
                        open.Add('c');
                        break;
                    case ReservedKeyword { Keyword: Keyword.Begin }:
                        if (PeekAfterBeginOrEnd(context) is var after && after != BeginKind.Transaction)
                            open.Add(after == BeginKind.TryOrCatch ? 't' : 'b');
                        break;
                    case ReservedKeyword { Keyword: Keyword.End }:
                        if (open.Count > 0)
                            open.RemoveAt(open.Count - 1);
                        break;
                    case ReservedKeyword { Keyword: Keyword.Goto }:
                        if (context.GetNextOptional() is UnquotedString target)
                        {
                            gotos ??= [];
                            gotos.Add((target.Value, Count(open, 't')));
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
                                    Count(open, 't'));
                                if (!labels.TryAdd(candidate.Value, declared))
                                    throw SimulatedSqlException.DuplicateLabel(candidate.Value);
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
                foreach (var (name, tryDepth) in gotos)
                {
                    if (labels is null || !labels.TryGetValue(name, out var declared))
                        throw SimulatedSqlException.UndeclaredLabel(name);
                    // A label enclosed in more TRY / CATCH scopes than the jump
                    // is sits inside one the jump would enter.
                    if (declared.TryDepth > tryDepth)
                        throw SimulatedSqlException.GotoCannotJumpIntoTryOrCatch();
                }
            }

            if (labels is not null)
                batch.Labels = labels;
        }
        finally
        {
            context.RestoreCheckpoint(entry);
        }
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

    private static int Count(List<char> open, char kind)
    {
        var total = 0;
        foreach (var entry in open)
        {
            if (entry == kind)
                total++;
        }
        return total;
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
