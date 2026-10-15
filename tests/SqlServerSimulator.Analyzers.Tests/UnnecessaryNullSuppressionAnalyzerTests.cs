using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class UnnecessaryNullSuppressionAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<UnnecessaryNullSuppressionAnalyzer, DefaultVerifier>
        {
            TestCode = "#nullable enable\n" + source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task NonNullableParameterAndLocal_Report() =>
        RunAsync("""
            internal static class C
            {
                public static int M(string s)
                {
                    string t = s;
                    return {|SSS017:s!|}.Length + {|SSS017:t!|}.Length;
                }
            }
            """);

    [TestMethod]
    public Task NonNullableMembers_Report() =>
        RunAsync("""
            internal sealed class C
            {
                private readonly string field = "";
                public string Property { get; } = "";
                public string Method() => "";
                public int M() => {|SSS017:this.field!|}.Length + {|SSS017:Property!|}.Length + {|SSS017:Method()!|}.Length;
            }
            """);

    [TestMethod]
    public Task NotNullExpressions_Report() =>
        RunAsync("""
            internal sealed class C
            {
                public object[] M(int value) =>
                [
                    {|SSS017:this!|},
                    {|SSS017:new object()!|},
                    {|SSS017:"text"!|},
                    {|SSS017:$"{value}"!|},
                    {|SSS017:typeof(C)!|},
                    {|SSS017:nameof(M)!|},
                    {|SSS017:value!|},
                ];
            }
            """);

    [TestMethod]
    public Task NullableDeclarations_DoNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                private string? field;
                public string Name = null!;
                public static T Get<T>() => default!;
                public int M(string? s, int? n) => s!.Length + this.field!.Length + n!.Value;
            }
            """);

    // Flow analysis has proven the nullable local not-null, but the semantic
    // model reports state after the suppression, so the rule can't see it.
    [TestMethod]
    public Task NullableCheckedByFlow_DoesNotReport() =>
        RunAsync("""
            internal static class C
            {
                public static int M(string? s)
                {
                    if (s is null)
                        return 0;
                    return s!.Length;
                }
            }
            """);

    // A '!' on a generic or array operand may be silencing a nested mismatch.
    [TestMethod]
    public Task NestedNullability_DoesNotReport() =>
        RunAsync("""
            using System.Collections.Generic;
            internal static class C
            {
                public static List<string> M(List<string?> list) => list!;
                public static string[] O(string?[] array) => array!;
                public static List<string> P(List<string> list) => list!;
            }
            """);

    [TestMethod]
    public Task TypeParameter_DoesNotReport() =>
        RunAsync("""
            internal static class C
            {
                public static string? M<T>(T value) => value!.ToString();
            }
            """);

    // A struct's default leaves its fields null whatever they're declared as.
    [TestMethod]
    public Task StructMember_DoesNotReport() =>
        RunAsync("""
            internal readonly struct S
            {
                public readonly string Name;
                public S(string name) => Name = name;
            }
            internal static class C
            {
                public static int M()
                {
                    var value = default(S);
                    return value.Name!.Length;
                }
            }
            """);

    // Flow analysis starts an instance field maybe-null in a constructor.
    [TestMethod]
    public Task FieldReadInConstructor_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                private readonly string field;
                public C()
                {
                    var length = this.field!.Length;
                    this.field = length.ToString();
                }
            }
            """);

    [TestMethod]
    public Task NullabilityAttributes_DoNotReport() =>
        RunAsync("""
            using System.Diagnostics.CodeAnalysis;
            internal sealed class C
            {
                [MaybeNull] public string Property { get; set; } = "";
                [return: MaybeNull] public string Method() => null;
                public int M([AllowNull] string s) => s!.Length + Property!.Length + Method()!.Length;
            }
            """);

    [TestMethod]
    public Task NullableDisabled_DoesNotReport() =>
        RunAsync("""
            #nullable disable
            internal static class C
            {
                public static int M(string s) => s!.Length;
            }
            """);

    [TestMethod]
    public Task OutArgument_DoesNotReport() =>
        RunAsync("""
            internal static class C
            {
                private static void Set(out string s) => s = "";
                public static void M(string value) => Set(out value!);
            }
            """);
}
