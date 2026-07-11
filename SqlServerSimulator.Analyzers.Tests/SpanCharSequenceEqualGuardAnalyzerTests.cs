using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class SpanCharSequenceEqualGuardAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<SpanCharSequenceEqualGuardAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task ReadOnlySpanGuard_Reports() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s) => s switch
                {
                    _ when {|SSS007:s.SequenceEqual("X")|} => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task WritableSpanGuard_Reports() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(string input)
                {
                    Span<char> s = stackalloc char[input.Length];
                    _ = input.AsSpan().ToUpperInvariant(s);
                    return s switch
                    {
                        _ when {|SSS007:s.SequenceEqual("ABC")|} => 1,
                        _ => 0,
                    };
                }
            }
            """);

    [TestMethod]
    public Task ConstantPatternForm_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s) => s switch
                {
                    "X" => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task GuardOnDifferentSpan_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s, ReadOnlySpan<char> other) => s switch
                {
                    _ when other.SequenceEqual("X") => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task NonConstantArgument_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s, string other) => s switch
                {
                    _ when s.SequenceEqual(other) => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task NegatedGuard_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s) => s switch
                {
                    _ when !s.SequenceEqual("X") => 0,
                    _ => 1,
                };
            }
            """);

    [TestMethod]
    public Task CombinedGuard_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s) => s switch
                {
                    _ when s.SequenceEqual("X") && s.Length > 0 => 1,
                    _ => 0,
                };
            }
            """);
}
