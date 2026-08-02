using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class WrapperPropertyAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    // Records need a reference set carrying IsExternalInit, which the testing
    // library's default (a pre-.NET 5 framework) doesn't have.
    //
    // The property and positional-record diagnostics share the SSS001 id across
    // two descriptors, so `{|SSS001:x|}` markup is ambiguous to the testing
    // library. UseFirstDescriptor picks one to build the expected diagnostic
    // from; matching is by id, severity and location, which the two agree on.
    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<WrapperPropertyAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            MarkupOptions = MarkupOptions.UseFirstDescriptor,
        }.RunAsync(this.TestContext.CancellationToken);

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

    // Static auto-properties on non-public types have the same overhead as
    // instance ones — a backing field plus a getter method, no API-stability
    // benefit. Convert to `public static readonly int Value = 42;`.
    [TestMethod]
    public Task StaticAutoProperty_Reports() =>
        RunAsync("""
            internal static class C
            {
                public static int {|SSS001:Value|} { get; } = 42;
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

    // A positional parameter list declares one auto-property per parameter with
    // no property syntax to bind to, so the diagnostic lands on the record name
    // and the whole list is the fix.
    [TestMethod]
    public Task PositionalRecordStruct_OnInternalType_Reports() =>
        RunAsync("""
            internal readonly record struct {|SSS001:S|}(int Value, string Name);
            """);

    [TestMethod]
    public Task PositionalRecordClass_OnInternalType_Reports() =>
        RunAsync("""
            internal sealed record {|SSS001:C|}(int Value);
            """);

    [TestMethod]
    public Task PositionalRecord_OnPublicType_DoesNotReport() =>
        RunAsync("""
            public sealed record C(int Value);
            """);

    [TestMethod]
    public Task PositionalRecordStruct_OnPublicType_DoesNotReport() =>
        RunAsync("""
            public readonly record struct S(int Value);
            """);

    // No parameter list means no positional properties. What the body declares
    // is ordinary property syntax, which the property path already covers.
    [TestMethod]
    public Task ParameterlessRecord_DoesNotReport() =>
        RunAsync("""
            internal sealed record C
            {
                public readonly int Value;
                public C(int value) => this.Value = value;
            }
            """);

    [TestMethod]
    public Task ParameterlessRecord_WithBodyAutoProperty_ReportsOnProperty() =>
        RunAsync("""
            internal sealed record C
            {
                public int {|SSS001:Value|} { get; init; }
            }
            """);

    // The derived record's parameter feeds the base constructor; `Value` stays
    // the base's property, so the derived record adds no metadata of its own.
    [TestMethod]
    public Task DerivedRecord_ForwardingAllParametersToBase_DoesNotReport() =>
        RunAsync("""
            internal record {|SSS001:B|}(int Value);
            internal sealed record D(int Value) : B(Value);
            """);

    // A derived record that adds a parameter of its own does declare a property
    // for it, so it is flagged alongside the base.
    [TestMethod]
    public Task DerivedRecord_AddingParameter_Reports() =>
        RunAsync("""
            internal record {|SSS001:B|}(int Value);
            internal sealed record {|SSS001:D|}(int Value, string Name) : B(Value);
            """);

    // The shape the rule asks for: plain readonly fields and an explicit
    // constructor, on a record that is otherwise left alone (SSS009's business,
    // not this rule's).
    [TestMethod]
    public Task RecordWithPlainFields_DoesNotReport() =>
        RunAsync("""
            internal sealed record C
            {
                public readonly int Value;
                public readonly string Name;
                public C(int value, string name)
                {
                    this.Value = value;
                    this.Name = name;
                }
            }
            """);
}
