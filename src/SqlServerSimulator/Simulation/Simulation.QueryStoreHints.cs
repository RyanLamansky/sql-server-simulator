using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// Query Store hint application: the hints sp_query_store_set_hints set on a
// query take effect the next time a statement that is that query compiles —
// see docs/claude/database-options.md#query-store.
partial class Simulation
{
    /// <summary>
    /// The hint the store holds for the query the statement settling its
    /// hints is — read off the statement's text, which runs from its start to
    /// the token before the cursor — or null when it holds none or the
    /// statement isn't compiling to run.
    /// </summary>
    internal static QueryStoreHint? QueryStoreHintFor(BatchContext batch)
    {
        if (batch.IsSkipping || batch.CreateTimeBinding || (batch.SuppressDiagnosticsResolution && !batch.CapturesQueryStore))
            return null;
        var database = batch.CurrentDatabase;
        var data = database.QueryStoreData;
        if (database.QueryStore.DesiredState == QueryStoreState.Off || data.Hints.Count == 0)
            return null;
        var parser = batch.Parser;
        var start = batch.CurrentStatement.StartIndex;
        var end = parser.PreviousTokenEnd;
        // A MERGE's terminator is part of its text, as its capture reads it.
        if (parser.Token is Operator { Character: ';' } separator && separator.StartIndex == end
            && FirstTokenIs(parser.Command.CommandText, start, "MERGE"))
        {
            end = separator.EndIndex;
        }
        if (start < 0 || end <= start || end > parser.Command.CommandText.Length
            || QueryStoreKeyOf(batch, database, start, end, tokensFrom: null, out _, out _, out _) is not { } key)
        {
            return null;
        }
        lock (data.Gate)
        {
            return data.QueriesByKey.TryGetValue(key, out var query) ? data.Hints.Find(hint => hint.QueryId == query.QueryId) : null;
        }
    }

    /// <summary>
    /// Records that <paramref name="hint"/> couldn't be applied — its join
    /// hints leave no plan (Msg 8622, <c>NO_PLAN</c>) — so the statement
    /// compiled without it, as real records it in
    /// <c>sys.query_store_query_hints</c> (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    internal static void NoteQueryStoreHintFailure(BatchContext batch, QueryStoreHint hint, int reason)
    {
        lock (batch.CurrentDatabase.QueryStoreData.Gate)
        {
            hint.FailureReason = reason;
            hint.FailureCount++;
        }
    }
}
