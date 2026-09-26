using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER SCHEMA … TRANSFER</c> across every object class — rules,
/// defaults, alias types and XML schema collections among them — and what it
/// refuses. Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SchemaTransferKindTests
{
    private const string Collection = """
        create xml schema collection xc as '<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"><xsd:element name="a" type="xsd:int"/></xsd:schema>'
        """;

    [TestMethod]
    [DataRow("create rule r as @v > 0", "alter schema s2 transfer r", "select schema_name(schema_id) from sys.objects where name = 'r'")]
    [DataRow("create default d as 0", "alter schema s2 transfer object::dbo.d", "select schema_name(schema_id) from sys.objects where name = 'd'")]
    [DataRow("create type at from int", "alter schema s2 transfer type::at", "select schema_name(schema_id) from sys.types where name = 'at'")]
    [DataRow(Collection, "alter schema s2 transfer xml schema collection::xc", "select schema_name(schema_id) from sys.xml_schema_collections where name = 'xc'")]
    public void AKind_MovesToTheDestination(string create, string transfer, string check)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", create, transfer);
        AreEqual("s2", sim.ExecuteScalar(check));
    }

    [TestMethod]
    public void ABoundRule_StaysBoundAcrossTheMove()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", "create rule r as @v > 0", "create table t (a int)", "exec sp_bindrule 'r', 't.a'", "alter schema s2 transfer r");
        AreEqual("s2.r", sim.ExecuteScalar("select object_schema_name(rule_object_id) + '.' + object_name(rule_object_id) from sys.columns where object_id = object_id('t')"));
    }

    [TestMethod]
    [DataRow("create type at from int; create type s2.at from bigint", "alter schema s2 transfer type::at", "The type with name \"at\" already exists.")]
    [DataRow("create type at from int; create type s2.at as table (a int)", "alter schema s2 transfer type::at", "The type with name \"at\" already exists.")]
    [DataRow("create rule r as @v > 0", "alter schema s2 transfer r", "The object with name \"r\" already exists.")]
    public void ANameTakenInTheDestination_IsMsg15530(string create, string transfer, string message)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", create, "create rule s2.r as @v > 1");
        AreEqual(message, sim.AssertSqlError(transfer, 15530).Errors[0].Message);
    }

    [TestMethod]
    public void AnXmlSchemaCollectionNameTaken_IsMsg15530()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", Collection, Collection.Replace("collection xc", "collection s2.xc", StringComparison.Ordinal));
        AreEqual("The xml schema collection with name \"xc\" already exists.", sim.AssertSqlError("alter schema s2 transfer xml schema collection::xc", 15530).Errors[0].Message);
    }

    [TestMethod]
    [DataRow("alter schema s2 transfer xml schema collection::nope", "Cannot find the xml schema collection 'nope', because it does not exist or you do not have permission.")]
    [DataRow("alter schema s2 transfer at", "Cannot find the object 'at', because it does not exist or you do not have permission.")]
    public void AMissingSource_IsMsg15151(string transfer, string message)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", "create type at from int");
        AreEqual(message, sim.AssertSqlError(transfer, 15151).Errors[0].Message);
    }

    [TestMethod]
    public void AConstraint_IsOwnedByItsTable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", "create table t (a int constraint ck check (a > 0))");
        _ = sim.AssertSqlError("alter schema s2 transfer ck", 15347);
    }

    [TestMethod]
    public void TheDdlEvent_NamesTheMovedKind()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema s2",
            "create table t (a int)",
            "create rule r as @v > 0",
            "create table log (k sysname)",
            "create trigger dt on database for alter_schema as insert log select eventdata().value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname')",
            "alter schema s2 transfer t",
            "alter schema s2 transfer r");
        AreEqual("RULE|TABLE", sim.ExecuteScalar("select string_agg(k, '|') within group (order by k) from log"));
    }

    [TestMethod]
    public void RollingBack_ReturnsTheTypeAndCollection()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create schema s2", "create type at from int", Collection);
        _ = sim.ExecuteNonQuery("begin tran; alter schema s2 transfer type::at; alter schema s2 transfer xml schema collection::xc; rollback");
        AreEqual("dbo:dbo", sim.ExecuteScalar(
            "select concat((select schema_name(schema_id) from sys.types where name = 'at'), ':', (select schema_name(schema_id) from sys.xml_schema_collections where name = 'xc'))"));
    }
}
