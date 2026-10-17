using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// SET FIPS_FLAGGER's statement warnings, read as the dispatch loop meets each
// statement.
partial class Simulation
{
    /// <summary>
    /// Flags the statement the parser stands at for the session's
    /// <c>FIPS_FLAGGER</c> level as real's parser does while the batch
    /// compiles: Msg 1021 naming the statement, at its start or — for the
    /// statements whose warning follows the name they read, a <c>WITH</c>'s
    /// first common table expression's included — after that name, once the
    /// statement has been read without error
    /// (<see cref="BatchContext.SettleNonAnsiStatement"/>). The set is the one
    /// probed 2026-10-17 against SQL Server 2025 at all three levels, which
    /// flag alike but for <c>DEALLOCATE</c>, flagged above <c>ENTRY</c>; an
    /// <c>IF</c> is flagged at its <c>ELSE</c> and a <c>WHILE</c> past its
    /// body, a <c>SET FIPS_FLAGGER</c> by its own parse.
    /// </summary>
    private static void FlagNonAnsiStatement(BatchContext batch)
    {
        var context = batch.Parser;
        if (!context.FlagsNonAnsi || context.Token is not { } first)
            return;
        batch.NoteFipsStatement(first.StartIndex, first.LineNumber);
        if (NonAnsiStatement(context, first, batch.Connection.FipsFlagger) is not ({ } name, var late, var state))
            return;
        // A CREATE's own second word may read as a name (SEQUENCE, ROLE …).
        var warning = SimulatedSqlException.FipsNonAnsiWarning(batch, 1021, (byte)state, "statement", name, first.LineNumber, first.LineNumber);
        if (late)
            batch.FlagStatementAtEnd(first.StartIndex, (FirstNameAfter(context, skip: first is ReservedKeyword { Keyword: Keyword.Create } ? 1 : 0) * 2L) + 1, warning);
        else
            batch.FlagNonAnsi(first.StartIndex * 2L, warning);
    }

    /// <summary>
    /// Where the first name after the parser's token and the
    /// <paramref name="skip"/> tokens after it starts, within the next few
    /// tokens, or where those tokens end when none is a name.
    /// </summary>
    private static int FirstNameAfter(ParserContext context, int skip)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var end = context.Token?.EndIndex ?? 0;
            for (var i = 0; i < 4 + skip && context.GetNextOptional() is { } next; i++)
            {
                if (next is Name && i >= skip)
                    return next.StartIndex;
                end = next.EndIndex;
            }
            return end;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// The statement <paramref name="first"/> opens, as Msg 1021 names it,
    /// whether its warning follows what it reads, and the warning's state —
    /// 50 for a <c>SET</c> of an on/off option, 1 otherwise; null for a
    /// statement no level flags.
    /// </summary>
    private static (string? Name, bool Late, int State) NonAnsiStatement(ParserContext context, Token first, byte level) => first switch
    {
        ReservedKeyword { Keyword: Keyword.Alter } => PeekKeyword(context) switch
        {
            Keyword.Database => ("ALTER DATABASE", true, 1),
            Keyword.Function => ("ALTER FUNCTION", false, 1),
            Keyword.Index => ("ALTER INDEX", true, 1),
            Keyword.Proc or Keyword.Procedure => ("ALTER PROCEDURE", false, 1),
            Keyword.Table => ("ALTER TABLE", false, 1),
            _ => default,
        },
        ReservedKeyword { Keyword: Keyword.Backup } when PeekKeyword(context) == Keyword.Database => ("BACKUP DATABASE", true, 1),
        ReservedKeyword { Keyword: Keyword.Begin } when PeekKeyword(context) is Keyword.Distributed or Keyword.Tran or Keyword.Transaction => ("BEGIN TRANSACTION", false, 1),
        ReservedKeyword { Keyword: Keyword.Break } => ("BREAK", false, 1),
        ReservedKeyword { Keyword: Keyword.Checkpoint } => ("CHECKPOINT", true, 1),
        ReservedKeyword { Keyword: Keyword.Continue } => ("CONTINUE", false, 1),
        ReservedKeyword { Keyword: Keyword.Create } => CreateStatement(context),
        ReservedKeyword { Keyword: Keyword.Dbcc } => ("DBCC", false, 1),
        ReservedKeyword { Keyword: Keyword.Deallocate } when level > FipsLevel.Entry => ("DEALLOCATE", false, 1),
        ReservedKeyword { Keyword: Keyword.Drop } => PeekKeyword(context) == Keyword.Index ? ("DROP INDEX", true, 1) : ("DROP", true, 1),
        ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute } => Peek(context) switch
        {
            Operator { Character: '(' } => ("EXECUTE", false, 1),
            ReservedKeyword { Keyword: Keyword.As } => ("EXECUTE AS", false, 1),
            _ => ("EXECUTE", true, 1),
        },
        ReservedKeyword { Keyword: Keyword.Goto } => ("GOTO", true, 1),
        ReservedKeyword { Keyword: Keyword.Kill } => ("KILL", false, 1),
        ReservedKeyword { Keyword: Keyword.Print } => ("PRINT", false, 1),
        ReservedKeyword { Keyword: Keyword.RaisError } => ("RAISERROR", false, 1),
        ReservedKeyword { Keyword: Keyword.ReadText } => ("READTEXT", false, 1),
        ReservedKeyword { Keyword: Keyword.Reconfigure } => ("RECONFIGURE", false, 1),
        ReservedKeyword { Keyword: Keyword.Return } => ("RETURN", false, 1),
        ReservedKeyword { Keyword: Keyword.Save } => ("SAVE TRANSACTION", false, 1),
        ReservedKeyword { Keyword: Keyword.Set } => Peek(context) switch
        {
            AtPrefixedString or DoubleAtPrefixedString => default,
            UnquotedString { Value: var option } when option.Equals("FIPS_FLAGGER", StringComparison.OrdinalIgnoreCase) => default,
            UnquotedString { Value: var option } when RecognizedOptions.TryGetValue(option, out var kind) && kind == SetOptionKind.OnOff => ("SET", true, 50),
            _ => ("SET", true, 1),
        },
        ReservedKeyword { Keyword: Keyword.Truncate } => ("TRUNCATE TABLE", true, 1),
        ReservedKeyword { Keyword: Keyword.Update } when PeekKeyword(context) == Keyword.Statistics => ("UPDATE STATISTICS", false, 1),
        ReservedKeyword { Keyword: Keyword.Use } => ("USE", false, 1),
        ReservedKeyword { Keyword: Keyword.WaitFor } => ("WAITFOR", false, 1),
        ReservedKeyword { Keyword: Keyword.With } => ("WITH", true, 1),
        UnquotedString { ContextualKeyword: ContextualKeyword.Disable } when PeekKeyword(context) == Keyword.Trigger => ("DISABLE TRIGGER", false, 1),
        UnquotedString { ContextualKeyword: ContextualKeyword.Enable } when PeekKeyword(context) == Keyword.Trigger => ("ENABLE TRIGGER", false, 1),
        UnquotedString { ContextualKeyword: ContextualKeyword.Throw } => ("THROW", false, 1),
        _ => default,
    };

    /// <summary>The <c>CREATE</c> statements a level flags, by the word after <c>CREATE</c> (and an index's options).</summary>
    private static (string? Name, bool Late, int State) CreateStatement(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var next = context.GetNextOptional();
            // CREATE OR ALTER names the CREATE it makes.
            if (next is ReservedKeyword { Keyword: Keyword.Or } && context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Alter })
                next = context.GetNextOptional();
            while (next is ReservedKeyword { Keyword: Keyword.Unique or Keyword.Clustered or Keyword.NonClustered })
                next = context.GetNextOptional();
            return next switch
            {
                ReservedKeyword { Keyword: Keyword.Database } => ("CREATE DATABASE", true, 1),
                ReservedKeyword { Keyword: Keyword.Function } => ("CREATE FUNCTION", false, 1),
                ReservedKeyword { Keyword: Keyword.Index } => ("CREATE INDEX", false, 1),
                ReservedKeyword { Keyword: Keyword.Proc or Keyword.Procedure } => ("CREATE PROCEDURE", false, 1),
                ReservedKeyword { Keyword: Keyword.Schema } => ("CREATE SCHEMA", false, 1),
                ReservedKeyword { Keyword: Keyword.Trigger } => ("CREATE TRIGGER", false, 1),
                ReservedKeyword { Keyword: Keyword.User } => ("CREATE USER", true, 1),
                UnquotedString { ContextualKeyword: ContextualKeyword.Login } => ("CREATE LOGIN", true, 1),
                UnquotedString { ContextualKeyword: ContextualKeyword.Role } => ("CREATE ROLE", true, 1),
                UnquotedString { ContextualKeyword: ContextualKeyword.Sequence } => ("CREATE SEQUENCE", true, 1),
                UnquotedString { Value: var word } when word.Equals("SYNONYM", StringComparison.OrdinalIgnoreCase) => ("CREATE SYNONYM", true, 1),
                UnquotedString { ContextualKeyword: ContextualKeyword.Type } => ("CREATE TYPE", false, 1),
                _ => default,
            };
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>The token after the parser's, which it leaves in place.</summary>
    private static Token? Peek(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            return context.GetNextOptional();
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>The reserved keyword after the parser's token, null when the next token is none.</summary>
    private static Keyword? PeekKeyword(ParserContext context) => Peek(context) is ReservedKeyword { Keyword: var keyword } ? keyword : null;
}
