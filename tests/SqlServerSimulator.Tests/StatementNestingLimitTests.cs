using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Statement nesting limits, a budget real keeps apart from an expression's:
/// an <c>IF</c>, <c>WHILE</c> or <c>TRY</c> costs one unit, a
/// <c>BEGIN … END</c> two, an <c>ELSE</c> three and the <c>SELECT</c> they end
/// in six, so each shape here passes at real's own limit and raises Msg 191
/// one level past it (probed 2026-10-24 against SQL Server 2025). The depths
/// real accepts need more stack than a default thread has, so they run on a
/// 64 MB one; a smaller thread meets the stack probe's Msg 8631 first, and
/// never a stack overflow.
/// </summary>
[TestClass]
public sealed class StatementNestingLimitTests
{
    private const int DeepStack = 64 * 1024 * 1024;

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static string Shape(string shape, int depth) => shape switch
    {
        "if" => Repeat("if 1 = 1 ", depth) + "select 1",
        "if over set" => "declare @i int; " + Repeat("if 1 = 1 ", depth) + "set @i = 1; select @i",
        "begin" => Repeat("begin ", depth) + "select 1; " + Repeat("end ", depth),
        "begin over set" => "declare @i int; " + Repeat("begin ", depth) + "set @i = 1; " + Repeat("end ", depth) + "select @i",
        "try" => Repeat("begin try ", depth) + "select 1; " + Repeat("end try begin catch end catch ", depth),
        "else if" => "if 1 = 0 select 0 " + Repeat("else if 1 = 0 select 0 ", depth - 1) + "else select 1",
        "begin if" => Repeat("begin if 1 = 1 ", depth) + "select 1; " + Repeat("end ", depth),
        _ => "declare @i int = 0; " + Repeat("while @i < 1 ", depth) + "set @i = 1; select @i",
    };

    [TestMethod]
    [Timeout(60000)]
    [DataRow("if", 1008, null)]
    [DataRow("if", 1009, 191)]
    [DataRow("if over set", 1014, null)]
    [DataRow("if over set", 1015, 191)]
    [DataRow("begin", 504, null)]
    [DataRow("begin", 505, 191)]
    [DataRow("begin over set", 507, null)]
    [DataRow("begin over set", 508, 191)]
    [DataRow("try", 1008, null)]
    [DataRow("try", 1009, 191)]
    [DataRow("else if", 252, null)]
    [DataRow("else if", 253, 191)]
    [DataRow("begin if", 336, null)]
    [DataRow("begin if", 337, 191)]
    [DataRow("while over set", 1014, null)]
    [DataRow("while over set", 1015, 191)]
    public void NestedStatements_MeetRealsLimit(string shape, int depth, int? error)
    {
        var (result, failure) = Extensions.ExecuteScalarOnStack(Shape(shape, depth), DeepStack);
        AreEqual(error, failure.ErrorNumber());
        if (error is null)
            AreEqual(1, result);
    }

    [TestMethod]
    [Timeout(60000)]
    [DataRow("begin")]
    [DataRow("try")]
    [DataRow("if")]
    public void NestedStatements_OnSmallStackThread_RaiseMsg8631NotProcessDeath(string shape)
        => AreEqual(8631, Extensions.ExecuteScalarOnStack(Shape(shape, 1000), 512 * 1024).Failure.ErrorNumber());
}
