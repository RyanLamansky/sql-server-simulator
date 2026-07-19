using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the system scalar functions
/// <c>sys.fn_varbintohexsubstring</c> and <c>sys.fn_varbintohexstr</c>, which
/// format a <c>varbinary</c> as a lowercase hex string (SMO scripts login SIDs
/// and binary defaults through them). Outputs are probe-confirmed against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class VarbinaryToHexScalarTests
{
    [TestMethod]
    public void Substring_FullLengthOne_PrefixesWith0x()
        => AreEqual("0x0123abcdef", new Simulation().ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 1, 0)"));

    [TestMethod]
    public void Substring_FullLengthZero_OmitsPrefix()
        => AreEqual("0123abcdef", new Simulation().ExecuteScalar("select sys.fn_varbintohexsubstring(0, 0x0123ABCDEF, 1, 0)"));

    [TestMethod]
    public void Substring_StartAndLength_SlicesBytes()
        => AreEqual("0x23abcd", new Simulation().ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 2, 3)"));

    [TestMethod]
    public void Substring_LengthPastEnd_ClampsToRemainder()
        => AreEqual("0x23abcdef", new Simulation().ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 2, 99)"));

    [TestMethod]
    public void Substring_NonPositiveLength_MeansToEnd()
    {
        var sim = new Simulation();
        AreEqual("0xabcdef", sim.ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 3, 0)"));
        AreEqual("0xabcdef", sim.ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 3, -1)"));
    }

    [TestMethod]
    public void Substring_StartOutOfRange_ReturnsNull()
    {
        var sim = new Simulation();
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 0, 2)"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x0123ABCDEF, 10, 2)"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select sys.fn_varbintohexsubstring(1, 0x, 1, 0)"));
    }

    [TestMethod]
    public void Substring_NullValue_ReturnsNull()
        => _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select sys.fn_varbintohexsubstring(1, NULL, 1, 0)"));

    [TestMethod]
    public void Str_IsFullLengthPrefixedToEnd()
    {
        var sim = new Simulation();
        AreEqual("0x0123abcdef", sim.ExecuteScalar("select sys.fn_varbintohexstr(0x0123ABCDEF)"));
        AreEqual("0x0123abcdef", sim.ExecuteScalar("select master.dbo.fn_varbintohexstr(0x0123ABCDEF)"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select sys.fn_varbintohexstr(NULL)"));
    }

    /// <summary>Unqualified is not a recognized built-in (real Msg 195).</summary>
    [TestMethod]
    public void Unqualified_RaisesMsg195()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("select fn_varbintohexstr(0x01)"));
        AreEqual(195, ex.Number);
    }

    /// <summary>Current-database dbo qualification does not resolve (real Msg 4121).</summary>
    [TestMethod]
    public void CurrentDbDboQualified_RaisesMsg4121()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("select dbo.fn_varbintohexstr(0x01)"));
        AreEqual(4121, ex.Number);
    }
}
