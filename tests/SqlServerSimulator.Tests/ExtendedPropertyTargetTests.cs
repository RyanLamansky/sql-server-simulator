using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The extended-property procedures' full target grammar — every level-1 and
/// level-2 kind real accepts, the class each lands in, the state each refusal
/// carries, where the procedures attribute their errors, how far those errors
/// reach, and how a property follows its target through ALTER and DROP.
/// Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ExtendedPropertyTargetTests
{
    private const string Setup = """
        create table t (a int constraint ck check (a > 0), b int); create index ix on t (b);
        create type tt as table (k int); create type at from int; create sequence sq; create synonym sy for t;
        create xml schema collection xc as '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="a" type="xsd:int"/></xsd:schema>';
        create user u without login;
        """;

    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            Setup,
            "create view v as select a, b from t",
            "create trigger tr on t after insert as select 1",
            "create trigger vtr on v instead of insert as select 1",
            "create procedure p @x int, @y int as select 1",
            "create function itf(@k int) returns table as return select a from t",
            "create function sf(@k int) returns int as begin return 1 end",
            "create rule r1 as @v > 0",
            "create default d1 as 0");
        return sim;
    }

    private static string Add(string levels) => $"exec sp_addextendedproperty 'd', 'v', {levels}";

    [TestMethod]
    [DataRow("'schema', 'dbo', 'view', 'v', 'column', 'b'", "OBJECT_OR_COLUMN:v:2")]
    [DataRow("'schema', 'dbo', 'function', 'itf', 'column', 'a'", "OBJECT_OR_COLUMN:itf:1")]
    [DataRow("'schema', 'dbo', 'procedure', 'p', 'parameter', '@y'", "PARAMETER:p:2")]
    [DataRow("'schema', 'dbo', 'function', 'sf', 'parameter', '@k'", "PARAMETER:sf:1")]
    [DataRow("'schema', 'dbo', 'table', 't', 'trigger', 'tr'", "OBJECT_OR_COLUMN:tr:0")]
    [DataRow("'schema', 'dbo', 'view', 'v', 'trigger', 'vtr'", "OBJECT_OR_COLUMN:vtr:0")]
    [DataRow("'schema', 'dbo', 'table', 't', 'index', 'ix'", "INDEX:t:2")]
    [DataRow("'schema', 'dbo', 'sequence', 'sq'", "OBJECT_OR_COLUMN:sq:0")]
    [DataRow("'schema', 'dbo', 'synonym', 'sy'", "OBJECT_OR_COLUMN:sy:0")]
    [DataRow("'schema', 'dbo', 'rule', 'r1'", "OBJECT_OR_COLUMN:r1:0")]
    [DataRow("'schema', 'dbo', 'default', 'd1'", "OBJECT_OR_COLUMN:d1:0")]
    public void AnObjectLevelTarget_LandsOnItsObject(string levels, string expected)
        => AreEqual(expected, Seeded().ExecuteScalar(Add(levels) + "; select concat(class_desc, ':', object_name(major_id), ':', minor_id) from sys.extended_properties"));

    [TestMethod]
    [DataRow("'schema', 'dbo', 'type', 'at'", "TYPE:at:0")]
    [DataRow("'schema', 'dbo', 'type', 'tt'", "TYPE:tt:0")]
    [DataRow("'schema', 'dbo', 'type', 'tt', 'column', 'k'", "TYPE_COLUMN:tt:1")]
    public void ATypeTarget_LandsOnItsUserTypeId(string levels, string expected)
        => AreEqual(expected, Seeded().ExecuteScalar(Add(levels) + "; select concat(class_desc, ':', type_name(major_id), ':', minor_id) from sys.extended_properties"));

    [TestMethod]
    public void AnXmlSchemaCollection_LandsOnItsId()
        => AreEqual(1, Seeded().ExecuteScalar(Add("'schema', 'dbo', 'xml schema collection', 'xc'")
            + "; select count(*) from sys.extended_properties e join sys.xml_schema_collections x on x.xml_collection_id = e.major_id where e.class_desc = 'XML_SCHEMA_COLLECTION' and x.name = 'xc'"));

    [TestMethod]
    public void AUser_LandsOnItsPrincipalId()
        => AreEqual("DATABASE_PRINCIPAL:u", Seeded().ExecuteScalar(Add("'user', 'u'") + "; select concat(class_desc, ':', user_name(major_id)) from sys.extended_properties"));

    [TestMethod]
    [DataRow("'trigger', 'nope'", 15096, 10)]
    [DataRow("'filegroup', 'nope'", 15096, 1)]
    [DataRow("'bogus', 'x'", 15600, 3)]
    [DataRow("'schema', null", 15600, 2)]
    [DataRow("null, 'dbo'", 15600, 2)]
    [DataRow("'schema', 'nope'", 15135, 4)]
    [DataRow("'user', 'nope'", 15135, 1)]
    [DataRow("'user', 'dbo'", 15135, 2)]
    [DataRow("'user', 'u', 'table', 't'", 15135, 27)]
    [DataRow("'schema', 'dbo', 'bogus', 'x'", 15600, 5)]
    [DataRow("'schema', 'dbo', null, 't'", 15600, 2)]
    [DataRow("'schema', 'dbo', 'table', 'nope'", 15135, 8)]
    [DataRow("'schema', 'dbo', 'procedure', 't'", 15135, 9)]
    [DataRow("'schema', 'dbo', 'type', 'nope'", 15135, 6)]
    [DataRow("'schema', 'dbo', 'table', 't', 'bogus', 'x'", 15600, 11)]
    [DataRow("'schema', 'dbo', 'table', 't', 'parameter', '@x'", 15600, 12)]
    [DataRow("'schema', 'dbo', 'procedure', 'p', 'column', 'a'", 15600, 12)]
    [DataRow("'schema', 'dbo', 'view', 'v', 'constraint', 'ck'", 15600, 12)]
    [DataRow("'schema', 'dbo', 'function', 'sf', 'column', 'a'", 15135, 14)]
    [DataRow("'schema', 'dbo', 'table', 't', 'column', 'nope'", 15135, 15)]
    [DataRow("'schema', 'dbo', 'view', 'v', 'column', 'nope'", 15135, 15)]
    [DataRow("'schema', 'dbo', 'procedure', 'p', 'parameter', '@nope'", 15135, 16)]
    [DataRow("'schema', 'dbo', 'table', 't', 'trigger', 'nope'", 15135, 17)]
    [DataRow("'schema', 'dbo', 'table', 't', 'index', 'nope'", 15600, 17)]
    [DataRow("'schema', 'dbo', 'table', 't', 'constraint', 'nope'", 15135, 19)]
    public void ARefusal_CarriesItsLevelsState(string levels, int number, int state)
    {
        var error = Seeded().AssertSqlError(Add(levels), number).Errors[0];
        AreEqual(state, error.State);
        AreEqual(37, error.LineNumber);
        AreEqual("sp_addextendedproperty", error.Procedure);
    }

    [TestMethod]
    [DataRow("exec sp_addextendedproperty null, 'v'", "sp_addextendedproperty", 22)]
    [DataRow("exec sp_updateextendedproperty null, 'v'", "sp_updateextendedproperty", 22)]
    [DataRow("exec sp_dropextendedproperty null", "sp_dropeextendedproperty", 14)]
    public void ANullName_IsTheSeverity15ArgumentCheck(string statement, string named, int line)
    {
        var error = new Simulation().AssertSqlError(statement, 15600).Errors[0];
        AreEqual(15, error.Class);
        AreEqual(line, error.LineNumber);
        AreEqual($"An invalid parameter or option was specified for procedure '{named}'.", error.Message);
    }

    [TestMethod]
    [DataRow("exec sp_updateextendedproperty 'd', 'v'", 2, 36)]
    [DataRow("exec sp_dropextendedproperty 'd'", 1, 28)]
    public void AMissingProperty_CarriesItsProceduresStateAndLine(string statement, int state, int line)
    {
        var error = new Simulation().AssertSqlError(statement, 15217).Errors[0];
        AreEqual(state, error.State);
        AreEqual(line, error.LineNumber);
    }

    /// <summary>A resolution error ends the batch and rolls back; the argument check lets it run on.</summary>
    [TestMethod]
    [DataRow("exec sp_addextendedproperty 'd', 'v', 'schema', 'nope'", "0:0")]
    [DataRow("exec sp_dropextendedproperty 'd'", "0:0")]
    [DataRow("exec sp_addextendedproperty null, 'v'", "1:2")]
    public void AnError_ReachesAsRealsDoes(string statement, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table w (a int)");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"begin tran; insert w values (1); {statement}; insert w values (2)";
        _ = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        command.CommandText = "select concat(@@trancount, ':', (select count(*) from w))";
        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    public void AProperty_GoesWithWhatItDescribes()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(string.Join("; ",
            Add("'schema', 'dbo', 'table', 't', 'column', 'b'"),
            Add("'schema', 'dbo', 'table', 't', 'index', 'ix'"),
            Add("'schema', 'dbo', 'table', 't', 'constraint', 'ck'"),
            Add("'schema', 'dbo', 'table', 't', 'trigger', 'tr'"),
            Add("'schema', 'dbo', 'type', 'at'"),
            Add("'schema', 'dbo', 'type', 'tt', 'column', 'k'"),
            Add("'schema', 'dbo', 'procedure', 'p', 'parameter', '@x'"),
            Add("'user', 'u'")));
        AreEqual(8, sim.ExecuteScalar("select count(*) from sys.extended_properties"));
        AreEqual(0, sim.ExecuteScalar("""
            drop index ix on t; alter table t drop constraint ck; drop trigger tr; alter table t drop column b;
            drop type at; drop type tt; drop procedure p; drop user u;
            select count(*) from sys.extended_properties
            """));
    }

    [TestMethod]
    public void AnAlter_CarriesColumnAndParameterPropertiesByName()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(string.Join("; ",
            Add("'schema', 'dbo', 'view', 'v', 'column', 'b'"),
            Add("'schema', 'dbo', 'view', 'v', 'column', 'a'"),
            Add("'schema', 'dbo', 'procedure', 'p', 'parameter', '@x'"),
            Add("'schema', 'dbo', 'procedure', 'p', 'parameter', '@y'")));
        sim.ExecuteBatches(
            "alter view v as select b, a as c from t",
            "alter procedure p @y int as select 2");
        AreEqual("v:1|p:1", sim.ExecuteScalar(
            "select string_agg(concat(object_name(major_id), ':', minor_id), '|') within group (order by class, major_id) from sys.extended_properties"));
    }

    [TestMethod]
    public void RollingBackAnAlter_RestoresTheBinding()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(Add("'schema', 'dbo', 'procedure', 'p', 'parameter', '@x'"));
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "begin tran";
        _ = command.ExecuteNonQuery();
        command.CommandText = "alter procedure p @z int as select 2";
        _ = command.ExecuteNonQuery();
        command.CommandText = "rollback; select count(*) from sys.extended_properties where class = 2 and minor_id = 1";
        AreEqual(1, command.ExecuteScalar());
    }

    [TestMethod]
    [DataRow("'schema', 'dbo', 'procedure', 'p', 'parameter', default", "PARAMETER:@x|PARAMETER:@y")]
    [DataRow("'schema', 'dbo', 'view', 'v', 'column', null", "COLUMN:b")]
    [DataRow("'schema', 'dbo', 'table', 't', 'index', null", "INDEX:ix")]
    [DataRow("'schema', 'dbo', 'table', 't', 'trigger', 'TR'", "TRIGGER:tr")]
    [DataRow("'schema', 'dbo', 'type', null, null, null", "TYPE:at|TYPE:tt")]
    [DataRow("'schema', 'dbo', 'type', 'tt', 'column', null", "COLUMN:k")]
    [DataRow("'schema', 'dbo', 'xml schema collection', null, null, null", "XML SCHEMA COLLECTION:xc")]
    [DataRow("'user', null, null, null, null, null", "USER:u")]
    [DataRow("'schema', 'dbo', 'table', null, 'column', null", "")]
    [DataRow("'schema', 'dbo', null, 't', null, null", "")]
    [DataRow("'schema', 'dbo', 'bogus', null, null, null", "")]
    [DataRow("'schema', 'dbo', 'table', 't', 'parameter', null", "")]
    public void FnListExtendedProperty_ListsTheAddressedLevel(string levels, string expected)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(string.Join("; ",
            Add("'schema', 'dbo', 'view', 'v', 'column', 'b'"),
            Add("'schema', 'dbo', 'table', 't', 'index', 'ix'"),
            Add("'schema', 'dbo', 'table', 't', 'trigger', 'tr'"),
            Add("'schema', 'dbo', 'procedure', 'p', 'parameter', '@y'"),
            Add("'schema', 'dbo', 'procedure', 'p', 'parameter', '@x'"),
            Add("'schema', 'dbo', 'type', 'at'"),
            Add("'schema', 'dbo', 'type', 'tt'"),
            Add("'schema', 'dbo', 'type', 'tt', 'column', 'k'"),
            Add("'schema', 'dbo', 'xml schema collection', 'xc'"),
            Add("'user', 'u'")));
        AreEqual(expected, sim.ExecuteScalar($"select isnull(string_agg(concat(objtype, ':', objname), '|') within group (order by objname), '') from fn_listextendedproperty(null, {levels})"));
    }

    [TestMethod]
    public void FnListExtendedProperty_DatabaseLevel_HasNoObject()
        => AreEqual("1:1:varchar", new Simulation().ExecuteScalar("""
            exec sp_addextendedproperty 'd', 'db';
            select concat(count(*), ':', count(name), ':', max(cast(sql_variant_property(value, 'BaseType') as sysname)))
            from fn_listextendedproperty(default, default, default, default, default, default, default) where objtype is null and objname is null
            """));

    [TestMethod]
    public void FnListExtendedProperty_TooFewArguments_IsMsg313()
        => _ = new Simulation().AssertSqlError("select * from fn_listextendedproperty(null, 'schema', 'dbo')", 313);

    [TestMethod]
    public void TypeId_ResolvesAnAliasType()
        => AreEqual("at", new Simulation().ExecuteScalar("create type at from int; select type_name(type_id('dbo.at'))"));
}
