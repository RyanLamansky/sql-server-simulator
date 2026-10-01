using System.Collections.Concurrent;
using System.Globalization;

namespace SqlServerSimulator.Parser;

/// <summary>
/// An invariant-culture name comparer under <c>options</c> whose hash is
/// memoized by ordinal text — what keys a batch's variables, table variables
/// and cursor variables (<see cref="BatchContext.VariableNameComparer"/>,
/// <see cref="BatchContext.TableVariableNameComparer"/>).
/// </summary>
/// <remarks>
/// The culture-aware hash builds an ICU sort key, about 80 ns for a short
/// name, paid by every lookup — which added up to a fifth of a batch naming
/// many parameters (EF Core's multi-row insert), since it seeds, binds and
/// reads each one through <see cref="BatchContext.Variables"/> and Query
/// Store reads each again. The hash is a pure function of the name's text, so
/// a hit in the memo costs a few nanoseconds instead. No cheaper exact hash
/// exists: the equivalence reaches across ASCII (<c>fi</c> equals the
/// <c>ﬁ</c> ligature, and <c>ab</c> equals <c>a</c>, soft hyphen, <c>b</c>),
/// so only the sort key agrees across a class. The memo is bounded in entries
/// and name length and stops growing when full, past which a name pays the
/// sort key again.
/// </remarks>
internal sealed class MemoizedNameComparer(CompareOptions options) : StringComparer
{
    private const int MemoCapacity = 4096;
    private const int MemoMaxLength = 128;

    private readonly StringComparer culture = Create(CultureInfo.InvariantCulture, options);

    private readonly ConcurrentDictionary<string, int> hashes = new(Ordinal);

    private int memoized;

    public override int Compare(string? x, string? y) => this.culture.Compare(x, y);

    // Identical text is always equal, which settles the common case — a
    // name spelled as it was declared — without the culture compare.
    public override bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal) || this.culture.Equals(x, y);

    public override int GetHashCode(string obj)
    {
        if (this.hashes.TryGetValue(obj, out var hash))
            return hash;
        hash = this.culture.GetHashCode(obj);
        if (obj.Length <= MemoMaxLength && Volatile.Read(ref this.memoized) < MemoCapacity && this.hashes.TryAdd(obj, hash))
            _ = Interlocked.Increment(ref this.memoized);
        return hash;
    }
}
