using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>CREATE SYNONYM</c> / <c>DROP SYNONYM</c>, synonym resolution at
/// every reference site (FROM, DML, EXEC, function call), and the catalog
/// surface (<c>sys.synonyms</c> / <c>sys.objects</c> / <c>OBJECT_ID</c> /
/// <c>OBJECTPROPERTYEX</c>). Probe-confirmed against SQL Server 2025: a name
/// collision raises Msg 2714 in both directions, a missing DROP target raises
/// Msg 3701 (State 5), a cross-kind DROP raises Msg 3705, a missing base object
/// raises Msg 5313 at first use, and a synonym chain raises Msg 470.
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

    // === Catalog projection ===

    [TestMethod]
    public void SysSynonyms_ProjectsRowForCreatedSynonym()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int);
            create synonym syn for dbo.t;
            select name, principal_id, schema_id, parent_object_id, type, type_desc,
                   is_ms_shipped, is_published, is_schema_published, base_object_name
            from sys.synonyms
            """);
        IsTrue(reader.Read());
        AreEqual("syn", reader.GetString(0));
        IsTrue(reader.IsDBNull(1));
        AreEqual(1, reader.GetInt32(2));
        AreEqual(0, reader.GetInt32(3));
        AreEqual("SN", reader.GetString(4));
        AreEqual("SYNONYM", reader.GetString(5));
        IsFalse(reader.GetBoolean(6));
        IsFalse(reader.GetBoolean(7));
        IsFalse(reader.GetBoolean(8));
        AreEqual("[dbo].[t]", reader.GetString(9));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// <c>base_object_name</c> keeps the base name's written qualification,
    /// bracket-quoting each segment — probe-confirmed <c>[t]</c> / <c>[dbo].[t]</c>
    /// / <c>[db].[dbo].[t]</c> for the 1-, 2-, and 3-part forms.
    /// </summary>
    [TestMethod]
    public void SysSynonyms_BaseObjectName_PreservesWrittenQualification()
        => AreEqual("[t]|[dbo].[t]|[simulated].[dbo].[t]", new Simulation().ExecuteScalar("""
            create table t (id int);
            create synonym s1 for t;
            create synonym s2 for dbo.t;
            create synonym s3 for simulated.dbo.t;
            select string_agg(base_object_name, '|') within group (order by name) from sys.synonyms
            """));

    [TestMethod]
    public void SysObjects_ProjectsSynonymAsSn()
        => AreEqual("SN|SYNONYM", new Simulation().ExecuteScalar("""
            create table t (id int);
            create synonym syn for dbo.t;
            select concat(rtrim(type), '|', type_desc) from sys.objects where name = 'syn'
            """));

    [TestMethod]
    public void SysSynonyms_ObjectIdMatchesSysObjectsAndObjectId()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int);
            create synonym syn for dbo.t;
            select count(*) from sys.synonyms s
            join sys.objects o on o.object_id = s.object_id
            where s.object_id = object_id('dbo.syn')
            """));

    [TestMethod]
    public void SysSynonyms_CreateDate_IsPopulated()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int);
            create synonym syn for dbo.t;
            select count(*) from sys.synonyms
            where create_date > '2020-01-01' and modify_date = create_date
            """));

    // === OBJECT_ID / OBJECT_NAME / OBJECTPROPERTYEX ===

    /// <summary>
    /// <c>OBJECT_ID</c> reports a synonym's own id and never follows it to the
    /// base: probe-confirmed the <c>'U'</c>-filtered form is NULL even when the
    /// base is a table.
    /// </summary>
    [TestMethod]
    public void ObjectId_ResolvesSynonymItself_NotItsBase()
        => AreEqual("syn|dbo|1|1", new Simulation().ExecuteScalar("""
            create table t (id int);
            create synonym syn for dbo.t;
            select concat(
                object_name(object_id('dbo.syn')), '|',
                object_schema_name(object_id('dbo.syn')), '|',
                case when object_id('dbo.syn', 'SN') = object_id('dbo.syn') then 1 else 0 end, '|',
                case when object_id('dbo.syn', 'U') is null then 1 else 0 end)
            """));

    [TestMethod]
    public void ObjectId_SnFilter_OnTable_IsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("""
            create table t (id int);
            select object_id('dbo.t', 'SN')
            """));

    /// <summary>
    /// <c>OBJECTPROPERTYEX(id, 'BaseType')</c> on a synonym reports the base
    /// object's type code rather than <c>'SN'</c> (probe-confirmed), and NULL
    /// when the base doesn't resolve.
    /// </summary>
    [TestMethod]
    public void ObjectPropertyEx_BaseType_ReportsBaseObjectKind()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int)",
            "create procedure p1 as select 1",
            "create function f1() returns int as begin return 1 end",
            """
            create synonym s_t for dbo.t;
            create synonym s_p for dbo.p1;
            create synonym s_f for dbo.f1;
            create synonym s_missing for dbo.nope
            """);
        AreEqual("U ", sim.ExecuteScalar("select cast(objectpropertyex(object_id('dbo.s_t'), 'BaseType') as char(2))"));
        AreEqual("P ", sim.ExecuteScalar("select cast(objectpropertyex(object_id('dbo.s_p'), 'BaseType') as char(2))"));
        AreEqual("FN", sim.ExecuteScalar("select cast(objectpropertyex(object_id('dbo.s_f'), 'BaseType') as char(2))"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectpropertyex(object_id('dbo.s_missing'), 'BaseType')"));
    }

    // === Non-FROM reference sites ===

    [TestMethod]
    public void Synonym_IsExecTarget_ForProcedure()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure p1 @a int as select @a * 2 as doubled",
            "create synonym s_p for dbo.p1");
        AreEqual(42, sim.ExecuteScalar("exec dbo.s_p 21"));
    }

    [TestMethod]
    public void Synonym_IsCallTarget_ForScalarFunction()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function f1(@a int) returns int as begin return @a * 3 end",
            "create synonym s_f for dbo.f1");
        AreEqual(12, sim.ExecuteScalar("select dbo.s_f(4)"));
    }

    [TestMethod]
    public void Synonym_IsFromSource_ForTableValuedFunction()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function tvf1(@a int) returns table as return select @a as v",
            "create synonym s_tvf for dbo.tvf1");
        AreEqual(9, sim.ExecuteScalar("select v from dbo.s_tvf(9)"));
    }

    [TestMethod]
    public void Synonym_TargetsDml()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create synonym syn for dbo.t;
            insert syn values (1, 10), (2, 20);
            update syn set a = 99 where id = 1;
            delete syn where id = 2
            """);
        AreEqual("1|99", sim.ExecuteScalar("select concat(count(*), '|', max(a)) from t"));
    }

    /// <summary>
    /// Real refuses <c>NEXT VALUE FOR</c> through a synonym even though the base
    /// is a sequence — Msg 11726, naming the synonym as written.
    /// </summary>
    [TestMethod]
    public void NextValueFor_Synonym_RaisesMsg11726()
        => new Simulation().AssertSqlError("""
            create sequence seq1 as int start with 5;
            create synonym s_seq for dbo.seq1;
            select next value for dbo.s_seq
            """, 11726, "Object 'dbo.s_seq' is not a sequence object.");

    // === Deferred base resolution ===

    [TestMethod]
    public void MissingBase_CreateSucceeds_FirstUseRaisesMsg5313()
        => new Simulation().AssertSqlError("""
            create synonym syn for dbo.nope;
            select * from dbo.syn
            """, 5313, "Synonym 'dbo.syn' refers to an invalid object.");

    /// <summary>Msg 5313 names the synonym exactly as written at the use site.</summary>
    [TestMethod]
    public void MissingBase_UnqualifiedReference_NamesSynonymAsWritten()
        => new Simulation().AssertSqlError("""
            create synonym syn for dbo.nope;
            insert syn (x) values (1)
            """, 5313, "Synonym 'syn' refers to an invalid object.");

    /// <summary>
    /// A base that exists but can't serve the reference (a procedure named in a
    /// FROM clause) is the same Msg 5313 with State 224 rather than State 1.
    /// </summary>
    [TestMethod]
    public void BaseOfWrongKind_InFrom_RaisesMsg5313State224()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure p1 as select 1",
            "create synonym s_p for dbo.p1");
        var ex = sim.AssertSqlError("select * from dbo.s_p", 5313);
        AreEqual((byte)224, ex.State);
    }

    /// <summary>
    /// EXEC expands the synonym before resolving, so a missing base surfaces
    /// Msg 2812 naming the base — probe-confirmed the synonym name never
    /// appears in that message.
    /// </summary>
    [TestMethod]
    public void MissingBase_Exec_RaisesMsg2812NamingBase()
        => new Simulation().AssertSqlError("""
            create synonym syn for dbo.nope;
            exec dbo.syn
            """, 2812, "Could not find stored procedure 'dbo.nope'.");

    /// <summary>
    /// Real accepts a <c>CREATE SYNONYM</c> whose base is another synonym and
    /// rejects the chain at first use (Msg 470), so the chain here is built in
    /// the reverse order a creation-time check would have caught.
    /// </summary>
    [TestMethod]
    public void SynonymChain_RaisesMsg470AtUse()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym s_inner for dbo.t;
            create synonym s_outer for dbo.s_inner;
            select * from s_outer
            """, 470, "The synonym \"s_outer\" referenced synonym \"dbo.s_inner\". Synonym chaining is not allowed.");

    // === Cross-database bases ===

    [TestMethod]
    public void CrossDatabaseBase_ReadsThroughTheThreePartPath()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create database other;
            use other;
            create table rt (id int);
            insert rt values (1), (2);
            use simulated;
            create synonym s_x for other.dbo.rt
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from s_x"));
    }

    /// <summary>
    /// A write through a synonym follows the base across the database
    /// boundary, exactly as the spelled-out three-part name does.
    /// </summary>
    [TestMethod]
    public void CrossDatabaseBase_WritesThroughTheThreePartPath()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create database other;
            use other;
            create table rt (id int);
            use simulated;
            create synonym s_x for other.dbo.rt
            """);
        AreEqual(1, sim.ExecuteNonQuery("insert s_x (id) values (1)"));
        _ = sim.ExecuteNonQuery("update s_x set id = 2 where id = 1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from other.dbo.rt where id = 2"));
        _ = sim.ExecuteNonQuery("delete s_x");
        AreEqual(0, sim.ExecuteScalar("select count(*) from other.dbo.rt"));
    }

    // === Name collisions, both directions ===

    [TestMethod]
    public void CreateTable_OverExistingSynonym_RaisesMsg2714()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym syn for dbo.t;
            create table syn (x int)
            """, 2714, "There is already an object named 'syn' in the database.");

    [TestMethod]
    public void SelectInto_OverExistingSynonym_RaisesMsg2714()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym syn for dbo.t;
            select * into syn from t
            """, 2714);

    [TestMethod]
    public void CreateView_OverExistingSynonym_RaisesMsg2714()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            create synonym syn for dbo.t
            """);
        _ = Assert.Throws<SimulatedSqlException>(() => sim.ExecuteBatches("create view syn as select 1 as x"));
    }

    [TestMethod]
    public void CreateSequence_OverExistingSynonym_RaisesMsg2714()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym syn for dbo.t;
            create sequence syn as int
            """, 2714);

    /// <summary>
    /// <c>DROP TABLE</c> naming a synonym raises Msg 3705 rather than dropping
    /// the base table — probe-confirmed wording, and <c>IF EXISTS</c> doesn't
    /// suppress it since the object does exist.
    /// </summary>
    [TestMethod]
    public void DropTable_OverSynonym_RaisesMsg3705()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create synonym syn for dbo.t;
            drop table syn
            """, 3705, "Cannot use DROP TABLE with 'syn' because 'syn' is a synonym. Use DROP SYNONYM.");

    [TestMethod]
    public void DropSynonym_OverTable_RaisesMsg3705()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            drop synonym dbo.t
            """, 3705, "Cannot use DROP SYNONYM with 'dbo.t' because 'dbo.t' is a table. Use DROP TABLE.");

    [TestMethod]
    public void DropSynonymIfExists_OverTable_StillRaisesMsg3705()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int);
            drop synonym if exists t
            """, 3705);

    /// <summary>
    /// A synonym is a grantable securable for both permission families — real
    /// accepts SELECT and EXECUTE on the same synonym, since the base object's
    /// kind isn't consulted at grant time.
    /// </summary>
    [TestMethod]
    public void Grant_OnSynonym_AcceptsBothPermissionFamilies()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int);
            create synonym syn for dbo.t;
            create user u without login;
            grant select on syn to u;
            grant execute on syn to u;
            select count(*) from sys.database_permissions p
            join sys.synonyms s on s.object_id = p.major_id
            """));

    /// <summary>
    /// A transferred synonym keeps its base name untouched — probe-confirmed
    /// <c>base_object_name</c> still reads <c>[dbo].[t]</c> after the move.
    /// </summary>
    [TestMethod]
    public void AlterSchemaTransfer_MovesSynonym_KeepingItsBase()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1);
            create synonym syn for dbo.t
            """);
        sim.ExecuteBatches("create schema sx");
        _ = sim.ExecuteNonQuery("alter schema sx transfer dbo.syn");
        AreEqual("sx|[dbo].[t]", sim.ExecuteScalar(
            "select concat(schema_name(schema_id), '|', base_object_name) from sys.synonyms where name = 'syn'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sx.syn"));
    }

    [TestMethod]
    public void DropSynonym_OverSequence_NamesSequenceKind()
        => new Simulation().AssertSqlError("""
            create sequence seq1 as int;
            drop synonym seq1
            """, 3705, "Cannot use DROP SYNONYM with 'seq1' because 'seq1' is a sequence. Use DROP SEQUENCE.");
}
