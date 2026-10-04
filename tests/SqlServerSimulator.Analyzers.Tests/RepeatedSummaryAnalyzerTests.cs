using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class RepeatedSummaryAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<RepeatedSummaryAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    // A member inserted between another's documentation and its declaration.
    [TestMethod]
    public Task DisplacedSummary_Reports() =>
        RunAsync("""
            internal static class Holder
            {
                /// <summary>Adds the two.</summary>
                /// {|SSS016:<summary>|}Doubles the value.</summary>
                public static int Double(int value) => value * 2;

                public static int Add(int a, int b) => a + b;
            }
            """);

    [TestMethod]
    public Task ThirdSummary_ReportsOnceEach() =>
        RunAsync("""
            internal static class Holder
            {
                /// <summary>One.</summary>
                /// <remarks>Detail.</remarks>
                /// {|SSS016:<summary>|}Two.</summary>
                /// <summary>Three.</summary>
                public static void Method() { }
            }
            """);

    [TestMethod]
    public Task OneSummaryPerComment_DoesNotReport() =>
        RunAsync("""
            internal static class Holder
            {
                /// <summary>
                /// One paragraph.
                /// <para>Another paragraph of the same summary.</para>
                /// </summary>
                /// <remarks>Detail.</remarks>
                public static void First() { }

                /// <summary>The next member's own.</summary>
                public static void Second() { }

                // <summary>Not documentation.</summary>
                // <summary>Still not.</summary>
                public static void Third() { }

                public const string Text = "/// <summary>a</summary> /// <summary>b</summary>";
            }
            """);
}
