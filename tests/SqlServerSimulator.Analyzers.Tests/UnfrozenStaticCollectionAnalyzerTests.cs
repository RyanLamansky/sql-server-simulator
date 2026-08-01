using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class UnfrozenStaticCollectionAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<UnfrozenStaticCollectionAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task StaticDictionary_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                private static readonly Dictionary<string, int> {|SSS008:Codes|} = new() { ["a"] = 1 };
                public static int Read(string key) => Codes[key];
            }
            """);

    [TestMethod]
    public Task StaticHashSet_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                private static readonly HashSet<string> {|SSS008:Names|} = ["a"];
                public static bool Has(string name) => Names.Contains(name);
            }
            """);

    [TestMethod]
    public Task StaticList_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                private static readonly List<int> {|SSS008:Values|} = [1, 2];
                public static int Count => Values.Count;
            }
            """);

    [TestMethod]
    public Task StaticSortedDictionary_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                private static readonly SortedDictionary<string, int> {|SSS008:Codes|} = new();
                public static int Count => Codes.Count;
            }
            """);

    // The Lazy wrapper defers construction; it doesn't make the contents any
    // less fixed, so the collection inside it is judged on its own terms.
    [TestMethod]
    public Task LazyWrappedDictionary_Reports() =>
        RunAsync("""
            using System;
            using System.Collections.Generic;
            internal static class Holder
            {
                private static readonly Lazy<Dictionary<string, int>> {|SSS008:Codes|} = new(() => new Dictionary<string, int>());
                public static int Read(string key) => Codes.Value[key];
            }
            """);

    // Already immutable, but a per-lookup tree walk is the wrong price for
    // contents that were never going to change.
    [TestMethod]
    public Task ImmutableDictionary_Reports() =>
        RunAsync("""
            using System.Collections.Immutable;
            internal static class Holder
            {
                private static readonly ImmutableDictionary<string, int> {|SSS008:Codes|} = ImmutableDictionary<string, int>.Empty;
                public static int Count => Codes.Count;
            }
            """);

    // Every declarator in a multi-field declaration is judged separately.
    [TestMethod]
    public Task MultipleDeclarators_ReportEach() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                private static readonly List<int> {|SSS008:First|} = [1], {|SSS008:Second|} = [2];
                public static int Total => First.Count + Second.Count;
            }
            """);

    // ImmutableArray is an array; the rule's whole point is that arrays are
    // already the cheapest fixed layout. (Built with Create rather than a
    // collection expression: the default test reference set ships an
    // ImmutableArray predating collection-expression support.)
    [TestMethod]
    public Task ImmutableArray_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Immutable;
            internal static class Holder
            {
                private static readonly ImmutableArray<int> Values = ImmutableArray.Create(1, 2);
                public static int Count => Values.Length;
            }
            """);

    [TestMethod]
    public Task Array_DoesNotReport() =>
        RunAsync("""
            internal static class Holder
            {
                private static readonly int[] Values = [1, 2];
                public static int Count => Values.Length;
            }
            """);

    // The shapes the rule steers toward. Declared in source because the
    // default test reference set predates System.Collections.Frozen — which
    // changes nothing for the analyzer, since it matches on type name and
    // namespace rather than on the defining assembly.
    [TestMethod]
    public Task FrozenCollections_DoNotReport() =>
        RunAsync("""
            using System.Collections.Frozen;
            namespace System.Collections.Frozen
            {
                internal sealed class FrozenDictionary<TKey, TValue> { }
                internal sealed class FrozenSet<T> { }
            }
            internal static class Holder
            {
                private static readonly FrozenDictionary<string, int> Codes = new();
                private static readonly FrozenSet<string> Names = new();
                public static object Read() => (Codes, Names);
            }
            """);

    // Concurrent collections exist to be written after publication — a static
    // memo cache is the intended use, not a violation.
    [TestMethod]
    public Task ConcurrentDictionary_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Concurrent;
            internal static class Holder
            {
                private static readonly ConcurrentDictionary<string, int> Memo = new();
                public static int Read(string key) => Memo.GetOrAdd(key, static _ => 0);
            }
            """);

    // Per-object state with a normal mutable lifetime.
    [TestMethod]
    public Task InstanceField_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class Holder
            {
                private readonly Dictionary<string, int> codes = [];
                public int Read(string key) => this.codes[key];
            }
            """);

    // A reassignable static holds whatever the last write put there, so its
    // declared type has to stay general.
    [TestMethod]
    public Task NonReadonlyStaticField_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class Holder
            {
                private static List<int>? sink;
                public static void Start() => sink = [];
                public static int Count => sink is null ? 0 : sink.Count;
            }
            """);

    // The name switch is only a filter; a project-local type that happens to
    // share a collection's name is not this rule's business.
    [TestMethod]
    public Task ProjectLocalTypeSharingName_DoesNotReport() =>
        RunAsync("""
            namespace Local
            {
                internal sealed class Dictionary<TKey, TValue> { }

                internal static class Holder
                {
                    private static readonly Dictionary<string, int> Codes = new();
                    public static object Read() => Codes;
                }
            }
            """);
}
