using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET LANGUAGE</c>, the session state it carries (<c>@@LANGUAGE</c> /
/// <c>@@LANGID</c>) and the <c>@@DATEFIRST</c> it implicitly moves. Every
/// value below was probed against SQL Server 2025 (2026-08-08), including the
/// full <c>sys.syslanguages</c> table. See <c>docs/claude/scalars.md</c>.
/// </summary>
[TestClass]
public sealed class SetLanguageTests
{
    private static object? Scalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    /// <summary>A fresh session is us_english, langid 0, DATEFIRST 7.</summary>
    [TestMethod]
    public void DefaultsToUsEnglish()
    {
        AreEqual("us_english", Scalar("select @@language"));
        AreEqual((short)0, Scalar("select @@langid"));
        AreEqual((byte)7, Scalar("select @@datefirst"));
    }

    /// <summary>
    /// The statement takes an official name or an alias, bare or quoted, and
    /// <c>@@LANGUAGE</c> always reads back the official name — so
    /// <c>SET LANGUAGE German</c> answers <c>Deutsch</c>.
    /// </summary>
    [TestMethod]
    [DataRow("German", "Deutsch", (short)1)]
    [DataRow("Deutsch", "Deutsch", (short)1)]
    [DataRow("'German'", "Deutsch", (short)1)]
    [DataRow("[German]", "Deutsch", (short)1)]
    [DataRow("Japanese", "日本語", (short)3)]
    [DataRow("N'Français'", "Français", (short)2)]
    [DataRow("British", "British", (short)23)]
    [DataRow("[British English]", "British", (short)23)]
    [DataRow("Brazilian", "Português (Brasil)", (short)27)]
    [DataRow("us_english", "us_english", (short)0)]
    public void NameOrAlias_SetsTheSessionLanguage(string written, string expectedName, short expectedLangId)
    {
        AreEqual(expectedName, Scalar($"set language {written}; select @@language"));
        AreEqual(expectedLangId, Scalar($"set language {written}; select @@langid"));
    }

    /// <summary>The variable form is legal wherever the bare identifier is.</summary>
    [TestMethod]
    public void VariableForm_SetsTheSessionLanguage()
        => AreEqual("Deutsch", Scalar("declare @l nvarchar(50) = N'German'; set language @l; select @@language"));

    /// <summary>
    /// The point of the coupling: a language's own <c>datefirst</c> becomes the
    /// session's. German is 1, Japanese 7, Swedish 1, Thai 7.
    /// </summary>
    [TestMethod]
    [DataRow("German", (byte)1)]
    [DataRow("Japanese", (byte)7)]
    [DataRow("Swedish", (byte)1)]
    [DataRow("Thai", (byte)7)]
    [DataRow("Brazilian", (byte)7)]
    [DataRow("Arabic", (byte)1)]
    public void SetLanguage_MovesDateFirst(string language, byte expected)
        => AreEqual(expected, Scalar($"set language {language}; select @@datefirst"));

    /// <summary>
    /// An explicit <c>SET DATEFIRST</c> earlier in the <em>same batch</em> wins
    /// over the language's implicit one; the same pair split across two batches
    /// ends on the language's value, since real scopes that precedence to the
    /// batch rather than the session.
    /// </summary>
    [TestMethod]
    public void ExplicitDateFirst_WinsWithinTheBatchOnly()
    {
        AreEqual((byte)3, Scalar("set datefirst 3; set language German; select @@datefirst"));

        using var connection = new Simulation().CreateOpenConnection();
        using (var command = connection.CreateCommand("set datefirst 3"))
        {
            _ = command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand("set language German; select @@datefirst"))
        {
            AreEqual((byte)1, command.ExecuteScalar());
        }
    }

    /// <summary>
    /// A <c>SET DATEFIRST</c> <em>after</em> the language still wins — it is
    /// simply the later write.
    /// </summary>
    [TestMethod]
    public void DateFirstAfterLanguage_TakesEffect()
        => AreEqual((byte)3, Scalar("set language German; set datefirst 3; select @@datefirst"));

    /// <summary>The setting rides the session, not the batch.</summary>
    [TestMethod]
    public void LanguageSurvivesTheBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using (var command = connection.CreateCommand("set language German"))
        {
            _ = command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand("select @@language"))
        {
            AreEqual("Deutsch", command.ExecuteScalar());
        }
    }

    /// <summary>
    /// An unrecognized name is <strong>Msg 2740</strong>, and the batch carries
    /// on past it — a statement-terminating error, not a batch-aborting one.
    /// </summary>
    [TestMethod]
    public void UnknownLanguage_RaisesMsg2740()
    {
        var ex = new Simulation().AssertSqlError("set language nosuchlang", 2740);
        AreEqual("SET LANGUAGE failed because 'nosuchlang' is not an official language name or a language alias on this SQL Server.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    /// <summary>
    /// Inside a <c>TRY</c> block real swallows the failure outright: nothing is
    /// raised, no <c>CATCH</c> runs, the statement no-ops and the body carries
    /// on — probe-confirmed, dynamic SQL included.
    /// </summary>
    [TestMethod]
    public void UnknownLanguage_InsideATryBlock_IsASilentNoOp()
    {
        AreEqual("inside-after", Scalar("""
            begin try set language nosuchlang; select 'inside-after'; end try
            begin catch select 'caught'; end catch
            """));
        AreEqual("us_english", Scalar("""
            begin try set language nosuchlang; end try begin catch select 'caught'; end catch;
            select @@language
            """));
    }

    /// <summary>
    /// But an <c>IF</c> / <c>BEGIN … END</c> / <c>WHILE</c> body is not a TRY
    /// frame, so the refusal still fires there (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 1 set language nosuchlang")]
    [DataRow("begin set language nosuchlang end")]
    [DataRow("declare @i int = 0; while @i < 1 begin set language nosuchlang; set @i = 1; end")]
    public void UnknownLanguage_OutsideATryBlock_StillRaises(string commandText)
        => _ = new Simulation().AssertSqlError(commandText, 2740);

    /// <summary>
    /// <c>sys.syslanguages</c> projects the whole installed set, in langid
    /// order, with the columns real declares.
    /// </summary>
    [TestMethod]
    public void SysLanguages_ProjectsEveryInstalledLanguage()
    {
        AreEqual(34, Scalar("select count(*) from sys.syslanguages"));
        AreEqual("us_english", Scalar("select name from sys.syslanguages where langid = 0"));
        AreEqual("English", Scalar("select alias from sys.syslanguages where langid = 0"));
        AreEqual((byte)1, Scalar("select datefirst from sys.syslanguages where alias = 'German'"));
        AreEqual("dmy", Scalar("select dateformat from sys.syslanguages where alias = 'German'"));
        AreEqual(2057, Scalar("select lcid from sys.syslanguages where alias = 'British English'"));
        // British English's messages come from us_english, so the two ids split.
        AreEqual((short)1033, Scalar("select msglangid from sys.syslanguages where alias = 'British English'"));
        AreEqual(8, Scalar("select count(*) from sys.syslanguages where datefirst = 7"));
    }
}
