using System.Collections.Concurrent;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Where each statement of one batch text ends, by the index its first token
/// starts at: its own last character (its tight end) and the character
/// before the next statement, or -1 for one running to the text's end
/// (its loose end) — which of the two real reports depends on the
/// statement's kind, settled where it is read (<c>LockReports.StatementEnd</c>).
/// The compile walk records them as it reads each statement whole; one the
/// walk stopped inside or never reached is recorded by the batch's first run,
/// while other sessions read the published ends.
/// </summary>
internal sealed class StatementEnds
{
    /// <summary>The walk's own ends, unchanged once published.</summary>
    private readonly Dictionary<int, (int Tight, int Loose)> walked = [];

    /// <summary>The ends the batch's run recorded after publication, for the statements the walk left out.</summary>
    private ConcurrentDictionary<int, (int Tight, int Loose)>? ran;

    /// <summary>Whether the walk left a statement's end unrecorded, which hands recording on to the run.</summary>
    public bool Incomplete;

    /// <summary>
    /// The statements carrying a secret the walk or a run met, each by the
    /// span its mask covers and its label (see <see cref="SecretStatements"/>);
    /// null when none did.
    /// </summary>
    private List<(int Start, int End, string Label)>? secrets;

    /// <summary>The text with each secret statement masked, built once it is asked for.</summary>
    private string? masked;

    /// <summary>Records that the statement at <paramref name="start"/> carries a secret, masked through <paramref name="end"/> under <paramref name="label"/>.</summary>
    public void RecordSecret(int start, int end, string label)
    {
        lock (this.walked)
        {
            this.secrets ??= [];
            if (!this.secrets.Exists(secret => secret.Start == start))
                this.secrets.Add((start, end, label));
            this.masked = null;
        }
    }

    /// <summary>Whether every secret the text's statements carry is a query's.</summary>
    public bool SecretsAreQueries
    {
        get
        {
            lock (this.walked)
                return this.secrets is { } held && held.TrueForAll(static secret => secret.Label == "SELECT WITHOUT QUERY");
        }
    }

    /// <summary>Whether a statement of the text carries a secret.</summary>
    public bool HoldsSecret => Volatile.Read(ref this.secrets) is { Count: > 0 };

    /// <summary><paramref name="text"/>, this one's batch text, with its secret statements masked; null when it carries none.</summary>
    public string? MaskedText(string text)
    {
        lock (this.walked)
        {
            if (this.secrets is not { Count: > 0 } held)
                return null;
            return this.masked ??= SecretStatements.Mask(text, held);
        }
    }

    private volatile bool published;

    /// <summary>
    /// Records the end of the statement starting at <paramref name="start"/>,
    /// answering whether it is new: the walk's, or past publication one a run
    /// supplies for a statement the walk left out.
    /// </summary>
    public bool Record(int start, int tight, int loose)
    {
        if (!this.published)
            return this.walked.TryAdd(start, (tight, loose));
        return !this.walked.ContainsKey(start) && this.ran is { } run && run.TryAdd(start, (tight, loose));
    }

    /// <summary>Seals the walk's ends for other sessions to read, a run filling in any it left out.</summary>
    public void Publish()
    {
        if (this.Incomplete)
            this.ran = new ConcurrentDictionary<int, (int, int)>(concurrencyLevel: 1, capacity: 4);
        this.published = true;
    }

    /// <summary>Whether the walk recorded nothing.</summary>
    public bool IsEmpty => this.walked.Count == 0;

    public bool TryGet(int start, out (int Tight, int Loose) end) =>
        this.walked.TryGetValue(start, out end) || (this.ran is { } run && run.TryGetValue(start, out end));
}
