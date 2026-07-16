using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SESSIONPROPERTY('option')</c> — the six ANSI / arithmetic SET
/// toggles plus <c>QUOTED_IDENTIFIER</c>. Real SQL Server returns sql_variant
/// with an inner base type of <c>int</c>; the simulator surfaces the int
/// directly (the <c>SERVERPROPERTY</c> convention). Fresh-session defaults are
/// probe-confirmed against SQL Server 2025: every option is 1 except
/// <c>ARITHABORT</c> and <c>NUMERIC_ROUNDABORT</c>, which default 0. A
/// top-level <c>SET</c> tracks the state; an unknown option name returns NULL;
/// names are case-insensitive. DacFx's bacpac-export preamble reads
/// <c>ISNULL(SESSIONPROPERTY('ANSI_NULLS'), 0)</c> /
/// <c>ISNULL(SESSIONPROPERTY('QUOTED_IDENTIFIER'), 1)</c>.
/// </summary>
[TestClass]
public sealed class SessionPropertyTests
{
    [TestMethod]
    public void AnsiNulls_DefaultsOn()
        => AreEqual(1, new Simulation().ExecuteScalar("select sessionproperty('ANSI_NULLS')"));

    [TestMethod]
    public void AnsiPadding_DefaultsOn()
        => AreEqual(1, new Simulation().ExecuteScalar("select sessionproperty('ANSI_PADDING')"));

    [TestMethod]
    public void AnsiWarnings_DefaultsOn()
        => AreEqual(1, new Simulation().ExecuteScalar("select sessionproperty('ANSI_WARNINGS')"));

    [TestMethod]
    public void ConcatNullYieldsNull_DefaultsOn()
        => AreEqual(1, new Simulation().ExecuteScalar("select sessionproperty('CONCAT_NULL_YIELDS_NULL')"));

    [TestMethod]
    public void QuotedIdentifier_DefaultsOn()
        => AreEqual(1, new Simulation().ExecuteScalar("select sessionproperty('QUOTED_IDENTIFIER')"));

    [TestMethod]
    public void Arithabort_DefaultsOff()
        => AreEqual(0, new Simulation().ExecuteScalar("select sessionproperty('ARITHABORT')"));

    [TestMethod]
    public void NumericRoundabort_DefaultsOff()
        => AreEqual(0, new Simulation().ExecuteScalar("select sessionproperty('NUMERIC_ROUNDABORT')"));

    [TestMethod]
    public void NameIsCaseInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar("select sessionproperty('ansi_nulls')"));

    [TestMethod]
    public void UnknownProperty_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select sessionproperty('BOGUS')");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SetAnsiNullsOff_TracksToZero()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            set ansi_nulls off;
            select sessionproperty('ANSI_NULLS')
            """));

    [TestMethod]
    public void SetArithabortOn_TracksToOne()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            set arithabort on;
            select sessionproperty('ARITHABORT')
            """));

    [TestMethod]
    public void SetCommaListForm_TracksEveryListedOption()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            set ansi_nulls, ansi_warnings, concat_null_yields_null off;
            select sessionproperty('ANSI_NULLS')
                 + sessionproperty('ANSI_WARNINGS')
                 + sessionproperty('CONCAT_NULL_YIELDS_NULL')
            """));

    [TestMethod]
    public void SetPersistsAcrossBatchesOnSameConnection()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("set ansi_padding off").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("select sessionproperty('ANSI_PADDING')").ExecuteScalar());
    }

    [TestMethod]
    public void IntType_FlowsToProjectionSchema()
    {
        using var reader = new Simulation().ExecuteReader("select sessionproperty('ANSI_NULLS')");
        AreEqual(typeof(int), reader.GetFieldType(0));
    }

    [TestMethod]
    public void DacFxIsnullPreamble_ReturnsOneOne()
    {
        using var reader = new Simulation().ExecuteReader(
            "select isnull(sessionproperty('ANSI_NULLS'), 0), isnull(sessionproperty('QUOTED_IDENTIFIER'), 1)");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
    }
}
