using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET TEXTSIZE</c> semantics: a session-scoped byte cap applied to
/// MAX-typed and legacy-LOB values as they leave the server for the client —
/// result columns and output parameters, never server-side computation,
/// variable assignment, or stored data. Value mapping (-1 preserved, 0 and
/// other negatives → 4096), Msg 1080 on an out-of-int-range literal,
/// per-type truncation units (bytes; wide chars floored), xml / bounded /
/// non-LOB exemption, and revert-at-proc-exit all probe-confirmed against
/// SQL Server 2025 (2026-07-19).
/// </summary>
[TestClass]
public sealed class TextSizeTests
{
    // ---- @@TEXTSIZE / SET value mapping ----

    [TestMethod]
    public void Default_IsMinusOne()
        => AreEqual(-1, new Simulation().ExecuteScalar("select @@TEXTSIZE"));

    [TestMethod]
    public void SetZero_ReadsBackDefault4096()
        => AreEqual(4096, new Simulation().ExecuteScalar("set textsize 0; select @@TEXTSIZE"));

    [TestMethod]
    public void SetOtherNegative_ReadsBackDefault4096()
        => AreEqual(4096, new Simulation().ExecuteScalar("set textsize -5; select @@TEXTSIZE"));

    [TestMethod]
    public void SetMinusOne_PreservedVerbatim()
        => AreEqual(-1, new Simulation().ExecuteScalar("set textsize 10; set textsize -1; select @@TEXTSIZE"));

    [TestMethod]
    public void SetPositive_ReadsBack()
        => AreEqual(10, new Simulation().ExecuteScalar("set textsize 10; select @@TEXTSIZE"));

    [TestMethod]
    public void SetMaxInt_Accepted()
        => AreEqual(2147483647, new Simulation().ExecuteScalar("set textsize 2147483647; select @@TEXTSIZE"));

    [TestMethod]
    public void SetPastIntRange_RaisesMsg1080()
        => new Simulation().AssertSqlError(
            "set textsize 2147483648",
            1080,
            "The integer value 2147483648 is out of range.");

    [TestMethod]
    public void SetInSkippedBranch_DoesNotApply()
        => AreEqual(-1, new Simulation().ExecuteScalar("if 1 = 0 set textsize 10; select @@TEXTSIZE"));

    // ---- Truncation per type ----

    [TestMethod]
    public void VarcharMax_TruncatesToByteCap()
        => AreEqual(new string('x', 10), new Simulation().ExecuteScalar(
            "set textsize 10; select replicate(cast('x' as varchar(max)), 100)"));

    [TestMethod]
    public void NVarcharMax_TruncatesToHalfCap()
        => AreEqual(new string('x', 5), new Simulation().ExecuteScalar(
            "set textsize 10; select replicate(cast(N'x' as nvarchar(max)), 100)"));

    [TestMethod]
    public void NVarcharMax_OddCap_Floors()
        => AreEqual(new string('x', 5), new Simulation().ExecuteScalar(
            "set textsize 11; select replicate(cast(N'x' as nvarchar(max)), 100)"));

    [TestMethod]
    public void VarbinaryMax_TruncatesToByteCap()
    {
        var bytes = (byte[])new Simulation().ExecuteScalar(
            "set textsize 10; select cast(replicate(cast('x' as varchar(max)), 100) as varbinary(max))")!;
        HasCount(10, bytes);
    }

    [TestMethod]
    public void LegacyLobs_Truncate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a text, b ntext, c image);
            insert t values (replicate(cast('x' as varchar(max)), 100),
                             replicate(cast(N'x' as nvarchar(max)), 100),
                             cast(replicate(cast('x' as varchar(max)), 100) as varbinary(max)))
            """);
        using var reader = sim.ExecuteReader("set textsize 10; select a, b, c from t");
        IsTrue(reader.Read());
        AreEqual(new string('x', 10), reader.GetValue(0));
        AreEqual(new string('x', 5), reader.GetValue(1));
        HasCount(10, (byte[])reader.GetValue(2));
    }

    [TestMethod]
    public void Xml_Unaffected()
        => AreEqual("<r><a>hello</a><b>world</b></r>", new Simulation().ExecuteScalar(
            "set textsize 10; select cast('<r><a>hello</a><b>world</b></r>' as xml)"));

    [TestMethod]
    public void BoundedVarTypes_Unaffected()
    {
        using var reader = new Simulation().ExecuteReader("""
            set textsize 10;
            select cast(replicate('x', 100) as varchar(200)),
                   cast(replicate(N'x', 100) as nvarchar(200)),
                   cast(replicate(cast('x' as varchar(100)), 100) as varbinary(200))
            """);
        IsTrue(reader.Read());
        AreEqual(new string('x', 100), reader.GetValue(0));
        AreEqual(new string('x', 100), reader.GetValue(1));
        HasCount(100, (byte[])reader.GetValue(2));
    }

    [TestMethod]
    public void CapOfOne_NVarcharEmpty_VarcharSingleChar()
    {
        using var reader = new Simulation().ExecuteReader("""
            set textsize 1;
            select replicate(cast(N'x' as nvarchar(max)), 100),
                   replicate(cast('x' as varchar(max)), 100)
            """);
        IsTrue(reader.Read());
        AreEqual(string.Empty, reader.GetValue(0));
        AreEqual("x", reader.GetValue(1));
    }

    // ---- Server-side state untouched ----

    [TestMethod]
    public void ServerSideComputations_Unaffected()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v varchar(max)); insert t values (replicate(cast('x' as varchar(max)), 100))");
        using var reader = sim.ExecuteReader("set textsize 10; select datalength(v), len(v), v from t");
        IsTrue(reader.Read());
        AreEqual(100L, reader.GetInt64(0));
        AreEqual(100, reader.GetInt32(1));
        AreEqual(new string('x', 10), reader.GetValue(2));
    }

    [TestMethod]
    public void VariableAssignment_KeepsFullValue_ReturnTruncates()
    {
        using var reader = new Simulation().ExecuteReader("""
            set textsize 10;
            declare @v varchar(max) = replicate(cast('x' as varchar(max)), 100);
            select len(@v), @v
            """);
        IsTrue(reader.Read());
        AreEqual(100, reader.GetInt32(0));
        AreEqual(new string('x', 10), reader.GetValue(1));
    }

    [TestMethod]
    public void InsertSelect_WritesFullData()
        => AreEqual(100L, new Simulation().ExecuteScalar("""
            create table src (v varchar(max));
            insert src values (replicate(cast('x' as varchar(max)), 100));
            set textsize 10;
            select v into dst from src;
            select datalength(v) from dst
            """));

    // ---- Scope / lifetime ----

    [TestMethod]
    public void PerSession_OtherConnectionUnaffected()
    {
        var sim = new Simulation();
        using var a = sim.CreateOpenConnection();
        using var b = sim.CreateOpenConnection();
        _ = a.CreateCommand("set textsize 10").ExecuteNonQuery();
        AreEqual(10, a.CreateCommand("select @@TEXTSIZE").ExecuteScalar());
        AreEqual(-1, b.CreateCommand("select @@TEXTSIZE").ExecuteScalar());
    }

    [TestMethod]
    public void PersistsAcrossBatches()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set textsize 10").ExecuteNonQuery();
        AreEqual(10, connection.CreateCommand("select @@TEXTSIZE").ExecuteScalar());
    }

    [TestMethod]
    public void SetInsideProc_RevertsAtProcExit()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create proc p as set textsize 10");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("exec p").ExecuteNonQuery();
        AreEqual(-1, connection.CreateCommand("select @@TEXTSIZE").ExecuteScalar());
    }

    // A proc body's SET TEXTSIZE governs the result sets that body produces
    // even though the client drains them after the proc-exit revert — the cap
    // is stamped at statement production, not read at drain.
    [TestMethod]
    public void ProcBodySet_GovernsProcResultSets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create proc p as set textsize 10 select replicate(cast('x' as varchar(max)), 100)");
        AreEqual(new string('x', 10), sim.ExecuteScalar("exec p"));
    }

    // ---- Output parameters ----

    // DbType.String declares nvarchar(max), so the 10-byte cap yields 5 chars.
    [TestMethod]
    public void OutputParameter_NVarcharMax_TruncatesToHalfCap()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set textsize 10").ExecuteNonQuery();
        using var command = connection.CreateCommand("set @o = replicate(cast(N'x' as nvarchar(max)), 100)");
        var output = command.CreateParameter();
        output.ParameterName = "@o";
        output.DbType = DbType.String;
        output.Size = -1;
        output.Direction = ParameterDirection.Output;
        _ = command.Parameters.Add(output);
        _ = command.ExecuteNonQuery();

        AreEqual(new string('x', 5), output.Value);
    }

    [TestMethod]
    public void OutputParameter_VarcharMax_TruncatesToByteCap()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set textsize 10").ExecuteNonQuery();
        using var command = connection.CreateCommand("set @o = replicate(cast('x' as varchar(max)), 100)");
        var output = command.CreateParameter();
        output.ParameterName = "@o";
        output.DbType = DbType.AnsiString;
        output.Size = -1;
        output.Direction = ParameterDirection.Output;
        _ = command.Parameters.Add(output);
        _ = command.ExecuteNonQuery();

        AreEqual(new string('x', 10), output.Value);
    }
}
