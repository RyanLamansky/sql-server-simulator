using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class WrapperPropertyAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<WrapperPropertyAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task AutoProperty_OnInternalType_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                public string {|SSS001:Name|} { get; } = "x";
            }
            """);

    [TestMethod]
    public Task AutoProperty_WithSetter_OnInternalType_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                public int {|SSS001:Count|} { get; set; }
            }
            """);

    [TestMethod]
    public Task AutoProperty_OnPublicType_DoesNotReport() =>
        RunAsync("""
            public sealed class C
            {
                public string Name { get; } = "x";
            }
            """);

    [TestMethod]
    public Task TrivialWrapper_OnInternalType_Reports() =>
        RunAsync("""
            using System.Collections.Generic;
            internal sealed class C
            {
                private readonly List<int> items = new();
                public IReadOnlyList<int> {|SSS001:Items|} => this.items;
            }
            """);

    [TestMethod]
    public Task TrivialWrapper_WithoutThis_OnInternalType_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                private readonly int value = 42;
                public int {|SSS001:Value|} => value;
            }
            """);

    [TestMethod]
    public Task BlockGetter_SingleReturn_OnInternalType_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                private readonly int value = 42;
                public int {|SSS001:Value|}
                {
                    get { return this.value; }
                }
            }
            """);

    [TestMethod]
    public Task TrivialWrapper_OnPublicType_DoesNotReport() =>
        RunAsync("""
            public sealed class C
            {
                private readonly int value = 42;
                public int Value => this.value;
            }
            """);

    [TestMethod]
    public Task ComputedProperty_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                private readonly int a = 1, b = 2;
                public int Sum => this.a + this.b;
            }
            """);

    [TestMethod]
    public Task MethodCall_NotField_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                private readonly int value = 42;
                public int Hash => this.value.GetHashCode();
            }
            """);

    [TestMethod]
    public Task AbstractProperty_DoesNotReport() =>
        RunAsync("""
            internal abstract class C
            {
                public abstract int Value { get; }
            }
            """);

    [TestMethod]
    public Task ImplicitInterfaceImplementation_DoesNotReport() =>
        RunAsync("""
            using System.Collections;
            using System.Collections.Generic;
            internal sealed class C : IReadOnlyCollection<int>
            {
                public int Count { get; } = 0;
                public IEnumerator<int> GetEnumerator() => System.Linq.Enumerable.Empty<int>().GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
            }
            """);

    [TestMethod]
    public Task PropertyMatchingInterfaceMemberOnUnimplementedInterface_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                // Same name as System.Collections.ICollection.Count, but C
                // doesn't implement that interface, so the property is plain
                // metadata over a backing field — should still be flagged.
                public int {|SSS001:Count|} { get; } = 0;
            }
            """);

    [TestMethod]
    public Task OverrideProperty_DoesNotReport() =>
        RunAsync("""
            internal abstract class B
            {
                public abstract string Name { get; }
            }
            internal sealed class C : B
            {
                private readonly string name = "x";
                public override string Name => this.name;
            }
            """);

    [TestMethod]
    public Task StaticProperty_DoesNotReport() =>
        RunAsync("""
            internal static class C
            {
                public static int Value { get; } = 42;
            }
            """);

    // A public type nested inside a non-public type is not effectively public.
    [TestMethod]
    public Task NestedPublicInsideInternal_Reports() =>
        RunAsync("""
            internal static class Outer
            {
                public sealed class Inner
                {
                    public int {|SSS001:Value|} { get; } = 1;
                }
            }
            """);

    // The wrapped field belongs to a base, not the property's containing type.
    [TestMethod]
    public Task FieldOfDifferentType_DoesNotReport() =>
        RunAsync("""
            internal class B
            {
                protected internal readonly int value = 1;
            }
            internal sealed class C : B
            {
                public int Value => this.value;
            }
            """);

    [TestMethod]
    public Task ReadonlyStruct_AutoProperty_Reports() =>
        RunAsync("""
            internal readonly struct S
            {
                public int {|SSS001:Value|} { get; }
                public S(int value) => this.Value = value;
            }
            """);
}
