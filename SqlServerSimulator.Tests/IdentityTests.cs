using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>IDENTITY(seed, increment)</c> columns: auto-generation,
/// <c>SET IDENTITY_INSERT</c> bracketing, the <c>SCOPE_IDENTITY()</c> /
/// <c>@@IDENTITY</c> / <c>IDENT_CURRENT</c> trio (all <c>numeric(38, 0)</c>),
/// reseed semantics, and CREATE TABLE validation errors.
/// </summary>
[TestClass]
public sealed class IdentityTests
{
    [TestMethod]
    public void Insert_Omitted_GeneratesNextValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity(1,1), name varchar(20));
            insert t (name) values ('a'),('b'),('c')
            """);
        AreEqual(1, simulation.ExecuteScalar("select id from t where name = 'a'"));
        AreEqual(2, simulation.ExecuteScalar("select id from t where name = 'b'"));
        AreEqual(3, simulation.ExecuteScalar("select id from t where name = 'c'"));
    }

    [TestMethod]
    public void Insert_NoColumnList_ImplicitlyExcludesIdentity()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity(1,1), name varchar(20));
            insert t values ('a')
            """);
        AreEqual(1, simulation.ExecuteScalar("select id from t"));
        AreEqual("a", simulation.ExecuteScalar("select name from t"));
    }

    [TestMethod]
    public void ScopeIdentity_NoInsertYet_IsNull() => AreEqual(DBNull.Value, ExecuteScalar("select SCOPE_IDENTITY()"));

    [TestMethod]
    public void AtAtIdentity_NoInsertYet_IsNull() => AreEqual(DBNull.Value, ExecuteScalar("select @@IDENTITY"));

    // SCOPE_IDENTITY() is typed as numeric(38, 0) regardless of the column's underlying integer type.
    [TestMethod]
    public void ScopeIdentity_ReturnsNumeric38Scale0()
        => AreEqual(1m, new Simulation().ExecuteScalar("""
            create table t (id int identity(1,1), name varchar(20));
            insert t (name) values ('a');
            select SCOPE_IDENTITY()
            """));

    [TestMethod]
    public void ScopeIdentity_AfterMultiRowInsert_ReturnsLastValue()
    {
        // SCOPE_IDENTITY/@@IDENTITY are per-session (per-connection) so the
        // setup INSERT and the read have to share a connection.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int identity(1,1), x int);
            insert t (x) values (1),(2),(3)
            """).ExecuteNonQuery();
        AreEqual(3m, connection.CreateCommand("select SCOPE_IDENTITY()").ExecuteScalar());
        AreEqual(3m, connection.CreateCommand("select @@IDENTITY").ExecuteScalar());
    }

    // Verified: insert a table without identity clears SCOPE_IDENTITY/@@IDENTITY back to NULL.
    [TestMethod]
    public void Insert_NonIdentityTable_ResetsScopeIdentityToNull()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tid (id int identity(1,1), x int);
            create table tplain (k int);
            insert tid (x) values (1);
            insert tplain values (42)
            """);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select SCOPE_IDENTITY()"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select @@IDENTITY"));
    }

    [TestMethod]
    public void IdentCurrent_BeforeAnyInsert_ReturnsSeed()
        => AreEqual(7m, new Simulation().ExecuteScalar("""
            create table t (id int identity(7,2), x int);
            select IDENT_CURRENT('t')
            """));

    [TestMethod]
    public void IdentCurrent_AfterInserts_ReturnsHighWaterMark()
        => AreEqual(3m, new Simulation().ExecuteScalar("""
            create table t (id int identity(1,1), x int);
            insert t (x) values (1),(2),(3);
            select IDENT_CURRENT('t')
            """));

    [TestMethod]
    public void IdentCurrent_NonexistentTable_ReturnsNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select IDENT_CURRENT('does_not_exist')"));

    [TestMethod]
    public void IdentCurrent_NonIdentityTable_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("""
            create table t (k int, x int);
            select IDENT_CURRENT('t')
            """));

    [TestMethod]
    public void Identity_BareKeyword_DefaultsToOneOne()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity, x int);
            insert t (x) values (1),(2)
            """);
        AreEqual(1, simulation.ExecuteScalar("select id from t where x = 1"));
        AreEqual(2, simulation.ExecuteScalar("select id from t where x = 2"));
    }

    [TestMethod]
    public void Identity_NegativeIncrement_CountsDown()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity(100, -3), x int);
            insert t (x) values (1),(2),(3)
            """);
        AreEqual(100, simulation.ExecuteScalar("select id from t where x = 1"));
        AreEqual(97, simulation.ExecuteScalar("select id from t where x = 2"));
        AreEqual(94, simulation.ExecuteScalar("select id from t where x = 3"));
    }

    [TestMethod]
    public void Identity_NegativeSeedAndIncrement_CountsDown()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity(-5, -2), x int);
            insert t (x) values (1),(2),(3)
            """);
        AreEqual(-5, simulation.ExecuteScalar("select id from t where x = 1"));
        AreEqual(-7, simulation.ExecuteScalar("select id from t where x = 2"));
        AreEqual(-9, simulation.ExecuteScalar("select id from t where x = 3"));
    }

    [TestMethod]
    public void Insert_ExplicitIdentityWithoutSetOn_RaisesMsg544()
        => new Simulation().AssertSqlError("""
            create table t (id int identity(1,1), name varchar(20));
            insert t (id, name) values (5, 'x')
            """, 544,
            "Cannot insert explicit value for identity column in table 't' when IDENTITY_INSERT is set to OFF.");

    [TestMethod]
    public void Insert_OmittedIdentityWithSetOn_RaisesMsg545()
        => new Simulation().AssertSqlError("""
            create table t (id int identity(1,1), name varchar(20));
            set identity_insert t on;
            insert t (name) values ('x')
            """, 545,
            "Explicit value must be specified for identity column in table 't' either when IDENTITY_INSERT is set to ON or when a replication user is inserting into a NOT FOR REPLICATION identity column.");

    [TestMethod]
    public void IdentityInsert_AlreadyOn_RaisesMsg8107()
        => new Simulation().AssertSqlError("""
            create table a (id int identity(1,1), x int);
            create table b (id int identity(1,1), x int);
            set identity_insert a on;
            set identity_insert b on
            """, 8107,
            "IDENTITY_INSERT is already ON for table 'a'. Cannot perform SET operation for table 'b'.");

    [TestMethod]
    public void IdentityInsert_OnThenOffThenOn_AllowsSwitching()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int identity(1,1), x int);
            create table b (id int identity(1,1), x int);
            set identity_insert a on;
            insert a (id, x) values (10, 1);
            set identity_insert a off;
            set identity_insert b on;
            insert b (id, x) values (20, 1)
            """);
        AreEqual(10, simulation.ExecuteScalar("select id from a"));
        AreEqual(20, simulation.ExecuteScalar("select id from b"));
    }

    [TestMethod]
    public void Insert_ExplicitLargerValue_AdvancesSeed()
        => AreEqual(101, new Simulation().ExecuteScalar("""
            create table t (id int identity(1,1), name varchar(20));
            insert t (name) values ('a');
            set identity_insert t on;
            insert t (id, name) values (100, 'jump');
            set identity_insert t off;
            insert t (name) values ('next');
            select id from t where name = 'next'
            """));

    [TestMethod]
    public void Insert_ExplicitSmallerValue_DoesNotReseed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity(1,1), name varchar(20));
            insert t (name) values ('a'),('b'),('c');
            set identity_insert t on;
            insert t (id, name) values (1, 'low');
            set identity_insert t off;
            insert t (name) values ('next')
            """);
        AreEqual(4, simulation.ExecuteScalar("select id from t where name = 'next'"));
        AreEqual(4m, simulation.ExecuteScalar("select IDENT_CURRENT('t')"));
    }

    [TestMethod]
    public void CreateTable_MultipleIdentityColumns_RaisesMsg2744()
        => new Simulation().AssertSqlError("create table t (id1 int identity(1,1), id2 int identity(1,1))", 2744,
            "Multiple identity columns specified for table 't'. Only one identity column per table is allowed.");

    [TestMethod]
    public void CreateTable_IdentityNullableExplicit_RaisesMsg8147()
        => new Simulation().AssertSqlError("create table t (id int identity(1,1) null, x int)", 8147,
            "Could not create IDENTITY attribute on nullable column 'id', table 't'.");

    [TestMethod]
    public void CreateTable_IdentityOnVarchar_RaisesMsg2749()
        => new Simulation().AssertSqlError("create table t (id varchar(10) identity(1,1), x int)", 2749,
            "Identity column 'id' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, unencrypted, and constrained to be nonnullable.");

    [TestMethod]
    public void CreateTable_IdentityZeroIncrement_RaisesMsg2753()
        => new Simulation().AssertSqlError("create table t (id int identity(1,0), x int)", 2753,
            "Identity column 'id' contains invalid INCREMENT.");

    // IDENTITY-specific Msg 8115 wording uses "converting IDENTITY" (vs generic "expression to data type").
    [TestMethod]
    public void Insert_TinyIntIdentityOverflow_RaisesIdentityMsg8115()
        => new Simulation().AssertSqlError("""
            create table t (id tinyint identity(255,1), x int);
            insert t (x) values (1);
            insert t (x) values (2)
            """, 8115,
            "Arithmetic overflow error converting IDENTITY to data type tinyint.");

    [TestMethod]
    public void Identity_OnTinyIntSmallIntBigInt_AllRoundTrip()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tt (id tinyint identity(1,1), x int);
            create table ts (id smallint identity(1,1), x int);
            create table tb (id bigint identity(1,1), x int);
            insert tt (x) values (1);
            insert ts (x) values (1);
            insert tb (x) values (1)
            """);
        AreEqual((byte)1, simulation.ExecuteScalar("select id from tt"));
        AreEqual((short)1, simulation.ExecuteScalar("select id from ts"));
        AreEqual(1L, simulation.ExecuteScalar("select id from tb"));
    }

    // IDENT_CURRENT is per Simulation in the simulator (per-table-globally in real SQL Server).
    [TestMethod]
    public void IdentCurrent_AcrossSimulationInstances_IsPerSimulation()
    {
        var s1 = new Simulation();
        _ = s1.ExecuteNonQuery("""
            create table t (id int identity(1,1), x int);
            insert t (x) values (1),(2)
            """);
        AreEqual(2m, s1.ExecuteScalar("select IDENT_CURRENT('t')"));

        var s2 = new Simulation();
        _ = s2.ExecuteNonQuery("create table t (id int identity(1,1), x int)");
        AreEqual(1m, s2.ExecuteScalar("select IDENT_CURRENT('t')"));
    }

    [TestMethod]
    public void NotForReplication_SetsIdentityColumnFlag()
    {
        // NOT FOR REPLICATION has no runtime effect (replication isn't modeled);
        // it round-trips through sys.identity_columns + COLUMNPROPERTY for BACPAC
        // parity. DacFx reads is_not_for_replication to emit
        // IdentityIsNotForReplication=True.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity(1,1) not for replication, x int)");
        IsTrue((bool)sim.ExecuteScalar(
            "select is_not_for_replication from sys.identity_columns where object_id = object_id('t')")!);
        AreEqual(1, sim.ExecuteScalar("select COLUMNPROPERTY(object_id('t'), 'id', 'IsIdNotForRepl')"));
        // Auto-generation still works normally.
        _ = sim.ExecuteNonQuery("insert t (x) values (10),(20)");
        AreEqual(2, sim.ExecuteScalar("select id from t where x = 20"));
    }

    [TestMethod]
    public void IdentityWithoutNotForReplication_FlagIsZero()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity(1,1), x int)");
        IsFalse((bool)sim.ExecuteScalar(
            "select is_not_for_replication from sys.identity_columns where object_id = object_id('t')")!);
        AreEqual(0, sim.ExecuteScalar("select COLUMNPROPERTY(object_id('t'), 'id', 'IsIdNotForRepl')"));
    }
}
