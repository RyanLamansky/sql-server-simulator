namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for PRIMARY KEY and UNIQUE constraints in CREATE TABLE:
/// inline + table-level grammar, named + auto-named constraints, NULL
/// semantics (PK rejects nullable; UNIQUE treats NULLs as equal — SQL Server's
/// signature divergence from ANSI), and Msg 2627 wording.
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
        Assert.AreEqual(20, new Simulation().ExecuteScalar("""
            create table t (id int primary key, x int);
            insert t values (1, 10), (2, 20);
            select x from t where id = 2
            """));
    }

    [TestMethod]
    public void PrimaryKey_Inline_DefaultNullability_FlipsToNotNull()
    {
        // Declaring `int primary key` without explicit NULL/NOT NULL silently flips to NOT NULL.
        _ = new Simulation().AssertSqlError("""
            create table t (id int primary key);
            insert t values (null)
            """, 515);
    }

    [TestMethod]
    public void PrimaryKey_Inline_ExplicitNull_RaisesMsg8111()
        => new Simulation().AssertSqlError("create table t (id int null primary key)", 8111,
            "Cannot define PRIMARY KEY constraint on nullable column in table 't'.");

    [TestMethod]
    public void PrimaryKey_Inline_ExplicitNotNull_Works()
    {
        Assert.AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (1);
            select id from t
            """));
    }

    [TestMethod]
    public void PrimaryKey_Identity_Works()
    {
        Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int identity(1, 1) not null primary key, x int);
            insert t (x) values (10), (20);
            select id from t where x = 20
            """));
    }

    [TestMethod]
    public void PrimaryKey_DuplicateInsert_RaisesMsg2627()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int constraint pk_t primary key);
            insert t values (1)
            """);
        simulation.AssertSqlError("insert t values (1)", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).");
    }

    [TestMethod]
    public void PrimaryKey_DuplicateInsert_StringValue_RendersWithoutQuotes()
    {
        // SQL Server prints the duplicate string value bare (no surrounding quotes), unlike SqlValue.ToString.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id nvarchar(50) not null constraint pk_t primary key);
            insert t values ('alpha')
            """);
        simulation.AssertSqlError("insert t values ('alpha')", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (alpha).");
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_Composite_Works()
    {
        Assert.AreEqual(200, new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c int, constraint pk_t primary key (a, b));
            insert t values (1, 2, 100), (1, 3, 200), (2, 2, 300);
            select c from t where a = 1 and b = 3
            """));
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_Composite_DuplicatePartialKeyOk()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null, b int not null, constraint pk_t primary key (a, b));
            insert t values (1, 2), (1, 3)
            """);
        Assert.AreEqual(2, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_Composite_DuplicateRaises()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null, b int not null, c int, constraint pk_t primary key (a, b));
            insert t values (1, 2, 100)
            """);
        simulation.AssertSqlError("insert t values (1, 2, 999)", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1, 2).");
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_OnExplicitNullColumn_RaisesMsg8111()
        => new Simulation().AssertSqlError("create table t (a int null, b int, constraint pk_t primary key (a, b))", 8111,
            "Cannot define PRIMARY KEY constraint on nullable column in table 't'.");

    [TestMethod]
    public void PrimaryKey_TableLevel_BareColumns_PromoteToNotNull()
    {
        // Probe-confirmed against SQL Server 2025: table-level PK promotes
        // bare-int columns (no explicit NULL/NOT NULL) to NOT NULL —
        // inserting NULL surfaces Msg 515 rather than ignoring the PK.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, constraint pk_t primary key (a, b))");
        _ = simulation.AssertSqlError("insert t values (null, 2)", 515);
    }

    [TestMethod]
    public void PrimaryKey_Multiple_RaisesMsg8110()
        => new Simulation().AssertSqlError("create table t (a int primary key, b int primary key)", 8110,
            "Cannot add multiple PRIMARY KEY constraints to table 't'.");

    [TestMethod]
    public void PrimaryKey_Multiple_InlineAndTableLevel_RaisesMsg8110()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int not null primary key, b int not null, constraint pk_t primary key (b))", 8110);

    [TestMethod]
    [DataRow("text")]
    [DataRow("ntext")]
    [DataRow("image")]
    public void PrimaryKey_OnLobType_RaisesMsg1919(string columnType)
        => new Simulation().AssertSqlError($"create table t (a {columnType} primary key)", 1919,
            "Column 'a' in table 't' is of a type that is invalid for use as a key column in an index.");

    [TestMethod]
    public void PrimaryKey_OnVarcharMax_RaisesMsg1919()
        => _ = new Simulation().AssertSqlError("create table t (a varchar(max) not null primary key)", 1919);

    [TestMethod]
    public void Unique_Inline_AllowsDistinctValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int unique);
            insert t values (1), (2), (3)
            """);
        Assert.AreEqual(3, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void Unique_Inline_DuplicateRaises2627WithUniqueKeyWord()
    {
        // SQL Server uses "UNIQUE KEY" (not "UNIQUE") in Msg 2627 wording.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int constraint uq_t unique);
            insert t values (1)
            """);
        simulation.AssertSqlError("insert t values (1)", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).");
    }

    [TestMethod]
    public void Unique_Inline_NullableColumn_AllowsOneNull()
    {
        // Single NULL through single-column UNIQUE allowed (NULLs treated as equal — distinct from ANSI).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int unique);
            insert t values (null)
            """);
        Assert.AreEqual(1, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void Unique_Inline_SecondNull_RaisesMsg2627WithNullDisplay()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int constraint uq_t unique);
            insert t values (null)
            """);
        simulation.AssertSqlError("insert t values (null)", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>).");
    }

    [TestMethod]
    public void Unique_Composite_NullsTreatedAsEqual()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, constraint uq_t unique (a, b));
            insert t values (null, 1), (1, null), (null, null)
            """);
        simulation.AssertSqlError("insert t values (null, 1)", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>, 1).");
    }

    [TestMethod]
    public void Unique_Composite_AllNullDuplicate_RaisesWithBothNullDisplay()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, constraint uq_t unique (a, b));
            insert t values (null, null)
            """);
        simulation.AssertSqlError("insert t values (null, null)", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>, <NULL>).");
    }

    [TestMethod]
    public void Unique_TableLevelUnnamed_AutoNameMatchesShape()
    {
        // Auto-name shape: UQ__<table truncated>__<16 hex>. Hex is deterministic FNV but cosmetic.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, unique (a));
            insert t values (1)
            """);
        var ex = simulation.AssertSqlError("insert t values (1)", 2627);
        Assert.StartsWith("Violation of UNIQUE KEY constraint 'UQ__t__", ex.Message);
        Assert.Contains("'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_TableLevelUnnamed_AutoNameStartsWithPK()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null, primary key (a));
            insert t values (1)
            """);
        var ex = simulation.AssertSqlError("insert t values (1)", 2627);
        Assert.StartsWith("Violation of PRIMARY KEY constraint 'PK__t__", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_Clustered_Accepted()
    {
        Assert.AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int primary key clustered);
            insert t values (1);
            select id from t
            """));
    }

    [TestMethod]
    public void Unique_NonClustered_Accepted()
    {
        Assert.AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int unique nonclustered);
            insert t values (1);
            select id from t
            """));
    }

    [TestMethod]
    public void PrimaryKey_TableLevel_AscDesc_Accepted()
    {
        // ASC/DESC affects physical-index direction (not modeled); should parse without error.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null, b int not null, primary key (a asc, b desc));
            insert t values (1, 2)
            """);
        Assert.AreEqual(1, RowCount(simulation, "a", "t"));
    }

    [TestMethod]
    public void Unique_PreservesUserNamedConstraint_InMessage()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, constraint my_unique_x unique (a, b));
            insert t values (1, 2)
            """);
        var ex = simulation.AssertSqlError("insert t values (1, 2)", 2627);
        Assert.Contains("constraint 'my_unique_x'", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_AndUnique_OnSameTable_BothEnforced()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int constraint pk_t primary key, x int constraint uq_t unique);
            insert t values (1, 100)
            """);

        var pkEx = simulation.AssertSqlError("insert t values (1, 200)", 2627);
        Assert.Contains("PRIMARY KEY constraint 'pk_t'", pkEx.Message);

        var uqEx = simulation.AssertSqlError("insert t values (2, 100)", 2627);
        Assert.Contains("UNIQUE KEY constraint 'uq_t'", uqEx.Message);
    }

    // --- PK / UNIQUE on computed columns ---

    [TestMethod]
    public void PrimaryKey_OnNonPersistedComputed_RaisesMsg1711()
    {
        // Probe-confirmed: real SQL Server raises Msg 1711 at CREATE TABLE when
        // PK targets a non-persisted computed column.
        var ex = new Simulation().AssertSqlError(
            "create table t (a int not null, c as a + 1, primary key (c))", 1711);
        Assert.Contains("computed column has to be persisted", ex.Message);
        Assert.Contains("'c'", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_OnPersistedComputed_PromotesToNotNull()
    {
        // Probe-confirmed: a PERSISTED computed column named by a PRIMARY KEY
        // is promoted to NOT NULL exactly as a regular column is, even when
        // the expression's source column is nullable — so no Msg 8111, and a
        // row whose expression evaluates to NULL fails with Msg 515 instead.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int null, c as a + 1 persisted, primary key (c));
            insert t (a) values (1)
            """);
        Assert.AreEqual("NO", sim.ExecuteScalar(
            "select is_nullable from information_schema.columns where table_name = 't' and column_name = 'c'"));
        var ex = sim.AssertSqlError("insert t (a) values (null)", 515);
        Assert.Contains("column 'c'", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_OnPersistedComputed_Inline_PromotesToNotNull()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int null, c as a + 1 persisted primary key);
            insert t (a) values (1), (2);
            select count(*) from t
            """));

    [TestMethod]
    public void PrimaryKey_OnPersistedNotNullComputed_TableLevel_Succeeds()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, c as a + 1 persisted not null, primary key (c));
            insert t(a) values (1), (2);
            select count(*) from t
            """));

    [TestMethod]
    public void PrimaryKey_OnPersistedNotNullComputed_Inline_Succeeds()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, c as a + 1 persisted not null primary key);
            insert t(a) values (1), (2);
            select count(*) from t
            """));

    [TestMethod]
    public void PrimaryKey_OnPersistedComputed_EnforcesUniqueness()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null, c as a + 1 persisted not null, primary key (c));
            insert t(a) values (1), (2)
            """);
        // a=1 → c=2 collides with existing c=2 from a=1
        var ex = simulation.AssertSqlError("insert t(a) values (1)", 2627);
        Assert.Contains("PRIMARY KEY", ex.Message);
        Assert.Contains("(2)", ex.Message);
    }

    [TestMethod]
    public void PrimaryKey_OnPersistedComputed_Compound_Succeeds()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, c as a + 1 persisted not null, x int not null,
                            primary key (a, c));
            insert t(a, x) values (1, 100), (2, 200);
            select count(*) from t
            """));

    [TestMethod]
    public void Unique_OnPersistedComputed_TableLevel_Succeeds()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, c as a * 10 persisted, unique (c));
            insert t(a) values (1), (2);
            select count(*) from t
            """));

    [TestMethod]
    public void Unique_OnPersistedComputed_EnforcesUniqueness()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null, c as a * 10 persisted, unique (c));
            insert t(a) values (1), (2)
            """);
        var ex = simulation.AssertSqlError("insert t(a) values (1)", 2627);
        Assert.Contains("UNIQUE KEY", ex.Message);
        Assert.Contains("(10)", ex.Message);
    }

    [TestMethod]
    public void Unique_OnNonPersistedComputed_NotSupported()
    {
        // Real SQL Server allows this; the simulator defers UNIQUE-on-non-
        // persisted because enforcement would need per-row expression
        // evaluation rather than storage-ordinal lookup.
        _ = Assert.Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "create table t (a int not null, c as a * 10, unique (c))"));
    }

    [TestMethod]
    public void AlterTable_AddPrimaryKey_OnPersistedComputed_Succeeds()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, c as a + 1 persisted not null);
            insert t(a) values (1), (2);
            alter table t add constraint pk_t primary key (c);
            select count(*) from t
            """));

    [TestMethod]
    public void AlterTable_AddPrimaryKey_OnPersistedComputed_RejectsExistingDuplicate()
        => _ = new Simulation().AssertSqlError("""
            create table t (a int not null, c as a + 1 persisted not null);
            insert t(a) values (1), (1);
            alter table t add constraint pk_t primary key (c)
            """, 1505);

    [TestMethod]
    public void AlterTable_AddPrimaryKey_OnNonPersistedComputed_RaisesMsg8111()
        => _ = new Simulation().AssertSqlError("""
            create table t (a int not null, c as a + 1);
            alter table t add constraint pk_t primary key (c)
            """, 8111);

    [TestMethod]
    public void AlterTable_AddUnique_OnPersistedComputed_Succeeds()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, c as a + 1 persisted);
            insert t(a) values (1), (2);
            alter table t add unique (c);
            select count(*) from t
            """));

    [TestMethod]
    public void AlterTable_AddUnique_OnNonPersistedComputed_NotSupported()
        => _ = Assert.Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("""
            create table t (a int not null, c as a + 1);
            alter table t add unique (c)
            """));

    [TestMethod]
    public void MergeInsert_DuplicateKey_RaisesMsg2627()
    {
        // EF Core's batched MERGE-INSERT shape; key enforcement must fire on MERGE write path too.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null constraint pk_t primary key, x int);
            insert t values (1, 100)
            """);
        var ex = simulation.AssertSqlError(
            "merge into t using (values (1, 200)) as src (id, x) on 1=0 when not matched then insert (id, x) values (src.id, src.x);", 2627);
        Assert.Contains("PRIMARY KEY constraint 'pk_t'", ex.Message);
    }

    /// <summary>
    /// A single CREATE TABLE may declare at most one CLUSTERED key. Real gives
    /// this its own <b>Msg 8112</b> rather than the Msg 1902 the CREATE INDEX
    /// and ALTER ADD CONSTRAINT paths raise — 1902 names the pre-existing
    /// clustered index, which doesn't exist yet when both constraints arrive in
    /// the same statement (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void TwoClusteredConstraints_InOneCreateTable_Raises8112()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (a int not null primary key clustered, b int not null unique clustered)", 8112);
        Assert.AreEqual("Cannot add more than one clustered index for constraints on table 't'.", ex.Message);
    }

    /// <summary>
    /// The multiple-PRIMARY-KEY check outranks the clustered check — two PKs
    /// are both clustered by default, and real reports Msg 8110 for them
    /// (probe-confirmed), so gate order is load-bearing here.
    /// </summary>
    [TestMethod]
    public void TwoPrimaryKeys_BothClusteredByDefault_StillRaises8110()
        => new Simulation().AssertSqlError("create table t (a int primary key, b int primary key)", 8110);

    /// <summary>
    /// The constraint paths carry the same one-clustered-per-table rule the
    /// CREATE INDEX path already enforced: real raises <b>Msg 1902 State 3</b>
    /// naming the existing clustered index, whether it arrived as a constraint
    /// or as a CREATE CLUSTERED INDEX.
    /// </summary>
    [TestMethod]
    [DataRow("alter table t add constraint pk_t primary key clustered (a)", "alter table t add constraint uq_t unique clustered (b)", "pk_t")]
    public void SecondClusteredConstraint_ViaAlter_Raises1902(string first, string second, string existingName)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null, b int not null)");
        _ = sim.ExecuteNonQuery(first);

        var ex = sim.AssertSqlError(second, 1902);
        Assert.AreEqual(
            $"Cannot create more than one clustered index on table 't'. Drop the existing clustered index '{existingName}' before creating another.",
            ex.Message);
        Assert.AreEqual(3, ex.State);
    }

    /// <summary>
    /// A clustered constraint added after a CREATE CLUSTERED INDEX hits the
    /// same rule from the other direction.
    /// </summary>
    [TestMethod]
    public void ClusteredConstraint_AfterClusteredIndex_Raises1902()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null, b int not null)");
        _ = sim.ExecuteNonQuery("create clustered index ix_t on t(a)");

        var ex = sim.AssertSqlError("alter table t add constraint pk_t primary key clustered (b)", 1902);
        Assert.AreEqual(
            "Cannot create more than one clustered index on table 't'. Drop the existing clustered index 'ix_t' before creating another.",
            ex.Message);
    }

    /// <summary>
    /// A single clustered key — the overwhelmingly common shape — is untouched
    /// by either gate, including alongside a nonclustered UNIQUE.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int not null primary key clustered, b bit not null default (0))")]
    [DataRow("create table t (a int not null primary key clustered, b int not null unique)")]
    [DataRow("create table t (a int identity(1,1) not null constraint pk_t primary key clustered, b int not null)")]
    public void SingleClusteredKey_Accepted(string sql)
        => _ = new Simulation().ExecuteNonQuery(sql);
}
