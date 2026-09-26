using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for multi-statement table-valued functions
/// (<c>RETURNS @r TABLE (cols) AS BEGIN ... END</c>): CREATE / DROP,
/// FROM-clause invocation, parameter binding, return-table accumulation
/// across multiple INSERTs, IF / early RETURN inside the body, nested
/// TVF calls, CROSS APPLY correlation, runtime constraint enforcement
/// (PRIMARY KEY / CHECK / IDENTITY on the return table), and the
/// catalog-view surface (<c>sys.objects.type = 'TF'</c>,
/// <c>OBJECT_ID('name', 'TF')</c>). Probe-confirmed against SQL Server
/// 2025 on 2026-05-13.
/// </summary>
[TestClass]
public sealed class MultiStatementTvfTests
{
    private static DbConnection Open() => new Simulation().CreateOpenConnection();

    [TestMethod]
    public void Create_And_Call_BasicMSTvf_AccumulatesFromBody()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fMS(@x int)
            returns @r table (Id int not null, Doubled int not null)
            as
            begin
                insert into @r values (@x, @x * 2);
                insert into @r values (@x + 1, (@x + 1) * 2);
                return;
            end
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select Id, Doubled from dbo.fMS(5) order by Id").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
        AreEqual(10, reader.GetInt32(1));
        IsTrue(reader.Read());
        AreEqual(6, reader.GetInt32(0));
        AreEqual(12, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Body_CanInsertFromExternalTable()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create table dbo.t1 (Id int not null primary key, OwnerId int not null, Label nvarchar(30) not null)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert dbo.t1 values (1, 100, 'apple'), (2, 100, 'banana'), (3, 200, 'cherry')").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create function dbo.fOwnerItems(@oid int)
            returns @r table (Id int not null, Label nvarchar(30) not null)
            as
            begin
                insert into @r select Id, Label from dbo.t1 where OwnerId = @oid;
                return;
            end
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select Id, Label from dbo.fOwnerItems(100) order by Id").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("apple", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual("banana", reader.GetString(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void EmptyBody_ReturnsZeroRows()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fEmpty(@x int)
            returns @r table (Id int not null)
            as
            begin
                return;
            end
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select * from dbo.fEmpty(0)").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void EarlyReturn_SkipsLaterInserts()
    {
        // Probed: bare RETURN exits the body before reaching later statements.
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fEarly(@cond int)
            returns @r table (Id int not null)
            as
            begin
                insert into @r values (1);
                if @cond = 0
                    return;
                insert into @r values (2);
                return;
            end
            """).ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("select count(*) from dbo.fEarly(0)").ExecuteScalar());
        AreEqual(2, connection.CreateCommand("select count(*) from dbo.fEarly(1)").ExecuteScalar());
    }

    [TestMethod]
    public void CrossApply_PerOuterRowCall()
    {
        // Probe: arguments evaluate in the outer row scope per call.
        using var connection = Open();
        _ = connection.CreateCommand("create table dbo.t1 (Id int not null primary key, OwnerId int not null, Label nvarchar(30) not null)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert dbo.t1 values (1, 100, 'apple'), (2, 100, 'banana'), (3, 200, 'cherry')").ExecuteNonQuery();
        _ = connection.CreateCommand("create table dbo.owners (oid int not null primary key)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert dbo.owners values (100), (200)").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create function dbo.fForOwner(@oid int)
            returns @r table (Id int not null, Label nvarchar(30) not null)
            as
            begin
                insert into @r select Id, Label from dbo.t1 where OwnerId = @oid;
                return;
            end
            """).ExecuteNonQuery();
        AreEqual(3, connection.CreateCommand(@"
            select count(*) from dbo.owners src
            cross apply dbo.fForOwner(src.oid) m").ExecuteScalar());
    }

    [TestMethod]
    public void NestedTvfCall_Works()
    {
        // Probed: MS-TVF body can SELECT from another TVF (inline or MS).
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fInner(@x int)
            returns @r table (V int not null)
            as
            begin
                insert into @r values (@x), (@x + 1);
                return;
            end
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create function dbo.fOuter(@x int)
            returns @r table (V int not null)
            as
            begin
                insert into @r select V from dbo.fInner(@x);
                return;
            end
            """).ExecuteNonQuery();
        AreEqual(2, connection.CreateCommand("select count(*) from dbo.fOuter(5)").ExecuteScalar());
    }

    [TestMethod]
    public void Body_ValueFormReturn_RaisesMsg178()
    {
        // Probed: real SQL Server rejects RETURN <expr> at CREATE time
        // (Msg 178) and leaves the function uncreated. The CREATE-time body
        // bind reaches it through the same frame-less batch the invocation
        // uses, so the message arrives at the CREATE.
        using var connection = Open();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            create function dbo.fBadRet()
            returns @r table (Id int not null)
            as
            begin
                return 5;
            end
            """).ExecuteNonQuery());
        AreEqual(178, ex.Number);
        AreEqual(0, connection.CreateCommand("select count(*) from sys.objects where name = 'fBadRet'").ExecuteScalar());
    }

    [TestMethod]
    public void ReturnTablePrimaryKey_EnforcedAtRuntime()
    {
        // Probed: PK violation on the return table surfaces an error at
        // call time. (Real SQL Server returns an empty result set in some
        // probe configurations, but the simulator's row-level uniqueness
        // check raises Msg 2627 here. Stricter than real SQL Server but
        // defensible — apps that hit this are buggy.)
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fPk()
            returns @r table (Id int not null primary key)
            as
            begin
                insert into @r values (1);
                insert into @r values (1);
                return;
            end
            """).ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("select * from dbo.fPk()").ExecuteReader().Read());
        AreEqual(2627, ex.Number);
    }

    [TestMethod]
    public void ReturnTableIdentity_AutoAssigned()
    {
        // Probed: identity column on the return table auto-assigns when the
        // INSERT column list omits it.
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fIdent()
            returns @r table (Id int identity(1,1), Label nvarchar(10) not null)
            as
            begin
                insert into @r (Label) values ('a'), ('b'), ('c');
                return;
            end
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select Id, Label from dbo.fIdent() order by Id").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("a", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual("b", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        AreEqual("c", reader.GetString(1));
    }

    [TestMethod]
    public void SysObjects_TypeIsTF()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fForCatalog()
            returns @r table (Id int not null)
            as
            begin
                return;
            end
            """).ExecuteNonQuery();
        AreEqual("TF", connection.CreateCommand("select type from sys.objects where name = 'fForCatalog'").ExecuteScalar());
        AreEqual("SQL_TABLE_VALUED_FUNCTION", connection.CreateCommand("select type_desc from sys.objects where name = 'fForCatalog'").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_TypeFilter_TF()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fForObjectId()
            returns @r table (Id int not null)
            as
            begin
                return;
            end
            """).ExecuteNonQuery();
        // 'TF' filter resolves; 'FN' / 'IF' filter returns NULL.
        IsNotNull(connection.CreateCommand("select object_id('dbo.fForObjectId', 'TF')").ExecuteScalar());
        IsTrue(connection.CreateCommand("select object_id('dbo.fForObjectId', 'IF')").ExecuteScalar() is DBNull);
        IsTrue(connection.CreateCommand("select object_id('dbo.fForObjectId', 'FN')").ExecuteScalar() is DBNull);
    }

    [TestMethod]
    public void DropFunction_RemovesMSTvf()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fDrop()
            returns @r table (Id int not null)
            as
            begin
                return;
            end
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("drop function dbo.fDrop").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("select count(*) from sys.objects where name = 'fDrop'").ExecuteScalar());
    }

    /// <summary>
    /// The return table's columns are catalogued like a table's — in
    /// sys.columns, and their identity, computed column and defaults in their
    /// own views, the default named after the function and a computed column
    /// reading no definition (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ReturnTableColumns_AreCatalogued()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create function dbo.f (@p int) returns @r table (id int identity(5, 2), x int not null default (5), z as x + 1) as begin return end");
        AreEqual("id:1:1:0,x:2:0:1,z:3:0:0", simulation.ExecuteScalar("""
            select string_agg(concat(name, ':', column_id, ':', cast(is_identity as int), ':', sign(default_object_id)), ',') within group (order by column_id)
            from sys.columns where object_id = object_id('dbo.f')
            """));
        AreEqual("id:5:2", simulation.ExecuteScalar("select concat(name, ':', cast(seed_value as int), ':', cast(increment_value as int)) from sys.identity_columns where object_id = object_id('dbo.f')"));
        AreEqual("z:-", simulation.ExecuteScalar("select concat(name, ':', isnull(definition, '-')) from sys.computed_columns where object_id = object_id('dbo.f')"));
        AreEqual("DF__f__x:((5))", simulation.ExecuteScalar("select concat(left(name, 8), ':', definition) from sys.default_constraints where parent_object_id = object_id('dbo.f')"));
    }

    /// <summary>
    /// The return table's constraints and their indexes are listed under the
    /// function's id, as a table's are (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    public void ReturnTableConstraints_AreListedUnderTheFunction()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create function dbo.f() returns @t table (a int primary key, b int unique, c int check (c > 0), d int default 5) as begin return end");
        AreEqual("CK__,DF__,PK__,UQ__", simulation.ExecuteScalar(
            "select string_agg(left(name, 4), ',') within group (order by name) from sys.objects where parent_object_id = object_id('dbo.f')"));
        AreEqual("PK:1,UQ:2", simulation.ExecuteScalar(
            "select string_agg(concat(type, ':', unique_index_id), ',') within group (order by type) from sys.key_constraints where parent_object_id = object_id('dbo.f')"));
        AreEqual("([c]>(0))", simulation.ExecuteScalar("select definition from sys.check_constraints where parent_object_id = object_id('dbo.f')"));
        AreEqual("1:CLUSTERED:1,2:NONCLUSTERED:2", simulation.ExecuteScalar("""
            select string_agg(concat(i.index_id, ':', i.type_desc, ':', c.column_id), ',') within group (order by i.index_id)
            from sys.indexes i join sys.index_columns c on c.object_id = i.object_id and c.index_id = i.index_id
            where i.object_id = object_id('dbo.f')
            """));
    }

    [TestMethod]
    public void ReturnTableWithoutAClusteredKey_IsAHeap()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create function dbo.f() returns @t table (a int unique, b int primary key nonclustered) as begin return end");
        AreEqual("0:HEAP,2:NONCLUSTERED,3:NONCLUSTERED", simulation.ExecuteScalar(
            "select string_agg(concat(index_id, ':', type_desc), ',') within group (order by index_id) from sys.indexes where object_id = object_id('dbo.f')"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where object_id = object_id('dbo.f')"));
    }

    /// <summary>
    /// A called function's body naming a missing object fails the calling
    /// statement, not its batch, for a scalar function and a multi-statement
    /// TVF alike (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create function dbo.f() returns int as begin return (select count(*) from dbo.nope) end", "declare @x int = dbo.f()")]
    [DataRow("create function dbo.f() returns int as begin return (select count(*) from dbo.nope) end", "insert t select dbo.f()")]
    [DataRow("create function dbo.f() returns @r table (a int) as begin insert @r select count(*) from dbo.nope; return end", "insert t select a from dbo.f()")]
    public void AFunctionBodysMissingObject_EndsOnlyTheCallingStatement(string function, string call)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int)", function);
        _ = sim.AssertSqlError(call + "; insert t values (7)", 208);
        AreEqual(7, sim.ExecuteScalar("select a from t"));
    }
}
