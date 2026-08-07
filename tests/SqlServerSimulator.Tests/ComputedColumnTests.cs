namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for computed columns: <c>col AS expr [PERSISTED [NOT NULL]]</c>
/// grammar in CREATE TABLE, write reservation (Msg 271), computed-of-computed
/// rejection (Msg 1759), and the "computed column constraints require PERSISTED"
/// rule (Msg 8183).
/// </summary>
[TestClass]
public sealed class ComputedColumnTests
{
    [TestMethod]
    public void Computed_NonPersisted_EvaluatesAtRead()
        => Assert.AreEqual(30, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c as a + b);
            insert t (a, b) values (10, 20);
            select c from t
            """));

    // Bare INSERT without column list populates only writable columns; computed cols excluded like identity.
    [TestMethod]
    public void Computed_NonPersisted_OmittedFromAutoColumnList()
        => Assert.AreEqual(7, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c as a + b);
            insert t values (3, 4);
            select c from t
            """));

    [TestMethod]
    public void Computed_NonPersisted_TracksUnderlyingChanges_AcrossRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, c as a * 10);
            insert t (a) values (1), (2), (3)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select c from t order by a").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, values);
    }

    [TestMethod]
    public void Computed_Persisted_StoresAndReadsBack()
        => Assert.AreEqual(12, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c as a + b persisted);
            insert t (a, b) values (5, 7);
            select c from t
            """));

    [TestMethod]
    public void Computed_Persisted_NotNull_AcceptsNonNullSource()
        => Assert.AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c as a + b persisted not null);
            insert t (a, b) values (1, 2);
            select c from t
            """));

    [TestMethod]
    public void Computed_ConstantExpression()
        => Assert.AreEqual(42, new Simulation().ExecuteScalar("""
            create table t (a int, c as 42);
            insert t (a) values (1);
            select c from t
            """));

    // Real SQL Server resolves all column refs after the entire column list is parsed.
    [TestMethod]
    public void Computed_ForwardReference()
        => Assert.AreEqual(11, new Simulation().ExecuteScalar("""
            create table t (c as b + 1, b int);
            insert t (b) values (10);
            select c from t
            """));

    [TestMethod]
    public void Computed_TypeInference_FromArithmetic()
        => Assert.AreEqual(41000, new Simulation().ExecuteScalar("""
            create table t (a int, b smallint, c as a + b);
            insert t (a, b) values (40000, 1000);
            select c from t
            """));

    [TestMethod]
    public void Computed_OutputClause_SeesNonPersistedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, c as a * 2)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("insert t (a) output inserted.c values (21)").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void Computed_OutputClause_SeesPersistedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, c as a * 2 persisted)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("insert t (a) output inserted.c values (21)").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void Computed_InsertIntoColumn_RaisesMsg271()
        => new Simulation().AssertSqlError("""
            create table t (a int, c as a + 1);
            insert t (a, c) values (1, 99)
            """, 271,
            "The column \"c\" cannot be modified because it is either a computed column or is the result of a UNION operator.");

    [TestMethod]
    public void Computed_ReferencingComputed_RaisesMsg1759()
        => new Simulation().AssertSqlError("create table t (a int, c as a + 1, d as c + 1)", 1759,
            "Computed column 'c' in table 't' is not allowed to be used in another computed-column definition.");

    [TestMethod]
    public void Computed_PersistedReferencingNonPersisted_RaisesMsg1759()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1, d as c + 1 persisted)", 1759);

    [TestMethod]
    public void Computed_SelfReference_RaisesMsg1759()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as c + 1)", 1759);

    [TestMethod]
    public void Computed_Identity_RaisesMsg8183()
        => new Simulation().AssertSqlError("create table t (a int, c as a + 1 identity(1, 1))", 8183,
            "Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.");

    [TestMethod]
    public void Computed_Default_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 default 5)", 8183);

    [TestMethod]
    public void Computed_NotNullWithoutPersisted_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 not null)", 8183);

    [TestMethod]
    public void Computed_ExplicitNullWithoutPersisted_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 null)", 8183);

    // Real SQL Server rejects explicit NULL after PERSISTED; only `PERSISTED` alone or `PERSISTED NOT NULL` accepted.
    [TestMethod]
    public void Computed_PersistedNull_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 persisted null)", 8183);

    [TestMethod]
    public void Computed_AllColumns_ProjectsAlongsideStored()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, c as a + b);
            insert t (a, b) values (2, 3)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a, b, c from t").ExecuteReader();
        Assert.AreEqual(3, reader.FieldCount);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(3, reader.GetInt32(1));
        Assert.AreEqual(5, reader.GetInt32(2));
    }

    [TestMethod]
    public void Computed_WhereClauseFiltersOnComputedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, c as a + b);
            insert t (a, b) values (1, 2), (5, 5), (10, 10)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a from t where c >= 10 order by a").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(5, reader.GetInt32(0));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(10, reader.GetInt32(0));
        Assert.IsFalse(reader.Read());
    }

    /// <summary>
    /// Regression: <c>varchar(N) + varchar(M)</c> in a computed column should
    /// resolve to <c>varchar(N+M)</c>. The earlier behavior dropped to
    /// length-unspecified varchar (defaulting to 1 in column context),
    /// which clobbered the persisted value to its first character.
    /// </summary>
    [TestMethod]
    public void Computed_VarcharConcat_PreservesCombinedLength()
        => Assert.AreEqual(
            "abcdefghijklmnopqrstuvwxyz1234",
            new Simulation().ExecuteScalar("""
                create table t (a varchar(10), b varchar(20), c as a + b persisted);
                insert t (a, b) values ('abcdefghij', 'klmnopqrstuvwxyz1234');
                select c from t
                """));

    /// <summary>
    /// sys.computed_columns.definition captures the parenthesized source text of
    /// the AS (…) body — so DacFx / SMO re-emit a re-parseable computed column.
    /// The captured text is wrapped in a single paren pair: an unparenthesized
    /// body gains one, an already-parenthesized body is not double-wrapped.
    /// is_persisted reflects the PERSISTED marker.
    /// </summary>
    [TestMethod]
    public void ComputedColumn_Definition_CapturesParenthesizedSourceText()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table dbo.t (a int, b int, " +
            "s1 as a + b, " +
            "s2 as (a + b) persisted, " +
            "s3 as (concat(a, 'x')))");
        Assert.AreEqual("(a + b)", sim.ExecuteScalar("select definition from sys.computed_columns where name = 's1'"));
        Assert.AreEqual("(a + b)", sim.ExecuteScalar("select definition from sys.computed_columns where name = 's2'"));
        Assert.AreEqual("(concat(a, 'x'))", sim.ExecuteScalar("select definition from sys.computed_columns where name = 's3'"));
        Assert.IsTrue((bool)sim.ExecuteScalar("select is_persisted from sys.computed_columns where name = 's2'")!);
        Assert.IsFalse((bool)sim.ExecuteScalar("select is_persisted from sys.computed_columns where name = 's1'")!);
    }

    /// <summary>
    /// ALTER TABLE ADD of a computed column captures its definition the same way
    /// as CREATE TABLE (both route through the shared column-definition parser),
    /// which is also the path the bacpac loader replays for imported computed
    /// columns.
    /// </summary>
    [TestMethod]
    public void ComputedColumn_Definition_CapturedOnAlterTableAdd()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (a int, b int)");
        _ = sim.ExecuteNonQuery("alter table dbo.t add c as (a * b)");
        Assert.AreEqual("(a * b)", sim.ExecuteScalar("select definition from sys.computed_columns where name = 'c'"));
    }

    // --- CHECK constraints over computed columns ---

    /// <summary>
    /// An inline CHECK on a PERSISTED computed column is enforced against the
    /// stored value: an INSERT whose expression falls outside the predicate
    /// raises Msg 547, and so does an UPDATE of the underlying column that
    /// drives the expression out of range without naming the computed column.
    /// </summary>
    [TestMethod]
    public void Check_InlineOnPersistedComputed_EnforcedOnInsertAndUpdate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, cc as a + 1 persisted check (cc > 0));
            insert t (a) values (5)
            """);
        var insertEx = sim.AssertSqlError("insert t (a) values (-5)", 547);
        Assert.Contains("CHECK constraint", insertEx.Message);
        var updateEx = sim.AssertSqlError("update t set a = -9", 547);
        Assert.Contains("UPDATE statement conflicted", updateEx.Message);
        Assert.AreEqual(6, sim.ExecuteScalar("select cc from t"));
    }

    [TestMethod]
    public void Check_InlineOnNonPersistedComputed_RaisesMsg8183()
        => new Simulation().AssertSqlError("create table t (a int, cc as a + 1 check (cc > 0))", 8183,
            "Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.");

    /// <summary>
    /// The unnamed inline form auto-names as a column-level CHECK
    /// (<c>CK__&lt;table&gt;__&lt;column&gt;__&lt;hex&gt;</c>), the same shape a
    /// regular column's inline CHECK gets; the named form keeps its name.
    /// </summary>
    [TestMethod]
    public void Check_InlineOnPersistedComputed_NamingFollowsTheColumnLevelShape()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, cc as a + 1 persisted check (cc > 0));
            create table u (a int, cc as a + 1 persisted constraint ck_u check (cc > 0))
            """);
        Assert.StartsWith("CK__t__cc__", (string)sim.ExecuteScalar(
            "select name from sys.check_constraints where object_name(parent_object_id) = 't'")!);
        Assert.AreEqual("ck_u", sim.ExecuteScalar(
            "select name from sys.check_constraints where object_name(parent_object_id) = 'u'"));
    }

    /// <summary>
    /// A computed column takes several inline constraints in any order — real
    /// accepts <c>PERSISTED PRIMARY KEY CHECK (…)</c>, the reverse order, and
    /// the doubly-named <c>CONSTRAINT … CHECK (…) CONSTRAINT … UNIQUE</c> pair.
    /// </summary>
    [TestMethod]
    public void Check_InlineOnComputed_CoexistsWithKeyConstraint()
        => Assert.AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (a int, cc as a + 1 persisted primary key check (cc > 0));
            create table u (a int, cc as a + 1 persisted check (cc > 0) primary key);
            create table v (a int, cc as a + 1 persisted constraint ck_v check (cc > 0) constraint uq_v unique);
            select count(*) from sys.check_constraints
            """));

    [TestMethod]
    public void Check_InlineOnPersistedComputed_ReferencingPeer_RaisesMsg8141()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int, b int, cc as a + 1 persisted check (cc > b))", 8141);

    /// <summary>
    /// A CHECK predicate may not read a non-persisted computed column, in any
    /// of the forms that reach constraint resolution — Msg 1764, with real's
    /// capitalized "Computed Column" and the 'CHECK CONSTRAINT' kind. The
    /// inline case here sits on a *persisted* column and reaches a
    /// non-persisted peer, which real reports as Msg 1764 rather than the
    /// Msg 8141 peer-reference error (probe-confirmed precedence).
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int, cc as a + 1, constraint ck_t check (cc > 0))")]
    [DataRow("create table t (a int, cc as a + 1, check (cc > 0))")]
    [DataRow("create table t (a int, cc as a + 1, c2 as a + 2 persisted check (cc > 0))")]
    public void Check_OverNonPersistedComputed_RaisesMsg1764(string createTable)
        => new Simulation().AssertSqlError(createTable, 1764,
            "Computed Column 'cc' in table 't' is invalid for use in 'CHECK CONSTRAINT' because it is not persisted.");

    [TestMethod]
    public void Check_TableLevelOverPersistedComputed_Enforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, cc as a + 1 persisted, constraint ck_t check (cc > 0))");
        _ = sim.AssertSqlError("insert t (a) values (-5)", 547);
    }

    /// <summary>
    /// <c>ALTER TABLE … ADD CONSTRAINT … CHECK</c> applies the same Msg 1764
    /// gate, and <c>WITH NOCHECK</c> doesn't excuse it — real rejects the
    /// declaration itself, not just the data validation it skips.
    /// </summary>
    [TestMethod]
    [DataRow("alter table t add constraint ck_t check (cc > 0)")]
    [DataRow("alter table t with nocheck add constraint ck_t check (cc > 0)")]
    public void Check_AlterTableAddOverNonPersistedComputed_RaisesMsg1764(string alterTable)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, cc as a + 1)");
        sim.AssertSqlError(alterTable, 1764,
            "Computed Column 'cc' in table 't' is invalid for use in 'CHECK CONSTRAINT' because it is not persisted.");
        Assert.AreEqual(0, sim.ExecuteScalar("select count(*) from sys.check_constraints"));
    }

    [TestMethod]
    public void Check_AlterTableAddOverPersistedComputed_Enforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, cc as a + 1 persisted);
            alter table t add constraint ck_t check (cc > 0)
            """);
        _ = sim.AssertSqlError("insert t (a) values (-5)", 547);
    }

    /// <summary>
    /// <c>ALTER TABLE … ADD</c> of a computed column carries the inline CHECK
    /// through the shared column parser, with the same persistence gate — and
    /// the added column's predicate is checked against the columns already on
    /// the table too.
    /// </summary>
    [TestMethod]
    public void Check_AlterTableAddComputedColumn_InlineCheckEnforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int);
            alter table t add cc as a + 1 persisted check (cc > 0)
            """);
        _ = sim.AssertSqlError("insert t (a) values (-5)", 547);
    }

    /// <summary>
    /// <c>PERSISTED</c> — and <c>PERSISTED NOT NULL</c> — may be a batch's very
    /// last token: everything the suffix parser looks for past the keyword is
    /// optional, so the form needs no trailing <c>;</c> (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int); alter table t add cc as a + 1 persisted")]
    [DataRow("create table t (a int not null); alter table t add cc as a + 1 persisted not null")]
    public void AlterTableAddComputedColumn_PersistedAsFinalToken_Succeeds(string sql)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(sql);
        Assert.AreEqual(3, sim.ExecuteScalar("insert t (a) values (2); select cc from t"));
    }

    [TestMethod]
    public void Check_AlterTableAddNonPersistedComputedColumn_InlineCheck_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int); alter table t add cc as a + 1 check (cc > 0)", 8183);

    [TestMethod]
    public void Check_AlterTableAddComputedColumn_InlineCheckOverNonPersistedPeer_RaisesMsg1764()
        => new Simulation().AssertSqlError(
            "create table t (a int, cc as a + 1); alter table t add c2 as a + 2 persisted check (cc > 0)", 1764,
            "Computed Column 'cc' in table 't' is invalid for use in 'CHECK CONSTRAINT' because it is not persisted.");

    /// <summary>
    /// Table variables and table types route through the same column parser,
    /// so both gates reach them: an inline CHECK on a persisted computed
    /// column is enforced, and a table-level CHECK over a non-persisted one
    /// raises Msg 1764 naming the variable / type.
    /// </summary>
    [TestMethod]
    public void Check_TableVariable_InlineOnPersistedComputed_Enforced()
        => _ = new Simulation().AssertSqlError("""
            declare @t table (a int, cc as a + 1 persisted check (cc > 0));
            insert @t (a) values (-5)
            """, 547);

    [TestMethod]
    public void Check_TableVariable_OverNonPersistedComputed_RaisesMsg1764()
        => new Simulation().AssertSqlError("declare @t table (a int, cc as a + 1, check (cc > 0))", 1764,
            "Computed Column 'cc' in table '@t' is invalid for use in 'CHECK CONSTRAINT' because it is not persisted.");

    [TestMethod]
    public void Check_TableType_OverNonPersistedComputed_RaisesMsg1764()
        => new Simulation().AssertSqlError("create type tt as table (a int, cc as a + 1, check (cc > 0))", 1764,
            "Computed Column 'cc' in table 'tt' is invalid for use in 'CHECK CONSTRAINT' because it is not persisted.");

    /// <summary>
    /// <c>PERSISTED NOT NULL</c> is the one nullability form a computed column
    /// accepts, and it composes with an inline CHECK; the NOT NULL is enforced
    /// with Msg 515 when the expression evaluates to NULL.
    /// </summary>
    [TestMethod]
    public void Check_InlineOnPersistedNotNullComputed_BothEnforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, cc as a + 1 persisted not null check (cc > 0))");
        _ = sim.AssertSqlError("insert t (a) values (-5)", 547);
        _ = sim.AssertSqlError("insert t (a) values (null)", 515);
    }

    /// <summary>
    /// A computed column's declared nullability is its expression's, derived
    /// with the rules real derives a projection's COLMETADATA flag with —
    /// probed cell for cell against SQL Server 2025. It is load-bearing beyond
    /// the catalog: a system-versioned base and its history table have to agree
    /// on it, which is what WideWorldImporters' <c>Warehouse.StockItems</c>
    /// (a <c>CONCAT</c> computed column against a NOT NULL history column)
    /// turns on. The table carries <c>a int NOT NULL</c>, <c>b int NULL</c>,
    /// <c>s nvarchar(10) NOT NULL</c> and <c>u nvarchar(10) NULL</c>.
    /// </summary>
    [TestMethod]
    [DataRow("a", false)]
    [DataRow("b", true)]
    [DataRow("1", false)]
    [DataRow("a + b", true)]
    [DataRow("s + u", true)]
    [DataRow("s + s", false)]
    [DataRow("len(s)", true)]
    [DataRow("cast(a as bigint)", true)]
    [DataRow("coalesce(b, 0)", true)]
    [DataRow("isnull(b, 0)", false)]
    [DataRow("case when a = 1 then 1 else 2 end", false)]
    [DataRow("getdate()", false)]
    [DataRow("concat(s, u)", false)]
    [DataRow("abs(a)", true)]
    [DataRow("a * 2", true)]
    public void ComputedColumn_NullabilityFollowsTheExpression(string expression, bool expectedNullable)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            $"create table t (a int not null, b int null, s nvarchar(10) not null, u nvarchar(10) null, cc as {expression})");
        Assert.AreEqual(expectedNullable, (bool)sim.ExecuteScalar("select is_nullable from sys.columns where object_id = object_id('t') and name = 'cc'")!);
    }

    /// <summary>The same inference runs on the <c>ALTER TABLE … ADD</c> path.</summary>
    [TestMethod]
    public void ComputedColumn_AddedByAlterTable_NullabilityFollowsTheExpression()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a nvarchar(10) not null, b nvarchar(10) null)");
        _ = sim.ExecuteNonQuery("alter table t add cc as concat(a, b), dd as a + b");
        Assert.IsFalse((bool)sim.ExecuteScalar("select is_nullable from sys.columns where object_id = object_id('t') and name = 'cc'")!);
        Assert.IsTrue((bool)sim.ExecuteScalar("select is_nullable from sys.columns where object_id = object_id('t') and name = 'dd'")!);
    }
}
