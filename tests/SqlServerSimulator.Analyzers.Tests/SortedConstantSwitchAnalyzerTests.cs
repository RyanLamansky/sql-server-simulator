using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class SortedConstantSwitchAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<SortedConstantSwitchAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task SortedStringExpression_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s) => s switch { "a" => 1, "b" => 2, "c" => 3, _ => 0 };
            }
            """);

    [TestMethod]
    public Task UnsortedStringExpression_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s) => s switch { "a" => 1, "c" => 2, {|SSS005:"b"|} => 3, _ => 0 };
            }
            """);

    [TestMethod]
    public Task SortedNumericExpression_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(int x) => x switch { 1 => 1, 2 => 2, 10 => 3, _ => 0 };
            }
            """);

    [TestMethod]
    public Task UnsortedNumericExpression_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(int x) => x switch { 1 => 1, 3 => 2, {|SSS005:2|} => 3, _ => 0 };
            }
            """);

    [TestMethod]
    public Task EnumSwitch_NotCovered_DoesNotReport() =>
        RunAsync("""
            internal enum E { Cos, Sin, Tan }
            internal sealed class C
            {
                public int M(E e) => e switch { E.Tan => 1, E.Cos => 2, E.Sin => 3, _ => 0 };
            }
            """);

    [TestMethod]
    public Task CharSwitch_NotCovered_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(char c) => c switch { '*' => 1, '+' => 2, '%' => 3, _ => 0 };
            }
            """);

    [TestMethod]
    public Task BoolSwitch_NotCovered_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(bool b) => b switch { true => 1, false => 2 };
            }
            """);

    [TestMethod]
    public Task GuardClause_ExemptsSwitch() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s) => s switch { "c" when s.Length > 0 => 1, "a" => 2, _ => 0 };
            }
            """);

    [TestMethod]
    public Task OrPattern_ExemptsSwitch() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s) => s switch { "c" or "d" => 1, "a" => 2, _ => 0 };
            }
            """);

    [TestMethod]
    public Task RelationalPattern_ExemptsSwitch() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(int x) => x switch { > 5 => 1, 1 => 2, _ => 0 };
            }
            """);

    [TestMethod]
    public Task TypePattern_ExemptsSwitch() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(object o) => o switch { string => 1, int => 2, _ => 0 };
            }
            """);

    [TestMethod]
    public Task MixedConstantKinds_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(object o) => o switch { "z" => 1, 5 => 2, _ => 0 };
            }
            """);

    [TestMethod]
    public Task NullAndDiscardSentinels_ExcludedFromOrdering() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string? s) => s switch { null => -1, "a" => 1, "b" => 2, _ => 0 };
            }
            """);

    [TestMethod]
    public Task SingleConstantArm_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s) => s switch { "a" => 1, _ => 0 };
            }
            """);

    [TestMethod]
    public Task NestedSwitches_ReportsOnlyTheUnsortedInner() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(int len, string s) => len switch
                {
                    2 => s switch { "ab" => 1, "cd" => 2, _ => 0 },
                    3 => s switch { "abc" => 1, "ghi" => 2, {|SSS005:"def"|} => 3, _ => 0 },
                    _ => 0,
                };
            }
            """);

    [TestMethod]
    public Task SortedSwitchStatement_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s)
                {
                    switch (s)
                    {
                        case "a": return 1;
                        case "b": return 2;
                        case "c": return 3;
                        default: return 0;
                    }
                }
            }
            """);

    [TestMethod]
    public Task UnsortedSwitchStatement_Reports() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s)
                {
                    switch (s)
                    {
                        case "a": return 1;
                        case "c": return 2;
                        {|SSS005:case "b":|} return 3;
                        default: return 0;
                    }
                }
            }
            """);

    [TestMethod]
    public Task MultiLabelSectionFlattened_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s)
                {
                    switch (s)
                    {
                        case "a":
                        case "b":
                            return 1;
                        case "c":
                            return 2;
                        default:
                            return 0;
                    }
                }
            }
            """);

    [TestMethod]
    public Task DefaultLabelNotLast_ExcludedFromOrdering() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s)
                {
                    switch (s)
                    {
                        default: return 0;
                        case "a": return 1;
                        case "b": return 2;
                    }
                }
            }
            """);

    [TestMethod]
    public Task TwoAdjacentInversions_ReportEach() =>
        RunAsync("""
            internal sealed class C
            {
                public int M(string s) => s switch { "d" => 1, {|SSS005:"a"|} => 2, "e" => 3, {|SSS005:"b"|} => 4, _ => 0 };
            }
            """);
}
