namespace SqlServerSimulator.Parser;

/// <summary>
/// The <c>SET FIPS_FLAGGER</c> levels a session carries
/// (<see cref="SimulatedDbConnection.FipsFlagger"/>).
/// </summary>
internal static class FipsLevel
{
    public const byte Off = 0;
    public const byte Entry = 1;
    public const byte Intermediate = 2;
    public const byte Full = 3;
}

/// <summary>
/// What <c>SET FIPS_FLAGGER</c> flags inside a statement, as real's parser
/// meets it while the batch compiles (probed 2026-10-17 against SQL Server
/// 2025): Msg 1022 for <c>TOP</c> on a <c>SELECT</c>, <c>INSERT</c> or
/// <c>UPDATE</c> at every level, state 62, and for <c>CASE</c> —
/// <c>COALESCE</c> and <c>NULLIF</c> among it — under <c>ENTRY</c>, state 6;
/// Msg 1024 for <c>CONVERT</c> and <c>TRY_CONVERT</c> below <c>FULL</c>,
/// state 5, and <c>CURRENT_TIMESTAMP</c> and <c>CURRENT_DATE</c> under
/// <c>ENTRY</c>, state 6 — each where it is written. The statements themselves are flagged by the dispatch loop
/// (<c>Simulation.FlagNonAnsiStatement</c>), an identifier longer than 18
/// characters by the parser as it reads it
/// (<see cref="ParserContext.NoteNonAnsiIdentifier"/>).
/// </summary>
internal static class FipsFlagger
{
    /// <summary>
    /// Flags the <c>TOP</c> written at <paramref name="top"/>, its count read:
    /// the warning follows the token after the count, as real's lexer has read
    /// that token by then.
    /// </summary>
    internal static void FlagTop(ParserContext context, Token top)
    {
        if (context.FlagsNonAnsi)
            Flag(context, 1022, 62, "clause", "TOP", top.StartIndex, ((context.Token?.StartIndex ?? context.PreviousTokenEnd) * 2L) + 1);
    }

    /// <summary>Flags a <c>CASE</c>, <c>COALESCE</c> or <c>NULLIF</c> starting at <paramref name="at"/> under <c>ENTRY</c>.</summary>
    internal static void FlagCase(ParserContext context, int at)
    {
        if (context.FlagsNonAnsi && context.Connection.FipsFlagger == FipsLevel.Entry)
            Flag(context, 1022, 6, "clause", "CASE", at);
    }

    /// <summary>
    /// Flags the built-in named <paramref name="upperName"/> (in capitals)
    /// that starts at <paramref name="at"/> when the session's level flags it.
    /// </summary>
    internal static void FlagFunction(ParserContext context, ReadOnlySpan<char> upperName, int at)
    {
        if (!context.FlagsNonAnsi)
            return;
        var level = context.Connection.FipsFlagger;
        switch (upperName)
        {
            case "COALESCE":
                FlagCase(context, at);
                break;
            case "CONVERT":
                if (level < FipsLevel.Full)
                    Flag(context, 1024, 5, "function", "convert", at);
                break;
            case "CURRENT_DATE":
                if (level == FipsLevel.Entry)
                    Flag(context, 1024, 6, "function", "current_date", at);
                break;
            case "CURRENT_TIMESTAMP":
                if (level == FipsLevel.Entry)
                    Flag(context, 1024, 6, "function", "current_timestamp", at);
                break;
            case "NULLIF":
                FlagCase(context, at);
                break;
            case "TRY_CONVERT":
                if (level < FipsLevel.Full)
                    Flag(context, 1024, 5, "function", "try_convert", at);
                break;
        }
    }

    private static void Flag(ParserContext context, int number, byte state, string kind, string name, int at, long? key = null)
    {
        var batch = context.Batch;
        var line = Token.LineAt(context.Command.CommandText, at);
        batch.FlagNonAnsi(key ?? (at * 2L), SimulatedSqlException.FipsNonAnsiWarning(batch, number, state, kind, name, line, line));
    }
}
