using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Classifies the statement the dispatch loop is about to run for real's
    /// function body-shape rules, and records what it settles: the line Msg 455
    /// would report, whether this statement satisfies the last-statement rule,
    /// and whether its leading keyword is one of the side-effecting operators
    /// Msg 443 names. Called only while a scalar UDF's / multi-statement TVF's
    /// body is being bound (<see cref="BatchContext.FunctionBodyShape"/>).
    /// </summary>
    /// <returns>
    /// True for a construct whose contained statements must not settle the
    /// last-statement rule (<c>IF</c> / <c>WHILE</c>), so the caller brackets
    /// the dispatch with <see cref="FunctionBodyShape.ConditionalDepth"/>.
    /// </returns>
    private static bool NoteFunctionBodyStatement(BatchContext batch, FunctionBodyShape shape)
    {
        shape.LastStatementLine = batch.CurrentStatement.StartLine;
        shape.StatementReadsData = false;

        var isReturn = false;
        var isTransparentBlock = false;
        var opensConditional = false;
        switch (batch.Parser.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Return }:
                isReturn = true;
                break;
            case ReservedKeyword { Keyword: Keyword.Begin }:
                isTransparentBlock = NoteFunctionBodyBeginStatement(batch);
                break;
            case ReservedKeyword { Keyword: Keyword.If or Keyword.While }:
                opensConditional = true;
                break;
            case ReservedKeyword { Keyword: Keyword.Print }:
                FunctionBodyShape.NoteSideEffect(batch, "PRINT", FunctionBodyShape.ControlOperatorState);
                break;
            case ReservedKeyword { Keyword: Keyword.RaisError }:
                FunctionBodyShape.NoteSideEffect(batch, "RAISERROR", FunctionBodyShape.ControlOperatorState);
                break;
            case ReservedKeyword { Keyword: Keyword.WaitFor }:
                FunctionBodyShape.NoteSideEffect(batch, "WAITFOR", FunctionBodyShape.ControlOperatorState);
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Throw }:
                FunctionBodyShape.NoteSideEffect(batch, "THROW", FunctionBodyShape.ControlOperatorState);
                break;
            case ReservedKeyword { Keyword: Keyword.Truncate }:
                FunctionBodyShape.NoteSideEffect(batch, "TRUNCATE TABLE", FunctionBodyShape.StatementOperatorState);
                break;
            case ReservedKeyword { Keyword: Keyword.Commit }:
                FunctionBodyShape.NoteSideEffect(batch, "COMMIT TRANSACTION", FunctionBodyShape.StatementOperatorState);
                break;
            case ReservedKeyword { Keyword: Keyword.Rollback }:
                FunctionBodyShape.NoteSideEffect(batch, "ROLLBACK TRANSACTION", FunctionBodyShape.StatementOperatorState);
                break;
            case ReservedKeyword { Keyword: Keyword.Save }:
                FunctionBodyShape.NoteSideEffect(batch, "SAVEPOINT", FunctionBodyShape.StatementOperatorState);
                break;
            default:
                break;
        }

        // A bare BEGIN … END is transparent: its own statements decide the rule
        // (probe-confirmed that a trailing block ending in RETURN is accepted),
        // so the block statement itself neither satisfies nor breaks it.
        if (!isTransparentBlock && shape.ConditionalDepth == 0)
            shape.LastStatementIsReturn = isReturn;
        return opensConditional;
    }

    /// <summary>
    /// Disambiguates the three statements that open with <c>BEGIN</c>, the same
    /// peek the dispatch switch does: a transaction start and a <c>TRY</c>
    /// block are each their own Msg 443 operator, while a compound block is
    /// transparent to the last-statement rule.
    /// </summary>
    private static bool NoteFunctionBodyBeginStatement(BatchContext batch)
    {
        var context = batch.Parser;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextOptional();
        var afterBegin = context.Token;
        context.RestoreCheckpoint(checkpoint);
        switch (afterBegin)
        {
            case ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed }:
                FunctionBodyShape.NoteSideEffect(batch, "BEGIN TRANSACTION", FunctionBodyShape.StatementOperatorState);
                return false;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Try }:
                FunctionBodyShape.NoteSideEffect(batch, "BEGIN TRY", FunctionBodyShape.ControlOperatorState);
                return false;
            default:
                return true;
        }
    }
}
