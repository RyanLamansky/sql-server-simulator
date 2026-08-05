using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A projecting SELECT hands the reader the values it computed rather than a
/// page image of them, so the one narrowing the page image used to perform —
/// the ANSI code page's <c>?</c> replacement for a character it can't carry —
/// has to be applied on the way out instead. These pin it at the client
/// surface, for a value the projection computes rather than one a column
/// stores. Every expectation is what SQL Server 2025 produced for the same
/// statement.
/// </summary>
/// <remarks>
/// Counterpart to <see cref="CollationCodePageTests"/>, which covers the same
/// code pages for data that reaches storage.
/// </remarks>
[TestClass]
public sealed class ProjectedValueNarrowingTests
{
    /// <summary>
    /// A character CP1252 can't represent is <c>?</c> the moment the value is
    /// read as <c>varchar</c> / <c>char</c> / <c>text</c>, whether or not it
    /// ever reaches a page. <c>char(4)</c> keeps its four bytes, so the
    /// replacement is padded like any other short value.
    /// </summary>
    [TestMethod]
    [DataRow("cast(N'水' as varchar(10))", "?")]
    [DataRow("cast(N'水' as char(4))", "?   ")]
    [DataRow("convert(varchar(10), N'水')", "?")]
    [DataRow("cast(N'水水' as varchar(10))", "??")]
    [DataRow("cast(N'a水b' as varchar(10))", "a?b")]
    public void ProjectedAnsiString_FoldsUnrepresentableCharacter(string expression, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>The deprecated <c>text</c> type stores CP1252 bytes and folds
    /// the same way; <c>DATALENGTH</c> counts the single replacement byte.</summary>
    [TestMethod]
    public void ProjectedText_FoldsUnrepresentableCharacter()
    {
        var sim = new Simulation();
        AreEqual("?", sim.ExecuteScalar("select cast(cast(N'水' as text) as varchar(10))"));
        AreEqual(1, sim.ExecuteScalar<int>("select datalength(cast(N'水' as text))"));
    }

    /// <summary>A <c>sql_variant</c> carries its inner value's own type, so an
    /// ANSI string inside one narrows exactly as it would outside.</summary>
    [TestMethod]
    public void ProjectedVariant_FoldsUnrepresentableCharacterInsideIt()
    {
        var sim = new Simulation();
        AreEqual("?", sim.ExecuteScalar("select cast(cast(N'水' as varchar(10)) as sql_variant)"));
        AreEqual(
            "varchar",
            sim.ExecuteScalar("select sql_variant_property(cast(cast(N'水' as varchar(10)) as sql_variant), 'BaseType')"));
    }

    /// <summary>
    /// A collation whose own code page can carry the character keeps it — the
    /// fold is the code page's, not a blanket ASCII narrowing.
    /// </summary>
    [TestMethod]
    public void ProjectedAnsiString_KeepsWhatTheCollationsCodePageCarries()
    {
        var sim = new Simulation();
        AreEqual("日本", sim.ExecuteScalar("select cast(N'日本' collate Japanese_CI_AS as varchar(10))"));
        AreEqual(4, sim.ExecuteScalar<int>("select datalength(cast(N'日本' collate Japanese_CI_AS as varchar(10)))"));
        AreEqual("café", sim.ExecuteScalar("select cast(N'café' as varchar(10))"));
    }

    /// <summary>
    /// The rest of a projected value's storage shape survives the same way: a
    /// <c>char(N)</c> is padded to its declared width, a <c>decimal</c> carries
    /// its declared scale, and the bytes a conversion renders are the ones the
    /// value holds.
    /// </summary>
    [TestMethod]
    public void ProjectedValue_KeepsItsDeclaredStorageShape()
    {
        var sim = new Simulation();
        AreEqual("abc       ", sim.ExecuteScalar("select cast('abc' as char(10))"));
        AreEqual(10, sim.ExecuteScalar<int>("select datalength(cast('abc' as char(10)))"));
        AreEqual(1.00m, sim.ExecuteScalar<decimal>("select cast(1 as numeric(10, 2))"));
        AreEqual(
            "3F",
            Convert.ToHexString((byte[])sim.ExecuteScalar("select convert(varbinary(20), cast(N'水' as varchar(10)))")!));
        AreEqual(
            "3F202020",
            Convert.ToHexString((byte[])sim.ExecuteScalar("select convert(varbinary(20), cast(N'水' as char(4)))")!));
    }

    /// <summary>
    /// The narrowing follows the value through the shapes that reach the client
    /// by a different route than a plain projection: a stored column, an ORDER
    /// BY's buffered path, a set operation, and a replayed cached plan.
    /// </summary>
    [TestMethod]
    public void ProjectedAnsiString_FoldsThroughEveryReaderPath()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int, v varchar(10))",
            "insert t values (1, N'水'), (2, 'b')");

        AreEqual("?", sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual("?", sim.ExecuteScalar("select cast(N'水' as varchar(10)) from t where id = 1 order by id"));
        AreEqual("?", sim.ExecuteScalar("select cast(N'水' as varchar(10)) union select 'z'"));
        AreEqual("?", sim.ExecuteScalar("select top (1) cast(N'水' as varchar(10)) from t order by id"));

        // The plan cache replays the same text through its own dispatch arm.
        for (var i = 0; i < 3; i++)
            AreEqual("?", sim.ExecuteScalar("select cast(N'水' as varchar(10)) as v from t where id = 1"));
    }
}
