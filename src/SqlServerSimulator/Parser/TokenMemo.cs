using SqlServerSimulator.Storage;
using System.Collections.Concurrent;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-<see cref="Simulation"/> memo of tokenized command texts: the
/// <see cref="Token"/> sequence a <see cref="ParserContext"/> walks, keyed by
/// everything <see cref="Tokenizer.NextToken"/> reads. A repeat execution of
/// the same text re-parses, but scans no characters and allocates no tokens.
/// <para>
/// This is the plan cache's counterpart for the statements that have no plan
/// object. A <see cref="Selection"/> can be cached because SELECT's parse
/// produces a re-executable artifact; INSERT / UPDATE / DELETE / MERGE parse
/// and execute in one interleaved pass, so there is nothing to store and
/// replay (see <c>docs/claude/plan-cache.md</c>). Their token sequence,
/// though, is the same on every execution, and is the one part of the
/// front half that can be shared with no replay-safety question at all.
/// </para>
/// <para>
/// <b>No invalidation.</b> Tokenization is a pure function of the four key
/// components, so an entry can never go stale — unlike a cached plan, which
/// stamps <see cref="Simulation.SchemaVersion"/> because it holds resolved
/// schema objects. A memo holds only tokens, which name schema objects
/// without resolving them.
/// </para>
/// </summary>
internal sealed class TokenMemo
{
    /// <summary>
    /// Hard cap, matching the plan cache's: once full, new texts tokenize
    /// live forever. "First <see cref="Capacity"/> unique texts win" is the
    /// same defensive policy documented there — a stable application's set of
    /// distinct command texts (queries, modification batches, module bodies)
    /// is far smaller.
    /// </summary>
    private const int Capacity = 1024;

    private readonly ConcurrentDictionary<TokenMemoKey, Token[]> entries = new();

    /// <summary>Test-observable: memo lookups that found a token sequence.</summary>
    public long Hits;

    /// <summary>Test-observable: memo lookups that had to tokenize live.</summary>
    public long Misses;

    /// <summary>Test-observable: live entry count.</summary>
    public int Count => this.entries.Count;

    /// <summary>
    /// The stored token sequence for <paramref name="key"/>, or
    /// <see langword="null"/> when the text hasn't been tokenized under these
    /// inputs yet. The returned array is shared with every other execution of
    /// the same text and must not be mutated.
    /// </summary>
    public Token[]? TryGet(in TokenMemoKey key)
    {
        if (this.entries.TryGetValue(key, out var tokens))
        {
            _ = Interlocked.Increment(ref this.Hits);
            return tokens;
        }

        _ = Interlocked.Increment(ref this.Misses);
        return null;
    }

    /// <summary>
    /// Whether a fresh text is worth collecting tokens for. Checked before
    /// the collecting <see cref="List{T}"/> is allocated so a full memo costs
    /// a lookup and nothing else.
    /// </summary>
    public bool HasCapacity => this.entries.Count < Capacity;

    /// <summary>
    /// Stores a completed token sequence. Only ever called with the tokens of
    /// a text the tokenizer walked to its end without raising, so a memo
    /// never holds a partial scan — which is what keeps a mid-text
    /// tokenization error (Msg 102 / 103 / 105 / 113) firing at exactly the
    /// character it fired at on the first execution.
    /// </summary>
    public void Publish(in TokenMemoKey key, Token[] tokens)
    {
        if (this.entries.Count < Capacity)
            _ = this.entries.TryAdd(key, tokens);
    }
}

/// <summary>
/// Identity of a tokenization: every input <see cref="Tokenizer.NextToken"/>
/// reads. The command text and the <c>QUOTED_IDENTIFIER</c> setting decide
/// the token shapes; the collation tags string-literal
/// <see cref="SqlValue"/>s; the compatibility level decides which words are
/// reserved. Nothing else reaches the tokenizer, which is why an entry needs
/// no version stamp.
/// </summary>
internal readonly struct TokenMemoKey(string commandText, Collation collation, CompatibilityLevel compatibilityLevel, bool quotedIdentifiers)
    : IEquatable<TokenMemoKey>
{
    public readonly string CommandText = commandText;

    /// <summary>
    /// Compared by reference: a database holds one collation instance, and
    /// re-collating it installs a different one, so identity is the exact test
    /// wanted and costs no string compare.
    /// </summary>
    public readonly Collation Collation = collation;

    public readonly CompatibilityLevel CompatibilityLevel = compatibilityLevel;

    public readonly bool QuotedIdentifiers = quotedIdentifiers;

    // Implemented rather than inherited for the same reason PlanCacheKey
    // implements it: ValueType.Equals would box both sides and compare them
    // by reflection on every dictionary probe.
    public bool Equals(TokenMemoKey other) =>
        this.QuotedIdentifiers == other.QuotedIdentifiers
        && this.CompatibilityLevel == other.CompatibilityLevel
        && ReferenceEquals(this.Collation, other.Collation)
        && string.Equals(this.CommandText, other.CommandText, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TokenMemoKey other && this.Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(this.CommandText, this.Collation, this.CompatibilityLevel, this.QuotedIdentifiers);
}
