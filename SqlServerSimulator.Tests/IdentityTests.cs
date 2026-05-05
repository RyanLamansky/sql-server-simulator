using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>IDENTITY(seed, increment)</c> columns: auto-
/// generation on omitted INSERT, <c>SET IDENTITY_INSERT</c> bracketing for
/// explicit values, the <c>SCOPE_IDENTITY()</c> / <c>@@IDENTITY</c> /
/// <c>IDENT_CURRENT</c> trio (all <c>numeric(38, 0)</c>), reseed semantics on
/// explicit advance, and the validation errors at <c>CREATE TABLE</c> time.
/// </summary>
[TestClass]
public sealed class IdentityTests
{
    [TestMethod]
    public void Insert_Omitted_GeneratesNextValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t (name) values ('a'),('b'),('c')");
        AreEqual(1, simulation.ExecuteScalar("select id from t where name = 'a'"));
        AreEqual(2, simulation.ExecuteScalar("select id from t where name = 'b'"));
        AreEqual(3, simulation.ExecuteScalar("select id from t where name = 'c'"));
    }

    [TestMethod]
    public void Insert_NoColumnList_ImplicitlyExcludesIdentity()
    {
        // SQL Server's "VALUES supplies non-identity columns" shorthand:
        // INSERT INTO t VALUES ('a') for (id identity, name) targets only name.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t values ('a')");
        AreEqual(1, simulation.ExecuteScalar("select id from t"));
        AreEqual("a", simulation.ExecuteScalar("select name from t"));
    }

    [TestMethod]
    public void ScopeIdentity_NoInsertYet_IsNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select SCOPE_IDENTITY()"));
    }

    [TestMethod]
    public void AtAtIdentity_NoInsertYet_IsNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select @@IDENTITY"));
    }

    [TestMethod]
    public void ScopeIdentity_ReturnsNumeric38Scale0()
    {
        // SQL Server emits SCOPE_IDENTITY() typed as numeric(38, 0)
        // regardless of the column's underlying integer type.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t (name) values ('a')");
        AreEqual(1m, simulation.ExecuteScalar("select SCOPE_IDENTITY()"));
    }

    [TestMethod]
    public void ScopeIdentity_AfterMultiRowInsert_ReturnsLastValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (1),(2),(3)");
        AreEqual(3m, simulation.ExecuteScalar("select SCOPE_IDENTITY()"));
        AreEqual(3m, simulation.ExecuteScalar("select @@IDENTITY"));
    }

    [TestMethod]
    public void Insert_NonIdentityTable_ResetsScopeIdentityToNull()
    {
        // Verified against SQL Server 2025: an INSERT into a table without
        // an identity column clears SCOPE_IDENTITY()/@@IDENTITY back to NULL,
        // even after a previous identity insert in the same batch.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table tid (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("create table tplain (k int)");
        _ = simulation.ExecuteNonQuery("insert into tid (x) values (1)");
        _ = simulation.ExecuteNonQuery("insert into tplain values (42)");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select SCOPE_IDENTITY()"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select @@IDENTITY"));
    }

    [TestMethod]
    public void IdentCurrent_BeforeAnyInsert_ReturnsSeed()
    {
        // SQL Server's documented fallback: IDENT_CURRENT returns the seed
        // when no row has yet been generated.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(7,2), x int)");
        AreEqual(7m, simulation.ExecuteScalar("select IDENT_CURRENT('t')"));
    }

    [TestMethod]
    public void IdentCurrent_AfterInserts_ReturnsHighWaterMark()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (1),(2),(3)");
        AreEqual(3m, simulation.ExecuteScalar("select IDENT_CURRENT('t')"));
    }

    [TestMethod]
    public void IdentCurrent_NonexistentTable_ReturnsNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select IDENT_CURRENT('does_not_exist')"));
    }

    [TestMethod]
    public void IdentCurrent_NonIdentityTable_ReturnsNull()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (k int, x int)");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select IDENT_CURRENT('t')"));
    }

    [TestMethod]
    public void Identity_BareKeyword_DefaultsToOneOne()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity, x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (1),(2)");
        AreEqual(1, simulation.ExecuteScalar("select id from t where x = 1"));
        AreEqual(2, simulation.ExecuteScalar("select id from t where x = 2"));
    }

    [TestMethod]
    public void Identity_NegativeIncrement_CountsDown()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(100, -3), x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (1),(2),(3)");
        AreEqual(100, simulation.ExecuteScalar("select id from t where x = 1"));
        AreEqual(97, simulation.ExecuteScalar("select id from t where x = 2"));
        AreEqual(94, simulation.ExecuteScalar("select id from t where x = 3"));
    }

    [TestMethod]
    public void Identity_NegativeSeedAndIncrement_CountsDown()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(-5, -2), x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (1),(2),(3)");
        AreEqual(-5, simulation.ExecuteScalar("select id from t where x = 1"));
        AreEqual(-7, simulation.ExecuteScalar("select id from t where x = 2"));
        AreEqual(-9, simulation.ExecuteScalar("select id from t where x = 3"));
    }

    [TestMethod]
    public void Insert_ExplicitIdentityWithoutSetOn_RaisesMsg544()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t (id, name) values (5, 'x')"));
        AreEqual("Cannot insert explicit value for identity column in table 't' when IDENTITY_INSERT is set to OFF.", ex.Message);
    }

    [TestMethod]
    public void Insert_OmittedIdentityWithSetOn_RaisesMsg545()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        _ = simulation.ExecuteNonQuery("set identity_insert t on");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t (name) values ('x')"));
        AreEqual("Explicit value must be specified for identity column in table 't' either when IDENTITY_INSERT is set to ON or when a replication user is inserting into a NOT FOR REPLICATION identity column.", ex.Message);
    }

    [TestMethod]
    public void IdentityInsert_AlreadyOn_RaisesMsg8107()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("create table b (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("set identity_insert a on");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("set identity_insert b on"));
        AreEqual("IDENTITY_INSERT is already ON for table 'a'. Cannot perform SET operation for table 'b'.", ex.Message);
    }

    [TestMethod]
    public void IdentityInsert_OnThenOffThenOn_AllowsSwitching()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("create table b (id int identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("set identity_insert a on");
        _ = simulation.ExecuteNonQuery("insert into a (id, x) values (10, 1)");
        _ = simulation.ExecuteNonQuery("set identity_insert a off");
        _ = simulation.ExecuteNonQuery("set identity_insert b on");
        _ = simulation.ExecuteNonQuery("insert into b (id, x) values (20, 1)");
        AreEqual(10, simulation.ExecuteScalar("select id from a"));
        AreEqual(20, simulation.ExecuteScalar("select id from b"));
    }

    [TestMethod]
    public void Insert_ExplicitLargerValue_AdvancesSeed()
    {
        // Verified against SQL Server 2025: explicit insert of a value past
        // the high-water mark advances the seed; subsequent auto-generations
        // continue from the explicit value plus increment.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t (name) values ('a')");
        _ = simulation.ExecuteNonQuery("set identity_insert t on");
        _ = simulation.ExecuteNonQuery("insert into t (id, name) values (100, 'jump')");
        _ = simulation.ExecuteNonQuery("set identity_insert t off");
        _ = simulation.ExecuteNonQuery("insert into t (name) values ('next')");
        AreEqual(101, simulation.ExecuteScalar("select id from t where name = 'next'"));
    }

    [TestMethod]
    public void Insert_ExplicitSmallerValue_DoesNotReseed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), name varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t (name) values ('a'),('b'),('c')");
        _ = simulation.ExecuteNonQuery("set identity_insert t on");
        _ = simulation.ExecuteNonQuery("insert into t (id, name) values (1, 'low')");
        _ = simulation.ExecuteNonQuery("set identity_insert t off");
        _ = simulation.ExecuteNonQuery("insert into t (name) values ('next')");
        AreEqual(4, simulation.ExecuteScalar("select id from t where name = 'next'"));
        AreEqual(4m, simulation.ExecuteScalar("select IDENT_CURRENT('t')"));
    }

    [TestMethod]
    public void CreateTable_MultipleIdentityColumns_RaisesMsg2744()
    {
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (id1 int identity(1,1), id2 int identity(1,1))"));
        AreEqual("Multiple identity columns specified for table 't'. Only one identity column per table is allowed.", ex.Message);
    }

    [TestMethod]
    public void CreateTable_IdentityNullableExplicit_RaisesMsg8147()
    {
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (id int identity(1,1) null, x int)"));
        AreEqual("Could not create IDENTITY attribute on nullable column 'id', table 't'.", ex.Message);
    }

    [TestMethod]
    public void CreateTable_IdentityOnVarchar_RaisesMsg2749()
    {
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (id varchar(10) identity(1,1), x int)"));
        AreEqual("Identity column 'id' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, unencrypted, and constrained to be nonnullable.", ex.Message);
    }

    [TestMethod]
    public void CreateTable_IdentityZeroIncrement_RaisesMsg2753()
    {
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (id int identity(1,0), x int)"));
        AreEqual("Identity column 'id' contains invalid INCREMENT.", ex.Message);
    }

    [TestMethod]
    public void Insert_TinyIntIdentityOverflow_RaisesIdentityMsg8115()
    {
        // Verified against SQL Server 2025: the IDENTITY-specific Msg 8115
        // wording uses "converting IDENTITY to data type tinyint" (vs the
        // generic "expression to data type" form).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id tinyint identity(255,1), x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (1)");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t (x) values (2)"));
        AreEqual("Arithmetic overflow error converting IDENTITY to data type tinyint.", ex.Message);
    }

    [TestMethod]
    public void Identity_OnTinyIntSmallIntBigInt_AllRoundTrip()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table tt (id tinyint identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("create table ts (id smallint identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("create table tb (id bigint identity(1,1), x int)");
        _ = simulation.ExecuteNonQuery("insert into tt (x) values (1)");
        _ = simulation.ExecuteNonQuery("insert into ts (x) values (1)");
        _ = simulation.ExecuteNonQuery("insert into tb (x) values (1)");
        AreEqual((byte)1, simulation.ExecuteScalar("select id from tt"));
        AreEqual((short)1, simulation.ExecuteScalar("select id from ts"));
        AreEqual(1L, simulation.ExecuteScalar("select id from tb"));
    }

    [TestMethod]
    public void IdentCurrent_AcrossSimulationInstances_IsPerSimulation()
    {
        // IDENT_CURRENT is per table on real SQL Server (visible across
        // sessions), but the simulator's "session" is the whole simulation —
        // separate Simulation instances are independent.
        var s1 = new Simulation();
        _ = s1.ExecuteNonQuery("create table t (id int identity(1,1), x int)");
        _ = s1.ExecuteNonQuery("insert into t (x) values (1),(2)");
        AreEqual(2m, s1.ExecuteScalar("select IDENT_CURRENT('t')"));

        var s2 = new Simulation();
        _ = s2.ExecuteNonQuery("create table t (id int identity(1,1), x int)");
        AreEqual(1m, s2.ExecuteScalar("select IDENT_CURRENT('t')"));
    }
}
