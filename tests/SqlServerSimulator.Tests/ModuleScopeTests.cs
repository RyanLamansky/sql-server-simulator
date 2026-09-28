using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a procedure, trigger, function or dynamic-SQL body scopes for its
/// caller: <c>SCOPE_IDENTITY()</c> against <c>@@IDENTITY</c>, the
/// <c>@@ROWCOUNT</c> a <c>RETURN</c> leaves, and <c>@@PROCID</c>. Probed
/// 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ModuleScopeTests
{
    private static Simulation WithIdentityTables(params ReadOnlySpan<string> modules)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(10, 1), a int); create table lg (id int identity(100, 1), a int); create table pl (a int)");
        simulation.ExecuteBatches(modules);
        return simulation;
    }

    [TestMethod]
    public void Procedure_ScopeIdentity_ReadsNullOnEntryAndTheCallersAfter()
    {
        var simulation = WithIdentityTables("create procedure p as begin select concat(scope_identity(), '|', @@identity); insert t (a) values (1); select scope_identity(); end");
        using var reader = simulation.ExecuteReader("insert t (a) values (0); exec p; select concat(scope_identity(), '|', @@identity)");
        IsTrue(reader.Read());
        AreEqual("|10", reader.GetString(0));
        IsTrue(reader.NextResult() && reader.Read());
        AreEqual(11m, reader.GetDecimal(0));
        IsTrue(reader.NextResult() && reader.Read());
        AreEqual("10|11", reader.GetString(0));
    }

    [TestMethod]
    [DataRow("insert lg (a) select a from inserted", "10|100")]
    [DataRow("insert pl (a) select a from inserted", "10|10")]
    public void Trigger_IdentityReadsItsInsertOnlyWhenItProducedOne(string body, string expected)
        => AreEqual(expected, WithIdentityTables($"create trigger tr on t after insert as {body}")
            .ExecuteScalar("insert t (a) values (1); select concat(scope_identity(), '|', @@identity)"));

    [TestMethod]
    public void ProcedureInsertWithoutIdentity_ClearsIdentityButNotTheCallersScope()
        => AreEqual("10|", WithIdentityTables("create procedure p as insert pl values (1)")
            .ExecuteScalar("insert t (a) values (0); exec p; select concat(scope_identity(), '|', @@identity)"));

    [TestMethod]
    public void DynamicSql_IsAScopeOfItsOwn()
        => AreEqual("10|11", WithIdentityTables()
            .ExecuteScalar("insert t (a) values (0); exec ('insert t (a) values (1)'); select concat(scope_identity(), '|', @@identity)"));

    [TestMethod]
    public void FunctionInsert_TouchesNeitherIdentity()
        => AreEqual("10|10", WithIdentityTables("create function mtf() returns @r table (id int identity(50, 1), a int) as begin insert @r (a) values (1); return end")
            .ExecuteScalar("insert t (a) values (0); declare @n int = (select count(*) from dbo.mtf()); select concat(scope_identity(), '|', @@identity)"));

    [TestMethod]
    [DataRow("return", 0)]
    [DataRow("return 0", 1)]
    [DataRow("return 7", 1)]
    [DataRow("return @x", 1)]
    [DataRow("if 1 = 1 return 5", 1)]
    [DataRow("if 1 = 1 return", 0)]
    [DataRow("return (select count(*) from t)", 1)]
    [DataRow("", 3)]
    public void RowCountAfterProcedure_FollowsItsLastStatement(string ending, int expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int); insert t values (1), (2), (3)");
        simulation.ExecuteBatches($"create procedure p as begin declare @x int = 3; update t set a = a; {ending} end");
        AreEqual(expected, simulation.ExecuteScalar("exec p; select @@rowcount"));
    }

    [TestMethod]
    public void ProcId_NamesEachModuleKind()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int); create table lg (n sysname null)",
            "create function f() returns sysname as begin return object_name(@@procid) end",
            "create function mtf() returns @r table (n sysname null) as begin insert @r values (object_name(@@procid)); return end",
            "create function itf() returns table as return select object_name(@@procid) n",
            "create trigger tr on t after insert as insert lg values (object_name(@@procid))",
            "create procedure p as select object_name(@@procid)");
        AreEqual("p", simulation.ExecuteScalar("exec p"));
        AreEqual("f|mtf||tr", simulation.ExecuteScalar("insert t values (1); select concat(dbo.f(), '|', (select n from dbo.mtf()), '|', (select n from dbo.itf()), '|', (select n from lg))"));
    }
}
