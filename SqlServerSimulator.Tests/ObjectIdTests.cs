using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>OBJECT_ID(name [, type])</c> scalar function. Returns the
/// table's stable per-database int id (assigned at CREATE, fresh on
/// DROP-then-recreate) or NULL when not found / wrong type / malformed name.
/// Probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class ObjectIdTests
{
    [TestMethod]
    public void ObjectId_ExistingTable_ReturnsInt()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as id
            """);
        IsTrue(reader.Read());
        AreEqual(typeof(int), reader.GetFieldType(0));
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_MissingTable_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select object_id('nope') as id");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_NullName_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select object_id(null) as id");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_NullType_ReturnsNull()
    {
        // Probe-confirmed: a NULL type filter propagates NULL even when the
        // table exists — real SQL Server treats it as "no match".
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', null) as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_UserType_Matches()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'U') as id
            """));

    [TestMethod]
    public void ObjectId_UserTypeLowercase_Matches()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'u') as id
            """));

    [TestMethod]
    public void ObjectId_TypeWithWhitespace_ReturnsNull()
    {
        // Probe-confirmed: real SQL Server is whitespace-sensitive on the
        // type filter; ' U ' returns NULL.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', ' U ') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_WrongType_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'V') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_InvalidType_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'XX') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_TwoPartName_Works()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create schema audit;
            create table audit.bar (id int);
            select object_id('audit.bar', 'U') as id
            """));

    [TestMethod]
    public void ObjectId_UnqualifiedResolvesToDbo()
        => AreEqual(new Simulation().ExecuteScalar("""
            create table dbo.foo (id int);
            select object_id('dbo.foo') as id
            """), new Simulation().ExecuteScalar("""
            create table dbo.foo (id int);
            select object_id('foo') as id
            """));

    [TestMethod]
    public void ObjectId_BracketedName_Works()
    {
        var bareId = (int)new Simulation().ExecuteScalar("""
            create table foo (id int);
            select object_id('foo') as id
            """)!;
        var bracketedId = (int)new Simulation().ExecuteScalar("""
            create table foo (id int);
            select object_id('[dbo].[foo]') as id
            """)!;
        // Same Simulation would be more direct but our infra creates two —
        // assert both are non-null ints. The cross-Simulation values differ.
        AreNotEqual(0, bareId);
        AreNotEqual(0, bracketedId);
    }

    [TestMethod]
    public void ObjectId_BracketsAndDots_SingleSimulation()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as a, object_id('[dbo].[foo]') as b, object_id('dbo.foo') as c
            """);
        IsTrue(reader.Read());
        var a = reader.GetInt32(0);
        AreEqual(a, reader.GetInt32(1));
        AreEqual(a, reader.GetInt32(2));
    }

    [TestMethod]
    public void ObjectId_ThreePartCorrectDb_Works()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('simulated.dbo.foo', 'U') as id
            """));

    [TestMethod]
    public void ObjectId_ThreePartWrongDb_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('baddb.dbo.foo', 'U') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_FourPartName_ReturnsNull()
    {
        // Linked-server names aren't modeled — real SQL Server also returns
        // NULL for a 4-part name pointing at a non-existent linked server.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('linked.simulated.dbo.foo', 'U') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_StableAcrossCalls()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as a, object_id('foo') as b
            """);
        IsTrue(reader.Read());
        AreEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void ObjectId_DistinctAcrossTables()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            create table bar (id int);
            select object_id('foo') as foo, object_id('bar') as bar
            """);
        IsTrue(reader.Read());
        AreNotEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void ObjectId_FreshAfterDropAndRecreate()
    {
        // Probe-confirmed against SQL Server 2025: drop-then-recreate yields
        // a fresh int id, the prior value is gone.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            declare @before int = object_id('foo');
            drop table foo;
            create table foo (id int);
            select @before as before_id, object_id('foo') as after_id
            """);
        IsTrue(reader.Read());
        AreNotEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void ObjectId_VariableArgument_RuntimeEvaluated()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            declare @n nvarchar(100) = 'foo';
            select object_id(@n) as id
            """);
        IsTrue(reader.Read());
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_AfterDrop_IsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            drop table foo;
            select object_id('foo') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_TempTable_Resolves()
    {
        // Divergence from real SQL Server (documented quirk): the simulator
        // routes # leaves to the connection's temp dict regardless of the
        // current db, so OBJECT_ID('#foo') finds the session's temp table
        // directly rather than requiring tempdb..#foo.
        using var reader = new Simulation().ExecuteReader("""
            create table #foo (id int);
            select object_id('#foo') as id
            """);
        IsTrue(reader.Read());
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_IfExistsIdiom_Works()
    {
        // The dominant real-world use of OBJECT_ID is the safe-drop pattern.
        // Confirm it threads end-to-end against an existing table.
        _ = new Simulation().ExecuteNonQuery("""
            create table foo (id int);
            if object_id('dbo.foo', 'U') is not null drop table foo;
            create table foo (id int)
            """);
    }

    [TestMethod]
    public void ObjectId_IfExistsIdiom_NoExistingTable()
        => _ = new Simulation().ExecuteNonQuery("""
            if object_id('dbo.foo', 'U') is not null drop table foo;
            create table foo (id int)
            """);

    [TestMethod]
    public void ObjectId_EmptyString_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select object_id('') as id");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_TempdbDotDotHash_Resolves()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table #foo (id int);
            select object_id('tempdb..#foo') as id
            """));

    [TestMethod]
    public void ObjectId_ResultTypeIsInt32()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as id
            """);
        IsTrue(reader.Read());
        AreEqual(typeof(int), reader.GetFieldType(0));
    }

    [TestMethod]
    public void ObjectId_TooManyArgs_RaisesMsg174()
        => new Simulation().AssertSqlError("select object_id('foo', 'U', 'extra')", 174);

    [TestMethod]
    public void ObjectId_Trigger_NoFilter_Resolves()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.trg1 on dbo.t after insert as select 1");
        IsFalseDbNull(sim.ExecuteReader("select object_id('dbo.trg1') as id"));
    }

    [TestMethod]
    public void ObjectId_Trigger_TrFilter_Resolves()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.trg1 on dbo.t after insert as select 1");
        IsFalseDbNull(sim.ExecuteReader("select object_id('dbo.trg1', 'TR') as id"));
    }

    private static void IsFalseDbNull(DbDataReader reader)
    {
        using (reader)
        {
            IsTrue(reader.Read());
            IsFalse(reader.IsDBNull(0));
        }
    }
}
