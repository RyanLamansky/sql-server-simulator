using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class PropertyPatternChainAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<PropertyPatternChainAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    private const string Preamble = """
        internal enum Tag { A, B, C }
        internal abstract class Token { }
        internal sealed class Keyword : Token { public Tag Kind; }
        internal sealed class Op : Token { public char Ch; }
        """;

    [TestMethod]
    public Task TwoArmIfElseIf_SameScrutineeTypeProperty_Reports() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                        return 1;
                    else if (t is Keyword { Kind: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task ThreeArmIfElseIf_Reports() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                        return 1;
                    else if (t is Keyword { Kind: Tag.B })
                        return 2;
                    else if (t is Keyword { Kind: Tag.C })
                        return 3;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task IfElseIfWithFinalElse_StillReports() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                        return 1;
                    else if (t is Keyword { Kind: Tag.B })
                        return 2;
                    else
                        return 0;
                }
            }
            """);

    [TestMethod]
    public Task TwoConsecutiveIfReturn_Reports() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                        return 1;
                    if (t is Keyword { Kind: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task IfThrow_Reports() =>
        RunAsync("""
            using System;
            """ + Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                        throw new InvalidOperationException();
                    if (t is Keyword { Kind: Tag.B })
                        throw new InvalidOperationException();
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task SingleArm_DoesNotReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    if (t is Keyword { Kind: Tag.A })
                        return 1;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task DifferentTypes_DoesNotReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    if (t is Keyword { Kind: Tag.A })
                        return 1;
                    else if (t is Op { Ch: ',' })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task DifferentProperty_DoesNotReport() =>
        RunAsync(Preamble + """

            internal sealed class Multi : Token { public Tag Kind; public Tag Other; }
            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    if (t is Multi { Kind: Tag.A })
                        return 1;
                    else if (t is Multi { Other: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task DifferentScrutinee_DoesNotReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token a, Token b)
                {
                    if (a is Keyword { Kind: Tag.A })
                        return 1;
                    else if (b is Keyword { Kind: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task ScrutineeWithMethodCall_DoesNotReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static Token Next() => null!;
                public static int Resolve()
                {
                    // The 'Next()' call is side-effecting; collapsing to a switch
                    // would call it once, not twice. Conservative: don't flag.
                    if (Next() is Keyword { Kind: Tag.A })
                        return 1;
                    else if (Next() is Keyword { Kind: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task ScrutineeWithIndexer_DoesNotReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token[] arr)
                {
                    if (arr[0] is Keyword { Kind: Tag.A })
                        return 1;
                    else if (arr[0] is Keyword { Kind: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task DesignationOnPattern_DoesNotReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    // 'k' designations would force the rewrite to rename across
                    // arms; the analyzer skips this shape.
                    if (t is Keyword { Kind: Tag.A } k1)
                        return k1.GetHashCode();
                    else if (t is Keyword { Kind: Tag.B } k2)
                        return k2.GetHashCode();
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task MultipleSubpatterns_DoesNotReport() =>
        RunAsync(Preamble + """

            internal sealed class Multi : Token { public Tag Kind; public char Ch; }
            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    if (t is Multi { Kind: Tag.A, Ch: ',' })
                        return 1;
                    else if (t is Multi { Kind: Tag.B, Ch: ';' })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task IfReturnFollowedByNonExitingIf_DoesNotChain() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    int x = 0;
                    if (t is Keyword { Kind: Tag.A })
                        return 1;
                    if (t is Keyword { Kind: Tag.B })
                        x = 2; // doesn't exit; chain breaks here
                    return x;
                }
            }
            """);

    [TestMethod]
    public Task DottedScrutinee_StillReports() =>
        RunAsync(Preamble + """

            internal sealed class Wrapper { public Token Token = null!; }
            internal static class Holder
            {
                public static int Resolve(Wrapper w)
                {
                    {|SSS004:if|} (w.Token is Keyword { Kind: Tag.A })
                        return 1;
                    else if (w.Token is Keyword { Kind: Tag.B })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task OrPatternInPropertyMatch_StillReports() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A or Tag.B })
                        return 1;
                    else if (t is Keyword { Kind: Tag.C })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task MultiValueOrPatternInBothArms_StillReports() =>
        RunAsync(Preamble + """

            internal enum Wide { L, R, C, T, Co, Cs, X, Y }
            internal sealed class Wk : Token { public Wide K; }
            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    // The shape from CLAUDE.md's modeled set-op example:
                    // multiple values funnel into one block via an or-pattern.
                    // Detection only constrains "one subpattern named K";
                    // the or-chain after the colon is opaque to the rule.
                    {|SSS004:if|} (t is Wk { K: Wide.L or Wide.R or Wide.C or Wide.T })
                        return 1;
                    else if (t is Wk { K: Wide.Co or Wide.Cs })
                        return 2;
                    else if (t is Wk { K: Wide.X or Wide.Y })
                        return 3;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task RelationalPatternInPropertyMatch_StillReports() =>
        RunAsync(Preamble + """

            internal sealed class IntTok : Token { public int N; }
            internal static class Holder
            {
                public static int Resolve(Token t)
                {
                    {|SSS004:if|} (t is IntTok { N: > 100 })
                        return 1;
                    else if (t is IntTok { N: < 0 })
                        return 2;
                    return 0;
                }
            }
            """);

    [TestMethod]
    public Task ChainInsideSwitchSection_Reports() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(int code, Token t)
                {
                    switch (code)
                    {
                        case 1:
                            {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                                return 1;
                            if (t is Keyword { Kind: Tag.B })
                                return 2;
                            return 0;
                        default:
                            return -1;
                    }
                }
            }
            """);

    [TestMethod]
    public Task NestedIfChainHeads_BothReport() =>
        RunAsync(Preamble + """

            internal static class Holder
            {
                public static int Resolve(Token t, Token u)
                {
                    {|SSS004:if|} (t is Keyword { Kind: Tag.A })
                    {
                        {|SSS004:if|} (u is Keyword { Kind: Tag.A })
                            return 1;
                        else if (u is Keyword { Kind: Tag.B })
                            return 2;
                        return 3;
                    }
                    else if (t is Keyword { Kind: Tag.B })
                    {
                        return 4;
                    }
                    return 0;
                }
            }
            """);
}
