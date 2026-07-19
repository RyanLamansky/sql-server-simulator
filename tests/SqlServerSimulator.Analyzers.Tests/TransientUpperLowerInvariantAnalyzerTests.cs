using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class TransientUpperLowerInvariantAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<TransientUpperLowerInvariantAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task SwitchExpressionGovern_OnString_Reports() =>
        RunAsync("""
            internal static class Holder
            {
                public static int Resolve(string s) => s.{|SSS003:ToUpperInvariant|}() switch
                {
                    "X" => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task SwitchStatementGovern_OnString_Reports() =>
        RunAsync("""
            internal static class Holder
            {
                public static int Resolve(string s)
                {
                    switch (s.{|SSS003:ToLowerInvariant|}())
                    {
                        case "x": return 1;
                        default: return 0;
                    }
                }
            }
            """);

    [TestMethod]
    public Task ParenthesizedSwitchGovern_StillReports() =>
        RunAsync("""
            internal static class Holder
            {
                public static int Resolve(string s) => (s.{|SSS003:ToUpperInvariant|}()) switch
                {
                    "X" => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task SpanOverload_Allowed_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(string s)
                {
                    Span<char> buf = stackalloc char[s.Length];
                    return s.AsSpan().ToUpperInvariant(buf) switch
                    {
                        // The Span overload returns the count written; switch
                        // on the length, then read the buffer separately.
                        1 => 1,
                        _ => 0,
                    };
                }
            }
            """);

    [TestMethod]
    public Task ResultUsedAsString_DoesNotReport() =>
        RunAsync("""
            internal static class Holder
            {
                // The upper-cased string IS the function's output — no transient
                // dispatch, no allocation to elide.
                public static string Up(string s) => s.ToUpperInvariant();
            }
            """);

    [TestMethod]
    public Task ResultPipedThroughLength_DoesNotReport() =>
        RunAsync("""
            internal static class Holder
            {
                // Switch governs '.Length', not the upper-cased string itself.
                public static int Resolve(string s) => s.ToUpperInvariant().Length switch
                {
                    0 => 1,
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task ResultStoredFirst_DoesNotReport() =>
        RunAsync("""
            internal static class Holder
            {
                // Storing in a local breaks the "transient" pattern — the rule
                // is conservative; only flag the direct-feed shape.
                public static int Resolve(string s)
                {
                    var upper = s.ToUpperInvariant();
                    return upper switch
                    {
                        "X" => 1,
                        _ => 0,
                    };
                }
            }
            """);

    [TestMethod]
    public Task SpanReceiver_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static int Resolve(ReadOnlySpan<char> s)
                {
                    Span<char> buf = stackalloc char[s.Length];
                    // s.ToUpperInvariant(buf) is the Span overload — not the
                    // string method we're flagging. Should not report.
                    return s.ToUpperInvariant(buf) switch
                    {
                        1 => 1,
                        _ => 0,
                    };
                }
            }
            """);
}
