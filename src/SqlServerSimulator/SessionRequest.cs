namespace SqlServerSimulator;

/// <summary>
/// One request a session is serving — a TDS MARS request or an in-process
/// command — which real tracks two ways, its MARS refusals each turning on
/// one of them (probed 2026-10-06 against SQL Server 2025):
/// <list type="bullet">
/// <item>Running on the server until its response has gone out: a rollback
/// of the transaction it works in ends it with Msg 3998 and refuses other
/// requests with Msg 3989 meanwhile, and a DML statement's <c>OUTPUT</c> rows
/// still going out refuse them with Msg 3980 (see
/// <see cref="SimulatedDbConnection.RefuseNewRequest"/>).</item>
/// <item>Outstanding on the client until it has read past the response's end,
/// which SqlClient counts in each request's transaction-descriptor header: a
/// transaction-manager begin with another outstanding is Msg 3988, and a
/// commit or save point with another outstanding in the transaction is
/// Msg 3981 — even for a one-row result the server sent whole (see
/// <see cref="SimulatedDbConnection.OtherRequestsOutstanding"/>).</item>
/// </list>
/// </summary>
internal sealed class SessionRequest(bool inProcess, bool consumed)
{
    /// <summary>
    /// Whether an in-process command execution opened this request, rather
    /// than a TDS MARS request arriving.
    /// </summary>
    public readonly bool InProcess = inProcess;

    /// <summary>
    /// The transaction open when the request began executing, 0 for none: the
    /// one it works on. A transaction begun after it doesn't enlist it, so an
    /// autocommit reader leaves another request free to begin and commit its
    /// own.
    /// </summary>
    public long EnlistedTransactionId;

    /// <summary>
    /// How many requests the client counted outstanding when it sent this
    /// one, itself included: the transaction-descriptor header's
    /// <c>OutstandingRequestCount</c>. Unused in process, where the
    /// connection keeps the count itself (see <see cref="Consumed"/>).
    /// </summary>
    public int OutstandingRequestCount = 1;

    /// <summary>
    /// Whether the client has read past the end of the request's response,
    /// after which it no longer counts as outstanding. Only an in-process
    /// reader starts without: every other consumer reads a response whole,
    /// and a TDS client reports its count instead.
    /// </summary>
    public bool Consumed = consumed;

    /// <summary>Whether the request's response has all gone out, so it is no longer running on the server.</summary>
    public bool Finished;

    /// <summary>
    /// Set when another request ended <see cref="EnlistedTransactionId"/>
    /// while this one was running; this request's response then ends with
    /// Msg 3998.
    /// </summary>
    public bool TransactionEndedUnder;

    /// <summary>
    /// How far a MARS response runs ahead of a client still reading it: real
    /// sends about 32 KB before waiting on the client, the four 8000-byte
    /// packets of the window the TDS endpoint grants too. In process, the
    /// batch runs on past a result set that fits, and a DML statement's
    /// <c>OUTPUT</c> rows that fit hold the session not at all.
    /// </summary>
    public const int BytesAheadOfClient = 4 * 8000;

    /// <summary>
    /// Set while a DML statement's <c>OUTPUT</c> rows are still on their way
    /// to the client: real can't suspend such a statement to run another
    /// request. Over TDS it clears as the statement's last bytes go out; in
    /// process, as the reader moves off its result set.
    /// </summary>
    public volatile bool HoldsSession;

    /// <summary>
    /// The session's published settings the request began executing from
    /// (see <see cref="SimulatedDbConnection.PublishedSettings"/>); null for
    /// a request that hasn't executed, and in process.
    /// </summary>
    public SessionSettings? StartSettings;

    /// <summary>
    /// The settings the request's execution left, its own copy of the
    /// session's, published over the session's once its response has gone
    /// out; null until then, and in process.
    /// </summary>
    public SessionSettings? Settings;
}
