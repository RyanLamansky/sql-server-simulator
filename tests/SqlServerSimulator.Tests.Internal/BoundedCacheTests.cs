using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="BoundedCache{TKey, TValue}"/>'s policy, which the compile-side
/// memos share and no SQL observes except through what each one costs.
/// </summary>
[TestClass]
public sealed class BoundedCacheTests
{
    [TestMethod]
    public void UnderItsCapacity_EveryKeyIsAdmittedOnArrival()
    {
        var cache = new BoundedCache<int, string>(8);
        for (var i = 0; i < 8; i++)
            IsTrue(cache.TryAdd(i, "v"));
        AreEqual(8, cache.Count);
    }

    [TestMethod]
    public void PastItsCapacity_TheLeastRecentlyUsedHalfGoes()
    {
        var cache = new BoundedCache<int, string>(8);
        for (var i = 0; i < 8; i++)
            _ = cache.TryAdd(i, "v");
        // 6 and 7 were added last, 0 and 1 are touched after them.
        IsTrue(cache.TryGetValue(0, out _));
        IsTrue(cache.TryGetValue(1, out _));
        cache.Set(100, "v");
        cache.Set(100, "w");
        _ = cache.TryAdd(101, "v");
        _ = cache.TryAdd(101, "v");
        IsLessThanOrEqualTo(8, cache.Count);
        IsTrue(cache.ContainsKey(0));
        IsTrue(cache.ContainsKey(1));
        IsFalse(cache.ContainsKey(2));
        IsGreaterThan(0L, cache.Evictions);
    }

    [TestMethod]
    public void AFullCache_AdmitsANewKeyOnItsSecondArrival()
    {
        var cache = new BoundedCache<int, string>(8);
        for (var i = 0; i < 8; i++)
            _ = cache.TryAdd(i, "v");
        IsFalse(cache.TryAdd(50, "v"));
        IsFalse(cache.ContainsKey(50));
        IsTrue(cache.TryAdd(50, "v"));
        IsTrue(cache.TryGetValue(50, out var value));
        AreEqual("v", value);
    }

    [TestMethod]
    public void ReplacingAKey_NeedsNoAdmission()
    {
        var cache = new BoundedCache<int, string>(8);
        for (var i = 0; i < 8; i++)
            _ = cache.TryAdd(i, "v");
        cache.Set(3, "w");
        IsTrue(cache.TryGetValue(3, out var value));
        AreEqual("w", value);
        AreEqual(8, cache.Count);
    }

    [TestMethod]
    public void APinnedEntry_IsNeverEvicted_AndTakesNoCapacity()
    {
        var cache = new BoundedCache<int, string>(8);
        IsTrue(cache.TryAdd(-1, "pinned", pinned: true));
        for (var i = 0; i < 100; i++)
        {
            _ = cache.TryAdd(i, "v");
            _ = cache.TryAdd(i, "v");
        }
        IsTrue(cache.ContainsKey(-1));
        IsLessThanOrEqualTo(8, cache.Count);
    }

    [TestMethod]
    public void RemoveWhere_DropsTheMatchingKeys()
    {
        var cache = new BoundedCache<int, string>(8);
        for (var i = 0; i < 6; i++)
            _ = cache.TryAdd(i, "v");
        cache.RemoveWhere(2, static (modulus, key) => key % modulus == 0);
        AreEqual(3, cache.Count);
        IsFalse(cache.ContainsKey(4));
        IsTrue(cache.ContainsKey(5));
        cache.Clear();
        AreEqual(0, cache.Count);
    }
}
