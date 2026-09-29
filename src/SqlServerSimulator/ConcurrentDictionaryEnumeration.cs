using System.Collections.Concurrent;

namespace SqlServerSimulator;

/// <summary>
/// Lock-free replacements for <see cref="ConcurrentDictionary{TKey, TValue}.Values"/>
/// and <see cref="ConcurrentDictionary{TKey, TValue}.Keys"/>, for a LINQ chain
/// or argument where a deconstructing <c>foreach</c> doesn't fit. Both walk the
/// dictionary's own enumerator: no lock and no copy, but a moving view that can
/// observe or miss a concurrent add or remove, where the properties they
/// replace take every bucket lock and copy the contents (SSS012).
/// </summary>
internal static class ConcurrentDictionaryEnumeration
{
    public static IEnumerable<TValue> EnumerateValues<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        foreach (var (_, value) in dictionary)
            yield return value;
    }

    public static IEnumerable<TKey> EnumerateKeys<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        foreach (var (key, _) in dictionary)
            yield return key;
    }
}
