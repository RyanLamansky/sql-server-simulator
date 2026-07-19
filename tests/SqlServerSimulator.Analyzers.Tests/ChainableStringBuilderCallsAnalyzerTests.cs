using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class ChainableStringBuilderCallsAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<ChainableStringBuilderCallsAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    private const string Preamble = "using System.Text;\n";

    [TestMethod]
    public Task TwoDiscardedAppends_Reports() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    {|SSS006:_ = sb.Append("a");|}
                    _ = sb.Append("b");
                }
            }
            """);

    [TestMethod]
    public Task ThreeAppends_ExampleShape_Reports() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb, byte[] bytes)
                {
                    {|SSS006:_ = sb.Append('"');|}
                    _ = sb.Append(System.Convert.ToBase64String(bytes));
                    _ = sb.Append('"');
                }
            }
            """);

    [TestMethod]
    public Task TwoBareAppends_Reports() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    {|SSS006:sb.Append("a");|}
                    sb.Append("b");
                }
            }
            """);

    [TestMethod]
    public Task AppendLineAndInsert_SelfReturningMethods_Report() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    {|SSS006:_ = sb.AppendLine("a");|}
                    _ = sb.Insert(0, "b");
                }
            }
            """);

    [TestMethod]
    public Task FieldReceiver_Reports() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                private readonly StringBuilder sb = new();
                void M()
                {
                    {|SSS006:_ = this.sb.Append("a");|}
                    _ = this.sb.Append("b");
                }
            }
            """);

    [TestMethod]
    public Task CommentBetween_StillReports() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    {|SSS006:_ = sb.Append("a");|}
                    // a documented step
                    _ = sb.Append("b");
                }
            }
            """);

    [TestMethod]
    public Task SingleAppend_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb) => sb.Append("a");
            }
            """);

    [TestMethod]
    public Task DifferentBuildersInterleaved_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb, StringBuilder other)
                {
                    _ = sb.Append("a");
                    _ = other.Append("b");
                    _ = sb.Append("c");
                }
            }
            """);

    [TestMethod]
    public Task NonBuilderStatementBetween_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    _ = sb.Append("a");
                    System.Console.WriteLine();
                    _ = sb.Append("b");
                }
            }
            """);

    [TestMethod]
    public Task MethodCallReceiver_NotSimple_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                StringBuilder Get() => new();
                void M()
                {
                    _ = Get().Append("a");
                    _ = Get().Append("b");
                }
            }
            """);

    [TestMethod]
    public Task CapturedReturnThenAppend_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    var captured = sb.Append("a");
                    _ = sb.Append("b");
                }
            }
            """);

    [TestMethod]
    public Task BareAppendAfterExistingChain_Reports() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    {|SSS006:_ = sb.Append("a").Append("b");|}
                    _ = sb.Append("c");
                }
            }
            """);

    [TestMethod]
    public Task LoneExistingChain_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                void M(StringBuilder sb)
                {
                    _ = sb.Append("a").Append("b");
                }
            }
            """);

    [TestMethod]
    public Task ChainsOnCallValuedRoot_DoesNotReport() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                StringBuilder Get() => new();
                void M()
                {
                    _ = Get().Append("a").Append("b");
                    _ = Get().Append("c");
                }
            }
            """);

    [TestMethod]
    public Task RunFollowedByToString_ReportsOnlyTheAppendRun() =>
        RunAsync(Preamble + """
            internal sealed class C
            {
                string M(StringBuilder sb)
                {
                    {|SSS006:_ = sb.Append("a");|}
                    _ = sb.Append("b");
                    return sb.ToString();
                }
            }
            """);
}
