using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>CREATE SYNONYM</c> / <c>DROP SYNONYM</c> and FROM-source
/// synonym resolution. Probe-confirmed against SQL Server 2025 (2026-07-21):
/// a name collision raises Msg 2714, a missing DROP target raises Msg 3701
/// (State 5). Catalog projection (sys.synonyms) and non-FROM synonym targets
/// are deferred — see <c>Synonym</c>.
/// </summary>
[TestClass]
public sealed class SynonymTests
{
    [TestMethod]
    public void Synonym_ResolvesToBaseTableInFrom()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10), (2, 20), (3, 30);
            create synonym syn for t;
            select count(*) from syn
            """));

    [TestMethod]
    public void Synonym_ProjectsBaseColumnsWithPredicate()
        => AreEqual(20, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10), (2, 20), (3, 30);
            create synonym syn for t;
            select a from syn where id = 2
            """));

    [TestMethod]
    public void Synonym_ResolvesToBaseViewInFrom()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10), (2, 20), (3, 30);
            """).ExecuteNonQuery();
        _ = conn.CreateCommand("create view vv as select * from t where a > 10").ExecuteNonQuery();
        _ = conn.CreateCommand("create synonym synv for vv").ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from synv").ExecuteScalar());
    }

    [TestMethod]
    public void Synonym_SchemaQualifiedBase_Resolves()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10), (2, 20), (3, 30);
            create synonym syn for dbo.t;
            select count(*) from syn
            """));

    [TestMethod]
    public void CreateSynonym_NameCollidesWithTable_RaisesMsg2714()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym t for t
            """, 2714, "There is already an object named 't' in the database.");

    [TestMethod]
    public void CreateSynonym_Duplicate_RaisesMsg2714()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym syn for t;
            create synonym syn for t
            """, 2714);

    [TestMethod]
    public void DropSynonym_RemovesResolution()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym syn for t;
            drop synonym syn;
            select * from syn
            """, 208);

    [TestMethod]
    public void DropSynonym_Missing_RaisesMsg3701()
    {
        var ex = new Simulation().AssertSqlError("drop synonym nope", 3701);
        Assert.Contains("Cannot drop the synonym 'nope'", ex.Message);
    }

    [TestMethod]
    public void DropSynonymIfExists_Missing_IsSilent()
        => AreEqual(1, new Simulation().ExecuteScalar("drop synonym if exists nope; select 1"));
}
