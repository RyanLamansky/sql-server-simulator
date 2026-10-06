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
            "IDENTITY_INSERT is already ON for table 'simulated.dbo.a'. Cannot perform SET operation for table 'b'.");

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

    /// <summary>
    /// Real follows an identity overflow with the class-0 Msg 3606 where
    /// another error ending a write takes Msg 3621 (probed 2026-09-25 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void IdentityOverflow_IsFollowedByMsg3606()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id tinyint identity(255,1), x int);
            insert t (x) values (1);
            insert t (x) values (2)
            """, 8115);
        CollectionAssert.AreEqual(new[] { 8115, 3606 }, ex.Errors.Cast<SimulatedError>().Select(error => error.Number).ToArray());
        AreEqual("Arithmetic overflow occurred.", ex.Errors[1].Message);
    }

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

    [TestMethod]
    public void SetIdentityInsertOn_TableWithoutIdentity_RaisesMsg8106()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, x int);
            set identity_insert t on
            """, 8106);
        Assert.Contains("does not have the identity property", ex.Message);
    }

    [TestMethod]
    public void SetIdentityInsertOn_TableWithIdentity_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int identity(1,1) primary key, x int);
            set identity_insert t on;
            insert t (id, x) values (5, 10);
            set identity_insert t off;
            select count(*) from t
            """));

    // ---- seed / increment validation and decimal identity (probed 2026-09-24) ----

    [TestMethod]
    [DataRow("int identity(1.5, 1)", 2752, 2)]
    [DataRow("int identity(1.0, 1)", 2752, 2)]
    [DataRow("int identity(3000000000, 1)", 2752, 1)]
    [DataRow("tinyint identity(-1, 1)", 2752, 1)]
    [DataRow("int identity(1, 1.5)", 2753, 3)]
    [DataRow("int identity(1, 3000000000)", 2753, 1)]
    [DataRow("int identity(1, 0)", 2753, 2)]
    public void CreateTable_InvalidSeedOrIncrement(string column, int number, int state)
        => AreEqual(state, new Simulation().AssertSqlError($"create table t (i {column})", number).State);

    [TestMethod]
    [DataRow("int identity(1e0, 1)")]
    [DataRow("int identity(0x01, 1)")]
    public void CreateTable_NonIntegerLiteralSeed_RaisesMsg102(string column)
        => new Simulation().AssertSqlError($"create table t (i {column})", 102);

    [TestMethod]
    public void CreateTable_SignedSeed_Accepted()
        => AreEqual(-1, new Simulation().ExecuteScalar("create table t (i int identity(- 1, 1), v int); insert t (v) values (1); select i from t"));

    [TestMethod]
    public void CreateTable_DecimalScaleZeroIdentity_Generates()
        => AreEqual(99999m, new Simulation().ExecuteScalar("""
            create table t (i decimal(5, 0) identity(99998, 1), v int);
            insert t (v) values (1), (2);
            select max(i) from t
            """));

    [TestMethod]
    public void CreateTable_DecimalWithScale_RaisesMsg2749()
        => new Simulation().AssertSqlError("create table t (i numeric(10, 2) identity(1, 1))", 2749);

    [TestMethod]
    public void Insert_DecimalIdentityOverflow_RaisesMsg8115_AndKeepsIdentCurrent()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i decimal(5, 0) identity(99999, 1), v int); insert t (v) values (1)");
        sim.AssertSqlError("insert t (v) values (2)", 8115, "Arithmetic overflow error converting IDENTITY to data type decimal.");
        AreEqual(99999m, sim.ExecuteScalar("select ident_current('t')"));
    }

    // ---- DBCC CHECKIDENT (probed 2026-09-24 against SQL Server 2025) ----

    private static List<string> CheckIdentMessages(SimulatedDbConnection connection, string sql)
    {
        var messages = new List<string>();
        void Collect(object? sender, SimulatedInfoMessageEventArgs e) => messages.Add(e.Message);
        connection.InfoMessage += Collect;
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        connection.InfoMessage -= Collect;
        return messages;
    }

    [TestMethod]
    public void CheckIdent_ReportsTheCurrentAndColumnValues()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table cit (id int identity(1, 1), v int); insert cit (v) values (1), (2), (3)").ExecuteNonQuery();
        CollectionAssert.AreEqual(
            new[]
            {
                "Checking identity information: current identity value '3', current column value '3'.",
                "DBCC execution completed. If DBCC printed error messages, contact your system administrator.",
            },
            CheckIdentMessages(connection, "dbcc checkident ('cit', noreseed)"));
        CollectionAssert.AreEqual(
            new[]
            {
                "Checking identity information: current identity value '3'.",
                "DBCC execution completed. If DBCC printed error messages, contact your system administrator.",
            },
            CheckIdentMessages(connection, "dbcc checkident (cit, reseed, 10)"));
        IsEmpty(CheckIdentMessages(connection, "dbcc checkident ('dbo.cit', reseed, 20) with no_infomsgs"));
    }

    [TestMethod]
    [DataRow("insert cit (v) values (1); dbcc checkident ('cit', reseed, 10);", 11)]
    [DataRow("dbcc checkident ('cit', reseed, 10);", 10)]
    [DataRow("insert cit (v) values (1); truncate table cit; dbcc checkident ('cit', reseed, 0);", 0)]
    [DataRow("insert cit (v) values (1); delete cit; dbcc checkident ('cit', reseed, 0);", 1)]
    [DataRow("set identity_insert cit on; insert cit (id, v) values (50, 1); set identity_insert cit off; dbcc checkident ('cit', reseed, 5); dbcc checkident ('cit');", 51)]
    public void CheckIdent_Reseed_SetsTheNextValue(string setup, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"create table cit (id int identity(1, 1), v int); {setup} insert cit (v) values (9); select id from cit where v = 9"));

    [TestMethod]
    public void CheckIdent_ReseedRollsBackWithItsTransaction()
        => AreEqual(7m, new Simulation().ExecuteScalar("""
            create table cit (id int identity(1, 1));
            dbcc checkident ('cit', reseed, 7) with no_infomsgs;
            begin tran; dbcc checkident ('cit', reseed, 50) with no_infomsgs; rollback;
            select ident_current('cit')
            """));

    [TestMethod]
    [DataRow("dbcc checkident ('nosuch')", 2501, "Cannot find a table or object with the name \"nosuch\". Check the system catalog.")]
    [DataRow("create table cin (a int); dbcc checkident ('cin')", 7997, "'cin' does not contain an identity column.")]
    [DataRow("create table cid (id tinyint identity(1, 1)); dbcc checkident ('cid', reseed, 300) with no_infomsgs", 2560, "Parameter 3 is incorrect for this DBCC statement.")]
    public void CheckIdent_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);

    [TestMethod]
    public void IdentFunctions_ReadTheirArgumentPerRow()
        => AreEqual("t:5/2/3", new Simulation().ExecuteScalar("""
            create table t (id int identity(2, 3), v int); insert t values (1), (2);
            create table u (v int);
            select string_agg(concat(table_name, ':', ident_current(table_name), '/', ident_seed(table_name), '/', ident_incr(table_name)), ',')
            from information_schema.tables where ident_current(table_name) is not null
            """));

    [TestMethod]
    [DataRow("select ident_current(null)")]
    [DataRow("select ident_seed(cast(null as varchar(5)))")]
    [DataRow("select ident_incr(null)")]
    public void IdentFunctions_OfNull_AreNull(string sql)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(sql));

    // ---- identities past bigint (probed 2026-09-28 against SQL Server 2025) ----

    [TestMethod]
    public void DecimalIdentity_SeedPastBigInt_Generates()
        => AreEqual("100000000000000000001|100000000000000000001|100000000000000000001|100000000000000000000|1", new Simulation().ExecuteScalar("""
            create table t (id decimal(38, 0) identity(100000000000000000000, 1), x int);
            insert t (x) values (1), (2);
            select concat(max(id), '|', ident_current('t'), '|', scope_identity(), '|', ident_seed('t'), '|', ident_incr('t')) from t
            """));

    [TestMethod]
    public void DecimalIdentity_IncrementPastBigInt_Generates()
        => AreEqual(20000000000000000001m, new Simulation().ExecuteScalar("""
            create table t (id numeric(30, 0) identity(1, 10000000000000000000), x int);
            insert t (x) values (1), (2), (3);
            select max(id) from t
            """));

    [TestMethod]
    public void DecimalIdentity_ReseedAndExplicitPastBigInt_Advance()
        => AreEqual(90000000000000000001m, new Simulation().ExecuteScalar("""
            create table t (id decimal(25, 0) identity(1, 1), x int);
            dbcc checkident('t', reseed, 50000000000000000000) with no_infomsgs;
            insert t (x) values (1);
            set identity_insert t on;
            insert t (id, x) values (90000000000000000000, 2);
            set identity_insert t off;
            insert t (x) values (3);
            select max(id) from t
            """));

    [TestMethod]
    public void DecimalIdentity_NegativePastBigInt_Descends()
        => AreEqual(-50000000000000000001m, new Simulation().ExecuteScalar("""
            create table t (id decimal(25, 0) identity(-50000000000000000000, -1), x int);
            insert t (x) values (1), (2);
            select min(id) from t
            """));

    /// <summary>
    /// An identity overflow ends the batch and rolls back its transaction,
    /// and its Msg 3606 names no statement: line 1, outside any procedure
    /// (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void IdentityOverflow_EndsBatchAndRollsBack()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id tinyint identity(255, 1), x int); insert t (x) values (1)").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin tran;
            insert t (x) values (2);
            select 'after'
            """).ExecuteNonQuery());
        AreEqual(8115, ex.Number);
        AreEqual(1, ex.Errors[1].LineNumber);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    /// <summary>
    /// A constant NULL as an explicit identity value is Msg 339 while
    /// compiling, where a NULL only a run produces is Msg 515 (probed
    /// 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert t (id, b) values (null, 1)", 339)]
    [DataRow("insert t (id, b) values (cast(null as int), 1)", 339)]
    [DataRow("insert t (id, b) values (1, 1), (null, 2)", 339)]
    [DataRow("insert t (id, b) select null, 1", 339)]
    [DataRow("merge t using (values (1)) s(x) on 1 = 0 when not matched then insert (id, b) values (null, 1);", 339)]
    [DataRow("declare @n int; insert t (id, b) values (@n, 1)", 515)]
    public void IdentityInsert_NullIdentityValue(string statement, int number)
        => _ = new Simulation().AssertSqlError($"create table t (id int identity, b int); set identity_insert t on; {statement}", number);

    /// <summary>
    /// A row whose value fails to convert has drawn its identity value
    /// already, so the next row's skips it (probed 2026-10-01 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert t (b) values (1); insert t (b) values (1000)", 3)]
    [DataRow("insert t (b) values (1), (2), (1000)", 4)]
    [DataRow("merge t using (values (1000)) s(b) on 1 = 0 when not matched then insert (b) values (s.b);", 2)]
    public void Identity_FailedConversion_ConsumesTheValue(string failing, int next)
    {
        var simulation = new Simulation();
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery($"create table t (id int identity, b tinyint); {failing}"));
        AreEqual(next, simulation.ExecuteScalar<int>("insert t (b) values (9); select max(id) from t"));
    }

    [TestMethod]
    public void IdentityInsert_View_RaisesMsg8105()
        => new Simulation().AssertSqlError("create table t (id int identity, a int); exec('create view v as select id, a from t'); set identity_insert v on", 8105, "'v' is not a user table. Cannot perform SET operation.");

    [TestMethod]
    public void IdentityInsert_MissingObject_RaisesMsg1088()
        => new Simulation().AssertSqlError("set identity_insert nope on", 1088, "Cannot find the object \"nope\" because it does not exist or you do not have permissions.");

    /// <summary>SCOPE_IDENTITY and IDENT_CURRENT are numeric(38, 0) by name (probed 2026-10-01).</summary>
    [TestMethod]
    public void ScopeIdentity_ReportsNumericBaseType()
        => AreEqual("numeric|numeric", (string?)new Simulation().ExecuteScalar("""
            create table t (id bigint identity, b int); insert t (b) values (1);
            select concat(cast(sql_variant_property(scope_identity(), 'BaseType') as varchar(20)), '|', cast(sql_variant_property(ident_current('t'), 'BaseType') as varchar(20)))
            """));

    [TestMethod]
    public void IdentityOverflow_NamesANumericColumnNumeric()
        => new Simulation().AssertSqlError("create table t (id numeric(2, 0) identity(98, 1), b int); insert t (b) values (1), (2), (3)", 8115,
            "Arithmetic overflow error converting IDENTITY to data type numeric.");

    /// <summary>A DEFAULT on an identity column is Msg 1754 then Msg 1750 (probed 2026-10-01).</summary>
    [TestMethod]
    [DataRow("create table t (id int identity default 1, b int)")]
    [DataRow("create table t (id int identity, b int); alter table t add constraint d default 1 for id")]
    public void Identity_WithDefault_RaisesMsg1754(string sql)
    {
        var ex = new Simulation().AssertSqlError(sql, 1754);
        AreEqual(("Defaults cannot be created on columns with an IDENTITY attribute. Table 't', column 'id'.", 1750), (ex.Errors[0].Message, ex.Errors[1].Number));
    }
    /// <summary>
    /// A failing <c>INSERT</c> uses up one identity value per row its source
    /// produced before the failure, plus one for the failing row when the
    /// error came from computing the row's own values — which real does past
    /// the row's identity draw — and none when it came from an operator real
    /// runs ahead of the draws: a filter, a sort, <c>DISTINCT</c>, an
    /// aggregate, a multi-row <c>VALUES</c> or a set operation over constants
    /// (probed 2026-10-04 against SQL Server 2025). The table holds one row,
    /// so the value after the failure reads how many were used up.
    /// </summary>
    [TestMethod]
    [DataRow("insert t (v) values (cast('x' as int))", 2)]
    [DataRow("insert t (v) values (1/0)", 2)]
    [DataRow("insert t (v, s) values (1, 1/0)", 2)]
    [DataRow("insert t (v) values ((select 1/0))", 2)]
    [DataRow("declare @z int = 0; insert t (v) values (1/@z)", 2)]
    [DataRow("insert t (v) output inserted.id values (1/0)", 2)]
    [DataRow("insert t (v) values ('x')", 2)]
    [DataRow("insert t (v) values (1/0), (2)", 1)]
    [DataRow("insert t (v) values (1), (1/0)", 1)]
    [DataRow("insert t (v) values (1), ('x')", 1)]
    [DataRow("insert t (s) values (1), (300)", 3)]
    [DataRow("insert t (v) select cast('x' as int)", 2)]
    [DataRow("insert t (v) select 1/0", 2)]
    [DataRow("insert t (v) select 1/0 where 1 = 1", 2)]
    [DataRow("insert t (v, s) select 1, 1/0", 2)]
    [DataRow("insert t (v) select 'x'", 2)]
    [DataRow("insert t (s) select 300", 2)]
    [DataRow("insert t (v) select 10 / (k - 1) from src", 2)]
    [DataRow("insert t (v) select 10 / (k - 3) from src", 4)]
    [DataRow("insert t (v) select 10 / (k - 5) from src", 6)]
    [DataRow("insert t (v) select 10 / (k - 3) from src order by k", 4)]
    [DataRow("insert t (v) select 10 / (k - 1) from src order by k desc", 6)]
    [DataRow("insert t (v) select cast(case when k = 3 then 'x' else '1' end as int) from src", 4)]
    [DataRow("insert t (v) select case when k = 3 then 1/0 else k end from src", 4)]
    [DataRow("insert t (v) select (select 10 / (k - 3)) from src", 4)]
    [DataRow("insert t (v) select x from (select 10 / (k - 3) x from src) d", 4)]
    [DataRow("with c as (select 10 / (k - 3) x from src) insert t (v) select x from c", 4)]
    [DataRow("insert t (v) select a.x from src cross apply (select 10 / (k - 3) x) a", 4)]
    [DataRow("insert t (v) select 10 / (s.k - 3) from src s join src s2 on s.k = s2.k", 4)]
    [DataRow("insert t (v) output inserted.id select 10 / (k - 3) from src", 4)]
    [DataRow("insert t (v) select k from src where 10 / (3 - k) > 0", 3)]
    [DataRow("insert t (v) select k from src where 10 / (k - 3) > 0", 1)]
    [DataRow("insert t (v) select distinct 10 / (k - 3) from src", 1)]
    [DataRow("insert t (v) select 10 / (k - 3) from src order by 10 / (k - 3)", 1)]
    [DataRow("insert t (v) select sum(10 / (k - 3)) from src", 1)]
    [DataRow("insert t (v) select v from (values (1), (2), (1/0)) d(v)", 1)]
    [DataRow("insert t (v) select * from (values (1/0)) d(v)", 2)]
    [DataRow("insert t (v) select 1 union all select 1/0", 1)]
    [DataRow("insert t (v) select 1/0 union all select 1", 1)]
    [DataRow("insert t (v) select k from src union all select 1/0", 7)]
    [DataRow("insert t (v) select 1/0 union all select k from src", 2)]
    [DataRow("insert t (v) select 10 / (k - 3) from src union all select 1", 4)]
    [DataRow("insert t (s) select k * 100 from src", 4)]
    [DataRow("insert t (c) values (-1)", 2)]
    [DataRow("insert t (c) values (1), (-1)", 3)]
    [DataRow("insert d default values", 2)]
    [DataRow("insert d (w) values (1)", 2)]
    [DataRow("insert i (v) values (1/0)", 1)]
    [DataRow("insert a (v) values (1/0)", 2)]
    [DataRow("insert a (v) select 10 / (k - 3) from src", 4)]
    public void FailingInsert_UsesUpIdentityValues(string insert, int expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table src (k int); insert src values (1), (2), (3), (4), (5);
            create table t (id int identity, v int null, s tinyint null, c int null check (c >= 0));
            create table d (id int identity, v int default (1 / 0), w int null);
            create table i (id int identity, v int null);
            create table a (id int identity, v int null);
            """,
            "create trigger tri on i instead of insert as set nocount on",
            "create trigger tra on a after insert as set nocount on",
            "insert t default values; insert d (v) values (5); insert i default values; insert a default values");
        var table = insert.Split(' ')[1] is "t" or "d" or "i" or "a" ? insert.Split(' ')[1] : "t";
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(insert));
        AreEqual(expected, Convert.ToInt32(simulation.ExecuteScalar($"select ident_current('{table}')"), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A select list over a window function streams past the window, so a
    /// failing row uses up the values of the rows ahead of it and its own
    /// (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("row_number() over (order by k) * 10 / (k - 3)")]
    [DataRow("sum(k) over () * 10 / (k - 3)")]
    [DataRow("row_number() over (order by k desc) * 10 / (k - 3)")]
    public void FailingWindowedSelectList_UsesUpTheRowsItReached(string expression)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table src (k int); insert src values (1), (2), (3), (4), (5)",
            "create table t (id int identity, v int)");
        _ = simulation.AssertSqlError($"insert t (v) select {expression} from src", 8134);
        _ = simulation.ExecuteNonQuery("insert t (v) values (0)");
        AreEqual(4, simulation.ExecuteScalar("select id from t"));
    }

    /// <summary>
    /// A FROM-less source's value meets its column only once the row exists:
    /// under a WHERE that doesn't fold TRUE, a HAVING or TOP (0) nothing
    /// converts, while a table source converts it as its plan starts (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert t (c) select 'abcdef' where 1 = 0", 0)]
    [DataRow("insert t (c) select 'abcdef' from s where 1 = 0", 0)]
    [DataRow("insert t (i) select 'x' where 1 = 0", 0)]
    [DataRow("declare @v int = 1; insert t (c) select 'abcdef' where @v = 0", 0)]
    [DataRow("insert t (c) select top 0 'abcdef'", 0)]
    [DataRow("insert t (c) select 'abcdef' having 1 = 0", 0)]
    [DataRow("insert t (c) select 'abcdef' from s where k = 0", 2628)]
    [DataRow("insert t (c) select 'abcdef'", 2628)]
    public void FromlessSourceUnderAFilter_ConvertsNothing(string insert, int error)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (c varchar(2), i int)", "create table s (k int); insert s values (1)");
        if (error == 0)
            _ = simulation.ExecuteNonQuery(insert);
        else
            _ = simulation.AssertSqlError(insert, error);
    }
}
