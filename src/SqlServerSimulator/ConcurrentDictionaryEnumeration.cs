using System.Collections.Concurrent;

namespace SqlServerSimulator;

/// <summary>
/// Lock-free replacements for <see cref="ConcurrentDictionary{TKey, TValue}.Values"/>
/// and <see cref="ConcurrentDictionary{TKey, TValue}.Keys"/>, for a LINQ chain
/// or argument where a deconstructing <c>foreach</c> doesn't fit, and for
/// <see cref="ConcurrentDictionary{TKey, TValue}.IsEmpty"/>. All walk the
/// dictionary's own enumerator: no lock and no copy, but a moving view that can
/// observe or miss a concurrent add or remove, where the properties they
/// replace take every bucket lock (SSS012).
/// </summary>
internal static class ConcurrentDictionaryEnumeration
{
    public static IEnumerable<TValue> EnumerateValues<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        foreach (var (_, value) in dictionary)
            yield return value;
    }

    /// <summary>
    /// Whether <paramref name="dictionary"/> holds no entry, answered by its
    /// enumerator's first step. <see cref="ConcurrentDictionary{TKey, TValue}.IsEmpty"/>
    /// takes every bucket lock whenever the answer is yes, which is the common
    /// answer where a hot path asks it as a guard (a table no session holds a
    /// superseded key on, a heap with nothing reclaimable).
    /// </summary>
    public static bool IsEmptyLockFree<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        foreach (var _ in dictionary)
            return false;
        return true;
    }
}
