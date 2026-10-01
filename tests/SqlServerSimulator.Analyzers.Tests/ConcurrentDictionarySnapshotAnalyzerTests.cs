using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class ConcurrentDictionarySnapshotAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<ConcurrentDictionarySnapshotAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task ValuesInForeach_Reports() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal static class Holder
            {
                public static int Sum(ConcurrentDictionary<string, int> dict)
                {
                    var total = 0;
                    foreach (var value in dict.{|SSS012:Values|})
                        total += value;
                    return total;
                }
            }
            """);

    [TestMethod]
    public Task KeysInForeach_Reports() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal static class Holder
            {
                public static int Length(ConcurrentDictionary<string, int> dict)
                {
                    var total = 0;
                    foreach (var key in dict.{|SSS012:Keys|})
                        total += key.Length;
                    return total;
                }
            }
            """);

    // A LINQ chain copies just the same: the property is the cost, not the loop.
    [TestMethod]
    public Task ValuesInLinqChain_Reports() =>
        RunAsync("""
            using System.Collections.Concurrent;
            using System.Linq;
            internal static class Holder
            {
                public static bool Any(ConcurrentDictionary<string, int> dict) => dict.{|SSS012:Values|}.Any(v => v > 0);
            }
            """);

    // Values read off a field, through a derived type, is still the base
    // property.
    [TestMethod]
    public Task DerivedDictionaryValues_Reports() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal sealed class Registry : ConcurrentDictionary<int, string>;
            internal sealed class Holder
            {
                private readonly Registry registry = new();
                public int Count() => this.registry.{|SSS012:Values|}.Count;
            }
            """);

    // IsEmpty copies nothing, but sweeps every lock whenever it answers yes.
    [TestMethod]
    public Task IsEmpty_Reports() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal static class Holder
            {
                public static bool None(ConcurrentDictionary<string, int> dict) => dict.{|SSS012:IsEmpty|};
            }
            """);

    // The enumerator's first step is the lock-free answer to the same question.
    [TestMethod]
    public Task FirstEnumerationStep_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal static class Holder
            {
                public static bool None(ConcurrentDictionary<string, int> dict)
                {
                    foreach (var _ in dict)
                        return false;
                    return true;
                }
            }
            """);

    [TestMethod]
    public Task EnumeratingTheDictionary_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Concurrent;
            using System.Linq;
            internal static class Holder
            {
                public static int Sum(ConcurrentDictionary<string, int> dict)
                {
                    var total = 0;
                    foreach (var (_, value) in dict)
                        total += value;
                    return total + dict.Select(p => p.Value).Sum();
                }
            }
            """);

    // The explicit snapshot is the documented escape hatch, and Count copies
    // nothing.
    [TestMethod]
    public Task ExplicitSnapshotAndCount_DoNotReport() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal static class Holder
            {
                public static int Drain(ConcurrentDictionary<string, int> dict)
                {
                    var drained = 0;
                    foreach (var (key, _) in dict.ToArray())
                    {
                        if (dict.TryRemove(key, out _))
                            drained++;
                    }
                    return drained + dict.Count;
                }
            }
            """);

    [TestMethod]
    public Task PragmaSuppressedSite_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            internal static class Holder
            {
                public static ICollection<int> Copy(ConcurrentDictionary<string, int> dict)
                {
            #pragma warning disable SSS012 // the caller keeps the copy as a point-in-time record
                    return dict.Values;
            #pragma warning restore SSS012
                }
            }
            """);

    // Dictionary<,>.Values is a view over the live table, not a copy.
    [TestMethod]
    public Task PlainDictionaryValues_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                public static int Sum(Dictionary<string, int> dict)
                {
                    var total = 0;
                    foreach (var value in dict.Values)
                        total += value;
                    return total;
                }
            }
            """);

    // A project-local type that happens to share the name isn't the rule's business.
    [TestMethod]
    public Task SameNamedLocalType_DoesNotReport() =>
        RunAsync("""
            internal sealed class ConcurrentDictionary<TKey, TValue>
            {
                public TValue[] Values => [];
            }
            internal static class Holder
            {
                public static int Count(ConcurrentDictionary<string, int> dict) => dict.Values.Length;
            }
            """);
}
