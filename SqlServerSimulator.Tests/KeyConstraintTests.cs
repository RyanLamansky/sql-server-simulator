using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for PRIMARY KEY and UNIQUE constraints in CREATE TABLE:
/// inline + table-level grammar, named + auto-named constraints, NULL
/// semantics (PK rejects nullable columns; UNIQUE treats NULLs as equal —
/// SQL Server's signature divergence from ANSI), and the violations'
/// Msg 2627 wording. All error wording is sourced from SQL Server 2025
/// probes against the reference instance.
/// </summary>
[TestClass]
public sealed class KeyConstraintTests
{
    private static int RowCount(Simulation simulation, string projectionColumn, string tableName)
    {
        using var reader = simulation.ExecuteReader($"select {projectionColumn} from {tableName}");
        var count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [TestMethod]
    public void PrimaryKey_Inline_AcceptsInsert()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, x int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20)");
        Assert.AreEqual(20, simulation.ExecuteScalar("select x from t where id = 2"));
    }

    [TestMethod]
    public void PrimaryKey_Inline_DefaultNullability_FlipsToNotNull()
    {
        // SQL Server: declaring `int primary key` without explicit NULL/NOT
        // NULL silently flips the column to NOT NULL — verified against the
        // reference instance.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (null)"));
        Assert.AreEqual("515", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_Inline_ExplicitNull_RaisesMsg8111()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (id int null primary key)"));
        Assert.AreEqual("Cannot define PRIMARY KEY constraint on nullable column in table 't'.", ex.Message);
        Assert.AreEqual("8111", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_Inline_ExplicitNotNull_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null primary key)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        Assert.AreEqual(1, simulation.ExecuteScalar("select id from t"));
    }

    [TestMethod]
    public void PrimaryKey_Identity_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1, 1) not null primary key, x int)");
        _ = simulation.ExecuteNonQuery("insert into t (x) values (10), (20)");
        Assert.AreEqual(2, simulation.ExecuteScalar("select id from t where x = 20"));
    }

    [TestMethod]
    public void PrimaryKey_DuplicateInsert_RaisesMsg2627()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int constraint pk_t primary key)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1)"));
        Assert.AreEqual("Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).", ex.Message);
        Assert.AreEqual("2627", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_DuplicateInsert_StringValue_RendersWithoutQuotes()
    {
        // SQL Server prints the duplicate string value bare in Msg 2627 (no
        // surrounding quotes), distinct from SqlValue.ToString's quoted form.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id nvarchar(50) not null constraint pk_t primary key)");
        _ = simulation.ExecuteNonQuery("insert into t values ('alpha')");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values ('alpha')"));
        Assert.AreEqual("Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (alpha).", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_Composite_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int not null, c int, constraint pk_t primary key (a, b))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 2, 100), (1, 3, 200), (2, 2, 300)");
        Assert.AreEqual(200, simulation.ExecuteScalar("select c from t where a = 1 and b = 3"));
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_Composite_DuplicatePartialKeyOk()
    {
        // (1,2) and (1,3) don't collide because the composite-PK uniqueness
        // is over the *tuple*, not either component alone.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int not null, constraint pk_t primary key (a, b))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 2), (1, 3)");
        Assert.AreEqual(2, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_Composite_DuplicateRaises()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int not null, c int, constraint pk_t primary key (a, b))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 2, 100)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1, 2, 999)"));
        Assert.AreEqual("Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1, 2).", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_OnNullableColumn_RaisesMsg8111()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a int, b int, constraint pk_t primary key (a, b))"));
        Assert.AreEqual("Cannot define PRIMARY KEY constraint on nullable column in table 't'.", ex.Message);
        Assert.AreEqual("8111", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_Multiple_RaisesMsg8110()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a int primary key, b int primary key)"));
        Assert.AreEqual("Cannot add multiple PRIMARY KEY constraints to table 't'.", ex.Message);
        Assert.AreEqual("8110", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_Multiple_InlineAndTableLevel_RaisesMsg8110()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a int not null primary key, b int not null, constraint pk_t primary key (b))"));
        Assert.AreEqual("8110", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_OnText_RaisesMsg1919()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a text primary key)"));
        Assert.AreEqual("Column 'a' in table 't' is of a type that is invalid for use as a key column in an index.", ex.Message);
        Assert.AreEqual("1919", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_OnNText_RaisesMsg1919()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a ntext primary key)"));
        Assert.AreEqual("1919", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_OnImage_RaisesMsg1919()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a image primary key)"));
        Assert.AreEqual("1919", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void PrimaryKey_OnVarcharMax_RaisesMsg1919()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (a varchar(max) not null primary key)"));
        Assert.AreEqual("1919", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Unique_Inline_AllowsDistinctValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int unique)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3)");
        Assert.AreEqual(3, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void Unique_Inline_DuplicateRaises2627WithUniqueKeyWord()
    {
        // SQL Server's Msg 2627 uses "UNIQUE KEY" (not "UNIQUE") in the
        // wording for unique-constraint violations.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int constraint uq_t unique)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1)"));
        Assert.AreEqual("Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).", ex.Message);
        Assert.AreEqual("2627", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Unique_Inline_NullableColumn_AllowsOneNull()
    {
        // SQL Server allows a single NULL row through a single-column UNIQUE
        // constraint (NULLs treated as equal — distinct from ANSI's "NULLs
        // are never equal" rule).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int unique)");
        _ = simulation.ExecuteNonQuery("insert into t values (null)");
        Assert.AreEqual(1, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void Unique_Inline_SecondNull_RaisesMsg2627WithNullDisplay()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int constraint uq_t unique)");
        _ = simulation.ExecuteNonQuery("insert into t values (null)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (null)"));
        Assert.AreEqual("Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>).", ex.Message);
    }

    [TestMethod]
    public void Unique_Composite_NullsTreatedAsEqual()
    {
        // (NULL, 1) and (NULL, 1) collide; (NULL, NULL) collides with another
        // (NULL, NULL); (NULL, 1) and (1, NULL) are distinct.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, constraint uq_t unique (a, b))");
        _ = simulation.ExecuteNonQuery("insert into t values (null, 1), (1, null), (null, null)");

        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (null, 1)"));
        Assert.AreEqual("Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>, 1).", ex.Message);
    }

    [TestMethod]
    public void Unique_Composite_AllNullDuplicate_RaisesWithBothNullDisplay()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, constraint uq_t unique (a, b))");
        _ = simulation.ExecuteNonQuery("insert into t values (null, null)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (null, null)"));
        Assert.AreEqual("Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>, <NULL>).", ex.Message);
    }

    [TestMethod]
    public void Unique_TableLevelUnnamed_AutoNameMatchesShape()
    {
        // Auto-name shape: UQ__<table truncated>__<16 hex>. Test asserts on
        // structure, not the exact hex (which is a deterministic FNV hash of
        // table + columns; the hex is reproducible but cosmetic).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, unique (a))");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1)"));
        Assert.StartsWith("Violation of UNIQUE KEY constraint 'UQ__t__", ex.Message);
        Assert.Contains("'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_TableLevelUnnamed_AutoNameStartsWithPK()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, primary key (a))");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1)"));
        Assert.StartsWith("Violation of PRIMARY KEY constraint 'PK__t__", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_Clustered_Accepted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key clustered)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        Assert.AreEqual(1, simulation.ExecuteScalar("select id from t"));
    }

    [TestMethod]
    public void Unique_NonClustered_Accepted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int unique nonclustered)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        Assert.AreEqual(1, simulation.ExecuteScalar("select id from t"));
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_AscDesc_Accepted()
    {
        // SQL Server allows per-column ASC/DESC ordering hints in a key list;
        // they affect physical-index direction (which the simulator doesn't
        // model) but should parse without error.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int not null, primary key (a asc, b desc))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 2)");
        Assert.AreEqual(1, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void Unique_PreservesUserNamedConstraint_InMessage()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, constraint my_unique_x unique (a, b))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 2)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1, 2)"));
        Assert.Contains("constraint 'my_unique_x'", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_AndUnique_OnSameTable_BothEnforced()
    {
        // PK violation reports PK; UNIQUE violation on a separate column
        // reports UNIQUE.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int constraint pk_t primary key, x int constraint uq_t unique)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 100)");

        var pkEx = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (1, 200)"));
        Assert.Contains("PRIMARY KEY constraint 'pk_t'", pkEx.Message);

        var uqEx = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (2, 100)"));
        Assert.Contains("UNIQUE KEY constraint 'uq_t'", uqEx.Message);
    }

    [TestMethod]
    public void PrimaryKey_OnComputedColumn_NotSupported()
    {
        // Real SQL Server allows PK/UNIQUE on a computed column (and silently
        // persists it for the index); the simulator's v1 doesn't model this
        // and surfaces NotSupportedException naming the missing capability.
        var simulation = new Simulation();
        var ex = Assert.Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("create table t (a int not null, c as a + 1, primary key (c))"));
        Assert.Contains("computed column", ex.Message);
    }

    [TestMethod]
    public void MergeInsert_DuplicateKey_RaisesMsg2627()
    {
        // EF Core's batched MERGE-INSERT shape; key-constraint enforcement
        // must fire on the MERGE write path too.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null constraint pk_t primary key, x int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 100)");
        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteNonQuery("merge into t using (values (1, 200)) as src (id, x) on 1=0 when not matched then insert (id, x) values (src.id, src.x);"));
        Assert.Contains("PRIMARY KEY constraint 'pk_t'", ex.Message);
    }
}
