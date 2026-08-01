using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Rejections the simulator previously skipped, each closing a case where a
/// statement passed here and would have failed on a real server. Every
/// expectation is the message SQL Server 2025 produced for the same statement.
/// </summary>
/// <remarks>
/// Grouped by the surface they guard rather than split across the feature
/// suites, because what they have in common is the direction of the fix — see
/// the over-permissive register in <c>docs/claude/backlog.md</c>.
/// </remarks>
[TestClass]
public sealed class OverPermissiveClosureTests
{
    /// <summary>
    /// A <c>bit</c> paired with a <c>bit</c> has no arithmetic on real. The
    /// error splits by operator: multiply / divide report the operand type,
    /// the rest report the incompatible pair.
    /// </summary>
    [TestMethod]
    [DataRow("+", 402, "The data types bit and bit are incompatible in the add operator.")]
    [DataRow("-", 402, "The data types bit and bit are incompatible in the subtract operator.")]
    [DataRow("%", 402, "The data types bit and bit are incompatible in the modulo operator.")]
    [DataRow("*", 8117, "Operand data type bit is invalid for multiply operator.")]
    [DataRow("/", 8117, "Operand data type bit is invalid for divide operator.")]
    public void BitPair_Arithmetic_IsRejected(string op, int errorNumber, string message) =>
        new Simulation().AssertSqlError($"select cast(1 as bit) {op} cast(1 as bit)", errorNumber, message);

    /// <summary>
    /// The bitwise operators do accept a bit pair, and a mixed bit/int pair
    /// promotes and computes — only the same-type arithmetic pair is refused.
    /// </summary>
    [TestMethod]
    public void BitPair_BitwiseAndMixedArithmetic_StillCompute()
    {
        var sim = new Simulation();
        IsTrue(sim.ExecuteScalar<bool>("select cast(1 as bit) & cast(1 as bit)"));
        IsTrue(sim.ExecuteScalar<bool>("select cast(1 as bit) | cast(0 as bit)"));
        IsFalse(sim.ExecuteScalar<bool>("select cast(1 as bit) ^ cast(1 as bit)"));
        AreEqual(2, sim.ExecuteScalar<int>("select cast(1 as bit) + 1"));
    }

    /// <summary>
    /// Only <c>SELECT</c> / <c>UPDATE</c> / <c>REFERENCES</c> have a
    /// column-level form; every other permission is entity-level and rejects a
    /// column list outright.
    /// </summary>
    [TestMethod]
    [DataRow("select")]
    [DataRow("update")]
    [DataRow("references")]
    public void GrantColumnList_OnColumnCapablePermission_IsAccepted(string permission)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int, b int)", "create user u without login");
        _ = sim.ExecuteNonQuery($"grant {permission} (a) on t to u");
    }

    /// <summary>
    /// The entity-level permissions reject it with Msg 1020 — including
    /// <c>EXECUTE</c>, where the column list is refused before the
    /// permission-vs-object-kind check that would otherwise report Msg 4606.
    /// </summary>
    [TestMethod]
    [DataRow("insert")]
    [DataRow("delete")]
    [DataRow("execute")]
    [DataRow("alter")]
    [DataRow("control")]
    public void GrantColumnList_OnEntityLevelPermission_RaisesMsg1020(string permission)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int, b int)", "create user u without login");
        sim.AssertSqlError(
            $"grant {permission} (a) on t to u",
            1020,
            "Sub-entity lists (such as column or security expressions) cannot be specified for entity-level permissions.");
    }

    /// <summary>
    /// The rejection is positional, not object-dependent: it fires before the
    /// securable resolves, so a column list on EXECUTE is refused even when the
    /// securable is a procedure (where EXECUTE is otherwise valid), and equally
    /// in the securable-placed <c>ON t (col)</c> spelling.
    /// </summary>
    [TestMethod]
    public void GrantColumnList_RejectionPrecedesSecurableResolution()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int, b int)",
            "create procedure pr as select 1",
            "create user u without login");

        _ = sim.AssertSqlError("grant execute (a) on pr to u", 1020);
        _ = sim.AssertSqlError("grant execute on t (a) to u", 1020);
        // With no column list involved the kind check still reports Msg 4606.
        _ = sim.AssertSqlError("grant select on pr to u", 4606);
        // And EXECUTE on a procedure remains valid.
        _ = sim.ExecuteNonQuery("grant execute on pr to u");
    }

    /// <summary>
    /// A positional <c>OUTPUT … INTO</c> fills the target's <em>non-identity</em>
    /// columns, so the projection is measured against that narrower count.
    /// Matching it succeeds — this is the case the simulator used to reject.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void OutputInto_PositionalMatchingNonIdentityCount_Succeeds(int plainColumns)
    {
        var sim = new Simulation();
        var columns = string.Join(", ", Enumerable.Range(1, plainColumns).Select(i => $"c{i} int"));
        var projection = string.Join(", ", new[] { "inserted.x", "inserted.y", "inserted.z" }.Take(plainColumns));
        sim.ExecuteBatches(
            "create table s (x int, y int, z int)",
            $"create table dest (identcol int identity, {columns})");

        _ = sim.ExecuteNonQuery($"insert into s (x, y, z) output {projection} into dest values (1, 2, 3)");
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from dest"));
        // The identity column generated its own value rather than being filled.
        AreEqual(1, sim.ExecuteScalar<int>("select identcol from dest"));
    }

    /// <summary>
    /// A wider projection would have to write the identity column, which is
    /// Msg 8101 — and that message names the OUTPUT target, schema-qualified.
    /// </summary>
    [TestMethod]
    public void OutputInto_ProjectionWiderThanNonIdentityColumns_RaisesMsg8101()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table s (x int, y int, z int)",
            "create table dest (identcol int identity, c1 int)");

        sim.AssertSqlError(
            "insert into s (x, y, z) output inserted.x, inserted.y into dest values (1, 2, 3)",
            8101,
            "An explicit value for the identity column in table 'dbo.dest' can only be specified when a column list is used and IDENTITY_INSERT is ON.");
    }

    /// <summary>
    /// A narrower projection stays the pre-existing arity error.
    /// </summary>
    [TestMethod]
    public void OutputInto_ProjectionNarrowerThanNonIdentityColumns_RaisesMsg213()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table s (x int, y int, z int)",
            "create table dest (identcol int identity, c1 int, c2 int)");

        _ = sim.AssertSqlError("insert into s (x, y, z) output inserted.x into dest values (1, 2, 3)", 213);
    }

    /// <summary>
    /// An explicit column list may not name the target's identity column.
    /// Real's Msg 544 names the <em>DML statement's</em> target table rather
    /// than the OUTPUT target that actually owns the identity column — a quirk
    /// mirrored verbatim, which is why the expected name here is the source.
    /// </summary>
    [TestMethod]
    public void OutputInto_ColumnListNamingIdentity_RaisesMsg544NamingTheSourceTable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table sourcetbl (x int, y int)",
            "create table outputtgt (identcol int identity, y int)");

        sim.AssertSqlError(
            "insert into sourcetbl (x, y) output inserted.x, inserted.y into outputtgt (identcol, y) values (1, 2)",
            544,
            "Cannot insert explicit value for identity column in table 'sourcetbl' when IDENTITY_INSERT is set to OFF.");
    }

    /// <summary>
    /// A column list that excludes the identity column is the one accepted
    /// form, and <c>SET IDENTITY_INSERT</c> on the target does not unlock the
    /// rejected one.
    /// </summary>
    [TestMethod]
    public void OutputInto_ColumnListExcludingIdentity_SucceedsAndIdentityInsertDoesNotUnlock()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table sourcetbl (x int, y int)",
            "create table outputtgt (identcol int identity, y int)");

        _ = sim.ExecuteNonQuery("insert into sourcetbl (x, y) output inserted.y into outputtgt (y) values (1, 2)");
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from outputtgt"));

        _ = sim.ExecuteNonQuery("set identity_insert outputtgt on");
        _ = sim.AssertSqlError(
            "insert into sourcetbl (x, y) output inserted.x, inserted.y into outputtgt (identcol, y) values (3, 4)",
            544);
    }

    /// <summary>
    /// <c>FORCESEEK</c>'s nested form names an index, and an unknown name is
    /// rejected exactly as <c>INDEX(name)</c> is.
    /// </summary>
    [TestMethod]
    public void ForceSeek_NestedFormWithUnknownIndex_RaisesMsg308()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table fs (a int)");
        sim.AssertSqlError(
            "select * from fs with (forceseek (nosuchindex (a)))",
            308,
            "Index 'nosuchindex' on table 'dbo.fs' (specified in the FROM clause) does not exist.");
    }

    /// <summary>
    /// The valid nested form, the bare hints, and the sibling
    /// <c>INDEX(name)</c> hint all keep working.
    /// </summary>
    [TestMethod]
    [DataRow("forceseek (ix_ok (a))")]
    [DataRow("forceseek")]
    [DataRow("forcescan")]
    [DataRow("index(ix_ok)")]
    public void ForceSeek_ValidShapes_StillParse(string hint)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table fs (a int)", "create index ix_ok on fs (a)", "insert fs values (1)");
        AreEqual(1, sim.ExecuteScalar<int>($"select count(*) from fs with ({hint})"));
    }

    /// <summary>
    /// <c>OBJECT_ID</c> resolves an unqualified scalar-function name against
    /// the default schema, which is what makes the common
    /// <c>OBJECTPROPERTY(OBJECT_ID('f'), …)</c> idiom work.
    /// </summary>
    [TestMethod]
    public void ObjectId_UnqualifiedScalarFunction_Resolves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create function f() returns int as begin return 1 end");

        var unqualified = sim.ExecuteScalar<int>("select object_id('f')");
        AreEqual(sim.ExecuteScalar<int>("select object_id('dbo.f')"), unqualified);
        // 0 because `f` isn't schema-bound, which real makes a precondition
        // for determinism — the point here is that the id resolved at all.
        AreEqual(0, sim.ExecuteScalar<int>("select objectproperty(object_id('f'), 'IsDeterministic')"));
        // A genuinely absent name still yields NULL.
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('nosuchthing')"));
    }
    /// <summary>
    /// The OUTPUT projection's type comes from the source table and need not
    /// match the destination column's, so each value coerces on the way in.
    /// An ORM writing <c>SELECT TOP 0 CAST(id AS bigint) … INTO #tmp</c> then
    /// <c>OUTPUT INSERTED.id INTO #tmp</c> hands an int to a bigint column;
    /// storing it raw reached the row encoder's type check as a bare
    /// ArgumentException, which over the TDS wire aborted the response
    /// mid-stream and the client reported a severe protocol error.
    /// </summary>
    [TestMethod]
    [DataRow("bigint", "1")]
    [DataRow("decimal(18,2)", "1.00")]
    [DataRow("varchar(20)", "1")]
    public void OutputInto_CoercesToDestinationColumnType(string destinationType, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table src (id int identity primary key, v int)",
            $"create table dest (id {destinationType})");

        _ = sim.ExecuteNonQuery("insert into src (v) output inserted.id into dest values (7)");
        AreEqual(expected, (string)sim.ExecuteScalar("select cast(id as varchar(20)) from dest")!);
    }

    /// <summary>
    /// The same coercion applies to a <c>#temp</c> target shaped by
    /// <c>SELECT TOP 0 … INTO</c>, which is how ORMs build a returning buffer.
    /// </summary>
    [TestMethod]
    public void OutputInto_TempTargetShapedBySelectInto_Coerces()
    {
        var sim = new Simulation();
        // One connection throughout: #buf is session-scoped.
        using var connection = sim.CreateOpenConnection();
        foreach (var statement in new[]
        {
            "create table src (id int identity primary key, created datetime2 not null)",
            "select top 0 cast(id as bigint) as id, created into #buf from src",
            "insert into src (created) output inserted.id, inserted.created into #buf values ('2020-01-01')",
        })
        {
            using var command = connection.CreateCommand(statement);
            _ = command.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand("select id from #buf");
        AreEqual(1L, read.ExecuteScalar());
    }
}
