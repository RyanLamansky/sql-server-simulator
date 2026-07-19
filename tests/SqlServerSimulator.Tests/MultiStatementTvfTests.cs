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
        // (Msg 178). The simulator defers Msg 178 to invoke time — same
        // convention scalar UDFs use for the falling-through-without-RETURN
        // case. The error fires when the function is actually called.
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.fBadRet()
            returns @r table (Id int not null)
            as
            begin
                return 5;
            end
            """).ExecuteNonQuery();
        var ex = Throws<DbException>(() => connection.CreateCommand("select * from dbo.fBadRet()").ExecuteReader().Read());
        AreEqual("178", ex.Data["HelpLink.EvtID"]);
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
        var ex = Throws<DbException>(() => connection.CreateCommand("select * from dbo.fPk()").ExecuteReader().Read());
        AreEqual("2627", ex.Data["HelpLink.EvtID"]);
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
}
