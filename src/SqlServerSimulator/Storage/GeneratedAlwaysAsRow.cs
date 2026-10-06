namespace SqlServerSimulator.Storage;

/// <summary>
/// Classifies a column's <c>GENERATED ALWAYS AS …</c> declaration. Only
/// system-versioned temporal tables declare <c>ROW</c> period columns; the
/// parent table has exactly one <c>Start</c> and one <c>End</c> column, named
/// in its <c>PERIOD FOR SYSTEM_TIME (start, end)</c> clause. The history
/// table's matching columns mirror the parent's types and the same flags but
/// aren't engine-populated (history rows carry the parent's chosen values).
/// The <c>TRANSACTION_ID</c> / <c>SEQUENCE_NUMBER</c> kinds may sit on any
/// table, at most one of each (<see cref="GeneratedAlwaysKinds.IsLedger"/>).
/// </summary>
internal enum GeneratedAlwaysAsRow
{
    None = 0,
    Start,
    End,

    // The ledger-style columns real accepts on any table, valued as
    // sys.columns.generated_always_type reports them (probed 2026-10-06
    // against SQL Server 2025): the writing transaction's id and the row
    // operation's number within it.
    TransactionIdStart = 7,
    TransactionIdEnd,
    SequenceNumberStart,
    SequenceNumberEnd,
}

internal static class GeneratedAlwaysKinds
{
    /// <summary>Whether <paramref name="kind"/> is one of the <c>TRANSACTION_ID</c> / <c>SEQUENCE_NUMBER</c> kinds.</summary>
    public static bool IsLedger(this GeneratedAlwaysAsRow kind) => kind >= GeneratedAlwaysAsRow.TransactionIdStart;

    /// <summary>Whether <paramref name="kind"/> is a <c>ROW START</c> / <c>ROW END</c> period column.</summary>
    public static bool IsPeriod(this GeneratedAlwaysAsRow kind) => kind is GeneratedAlwaysAsRow.Start or GeneratedAlwaysAsRow.End;

    /// <summary>The declaration as real's messages spell it: <c>GENERATED ALWAYS AS TRANSACTION_ID START</c>.</summary>
    public static string Spelling(this GeneratedAlwaysAsRow kind) => kind switch
    {
        GeneratedAlwaysAsRow.TransactionIdStart => "GENERATED ALWAYS AS TRANSACTION_ID START",
        GeneratedAlwaysAsRow.TransactionIdEnd => "GENERATED ALWAYS AS TRANSACTION_ID END",
        GeneratedAlwaysAsRow.SequenceNumberStart => "GENERATED ALWAYS AS SEQUENCE_NUMBER START",
        GeneratedAlwaysAsRow.SequenceNumberEnd => "GENERATED ALWAYS AS SEQUENCE_NUMBER END",
        GeneratedAlwaysAsRow.End => "GENERATED ALWAYS AS ROW END",
        _ => "GENERATED ALWAYS AS ROW START",
    };
}
