using SqlServerSimulator.Parser.Tokens;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
/// <remarks>
/// <see cref="Token.LineNumber"/> backs the <c>"Line N"</c> prefix in
/// SQL-Server-mimicking error messages (e.g. Msg 1002 invalid scale). Pin
/// the byte-counting math directly so the line attributed to an error is
/// not silently miscounted by an off-by-one against newline conventions.
/// </remarks>
[TestClass]
public sealed class TokenLineNumberTests
{
    [TestMethod]
    public void SingleLineCommand_AllTokensReportLine1()
    {
        foreach (var token in TokenizeMeaningful("select 1 + 2"))
            AreEqual(1, token.LineNumber, $"token '{token}' expected line 1");
    }

    [TestMethod]
    public void TokenAfterLfNewline_ReportsLine2()
    {
        var tokens = TokenizeMeaningful("select 1\n+ 2").ToArray();
        AreEqual(1, tokens[0].LineNumber);                  // 'select'
        var plus = tokens.First(t => t.ToString() == "+");
        AreEqual(2, plus.LineNumber);
    }

    [TestMethod]
    public void TokenAfterCrlfNewline_AlsoReportsLine2()
    {
        // Only \n increments the line. \r before it is folded into the same
        // line so CRLF and LF both count once. (SQL Server matches this.)
        var tokens = TokenizeMeaningful("select 1\r\n+ 2").ToArray();
        var plus = tokens.First(t => t.ToString() == "+");
        AreEqual(2, plus.LineNumber);
    }

    [TestMethod]
    public void LeadingBlankLines_PushTokenLineDown()
    {
        var tokens = TokenizeMeaningful("\n\n\nselect 1").ToArray();
        AreEqual(4, tokens[0].LineNumber);
    }

    [TestMethod]
    public void MultipleLineBreaks_AccumulateInOrder()
    {
        var tokens = TokenizeMeaningful("a\nb\nc\nd").ToArray();
        AreEqual(1, tokens[0].LineNumber);
        AreEqual(2, tokens[1].LineNumber);
        AreEqual(3, tokens[2].LineNumber);
        AreEqual(4, tokens[3].LineNumber);
    }

    [TestMethod]
    public void NewlineWithinACommentToken_PushesLaterTokensDown()
    {
        // Block comments are skipped at the parser level, but the lines they
        // contain still count toward what comes after them.
        var tokens = TokenizeMeaningful("/* a\nb */ select 1").ToArray();
        var select = tokens.First(t => t.ToString().Equals("select", StringComparison.OrdinalIgnoreCase));
        AreEqual(2, select.LineNumber);
    }

    /// <summary>
    /// Mirrors <see cref="ParserContext.MoveNext"/>: skips whitespace and
    /// comment tokens so the assertions match what error-reporting code sees.
    /// </summary>
    private static IEnumerable<Token> TokenizeMeaningful(string command)
    {
        var index = 0;
        while (Tokenizer.NextToken(command, ref index, Collation.Default) is Token t)
        {
            if (t is Whitespace or Comment)
                continue;
            yield return t;
        }
    }
}
