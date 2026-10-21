using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The DDL events of permission statements, application roles, full-text
/// catalogs and indexes, XML schema collections, extended properties and the
/// binding procedures, with the per-kind elements their <c>EVENTDATA()</c>
/// carries (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class DdlEventCoverageTests
{
    /// <summary>
    /// A trigger logging each event's document without the elements that vary
    /// by session and clock, so a whole document compares verbatim.
    /// </summary>
    private static Simulation Logging(string setup, string events = "ddl_database_level_events")
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ev (n int identity, s nvarchar(max))",
            $"""
            create trigger dt on database for {events} as
            begin
                declare @e xml = eventdata();
                set @e.modify('delete (/EVENT_INSTANCE/PostTime, /EVENT_INSTANCE/SPID, /EVENT_INSTANCE/ServerName, /EVENT_INSTANCE/LoginName, /EVENT_INSTANCE/UserName, /EVENT_INSTANCE/DatabaseName, /EVENT_INSTANCE/TSQLCommand/SetOptions)');
                insert ev (s) select cast(@e as nvarchar(max));
            end
            """);
        if (setup.Length != 0)
            sim.ExecuteBatches(setup);
        return sim;
    }

    private static string Last(Simulation sim) => (string)sim.ExecuteScalar("select top (1) s from ev order by n desc")!;

    private static string Types(Simulation sim) =>
        (string)sim.ExecuteScalar("select string_agg(cast(s as xml).value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), ',') within group (order by n) from ev")!;

    [TestMethod]
    public void Grant_OnAnObject()
    {
        var sim = Logging("create table t (a int); create user u without login");
        _ = sim.ExecuteNonQuery("GRANT SELECT, Update ON t TO u WITH GRANT OPTION;");
        AreEqual(
            "<EVENT_INSTANCE><EventType>GRANT_DATABASE</EventType><SchemaName>dbo</SchemaName><ObjectName>t</ObjectName><ObjectType>TABLE</ObjectType>"
            + "<Grantor>dbo</Grantor><Permissions><Permission>select</Permission><Permission>update</Permission></Permissions><Grantees><Grantee>u</Grantee></Grantees>"
            + "<AsGrantor/><GrantOption>1</GrantOption><CascadeOption>0</CascadeOption>"
            + "<TSQLCommand><CommandText>GRANT SELECT, Update ON t TO u WITH GRANT OPTION;</CommandText></TSQLCommand></EVENT_INSTANCE>",
            Last(sim));
    }

    [TestMethod]
    public void Deny_OnTheDatabase_ToSeveral()
    {
        var sim = Logging("create user u without login; create role r");
        _ = sim.ExecuteNonQuery("deny create view, create procedure to u, r");
        AreEqual(
            "<EVENT_INSTANCE><EventType>DENY_DATABASE</EventType><SchemaName/><ObjectName>simulated</ObjectName><ObjectType>DATABASE</ObjectType>"
            + "<Grantor>dbo</Grantor><Permissions><Permission>create view</Permission><Permission>create procedure</Permission></Permissions>"
            + "<Grantees><Grantee>u</Grantee><Grantee>r</Grantee></Grantees><AsGrantor/><GrantOption>0</GrantOption><CascadeOption>0</CascadeOption>"
            + "<TSQLCommand><CommandText>deny create view, create procedure to u, r</CommandText></TSQLCommand></EVENT_INSTANCE>",
            Last(sim));
    }

    [TestMethod]
    [DataRow("grant select on schema::dbo to u as dbo", "<SchemaName/><ObjectName>dbo</ObjectName><ObjectType>SCHEMA</ObjectType><Grantor>dbo</Grantor>", "<AsGrantor>dbo</AsGrantor>")]
    [DataRow("grant impersonate on user::u to r", "<SchemaName/><ObjectName>u</ObjectName><ObjectType>USER</ObjectType><Grantor>u</Grantor>", "<AsGrantor/>")]
    [DataRow("grant alter on role::r to u", "<SchemaName/><ObjectName>r</ObjectName><ObjectType>ROLE</ObjectType><Grantor>dbo</Grantor>", "<AsGrantor/>")]
    [DataRow("grant execute on p to u", "<SchemaName>dbo</SchemaName><ObjectName>p</ObjectName><ObjectType>PROCEDURE</ObjectType><Grantor>dbo</Grantor>", "<AsGrantor/>")]
    [DataRow("grant update on sq to u", "<SchemaName>dbo</SchemaName><ObjectName>sq</ObjectName><ObjectType>SEQUENCE</ObjectType><Grantor>dbo</Grantor>", "<AsGrantor/>")]
    public void Grant_Securables(string statement, string securable, string asGrantor)
    {
        var sim = Logging("create user u without login; create role r; create sequence sq", "grant_database");
        sim.ExecuteBatches("create procedure p as select 1");
        _ = sim.ExecuteNonQuery(statement);
        var document = Last(sim);
        Contains(securable, document);
        Contains(asGrantor, document);
    }

    [TestMethod]
    public void Revoke_CascadeCountsOnlyWhereAGrantOptionWasHeld()
    {
        var sim = Logging("create table t (a int); create user u without login; grant select on t to u; grant update on t to u with grant option");
        _ = sim.ExecuteNonQuery("revoke select on t from u cascade");
        Contains("<GrantOption>0</GrantOption><CascadeOption>0</CascadeOption>", Last(sim));
        _ = sim.ExecuteNonQuery("revoke update on t from u cascade");
        Contains("<GrantOption>0</GrantOption><CascadeOption>1</CascadeOption>", Last(sim));
    }

    [TestMethod]
    public void RevokeGrantOptionFor_ReportsTheGrantOption()
    {
        var sim = Logging("create table t (a int); create user u without login; grant select on t to u with grant option");
        _ = sim.ExecuteNonQuery("revoke grant option for select on t from u cascade");
        Contains("<EventType>REVOKE_DATABASE</EventType>", Last(sim));
        Contains("<GrantOption>1</GrantOption><CascadeOption>1</CascadeOption>", Last(sim));
    }

    [TestMethod]
    public void Grant_OnAnotherDatabase_Is4610()
        => new Simulation().AssertSqlError("create user u without login; grant connect on database::master to u", 4610);

    [TestMethod]
    public void ApplicationRole_Events_MaskThePassword()
    {
        var sim = Logging("");
        sim.ExecuteBatches(
            "create application role ar with password = 'P@ssw0rd1!', default_schema = dbo;",
            "alter application role ar with name = ar2",
            "alter application role ar2 with password = 'P@ssw0rd2!'",
            "drop application role ar2");
        AreEqual("CREATE_APPLICATION_ROLE,ALTER_APPLICATION_ROLE,ALTER_APPLICATION_ROLE,DROP_APPLICATION_ROLE", Types(sim));
        var created = (string)sim.ExecuteScalar("select s from ev where n = 1")!;
        StartsWith("<EVENT_INSTANCE><EventType>CREATE_APPLICATION_ROLE</EventType><ObjectName>ar</ObjectName><ObjectType>APPLICATION ROLE</ObjectType><DefaultSchema>dbo</DefaultSchema><SID>", created);
        Contains("<TSQLCommand><CommandText>create application role ar with password = '******', default_schema = dbo;</CommandText></TSQLCommand></EVENT_INSTANCE>", created);
        Contains("<ObjectName>ar2</ObjectName>", (string)sim.ExecuteScalar("select s from ev where n = 2")!);
    }

    [TestMethod]
    public void FullText_Events()
    {
        var sim = Logging("create table t (id int not null constraint pk primary key, c nvarchar(100))");
        sim.ExecuteBatches(
            "create fulltext catalog fc",
            "alter fulltext catalog fc reorganize",
            "create fulltext index on t (c) key index pk on fc",
            "alter fulltext index on t disable",
            "drop fulltext index on t",
            "drop fulltext catalog fc");
        AreEqual("CREATE_TABLE,CREATE_FULLTEXT_CATALOG,ALTER_FULLTEXT_CATALOG,CREATE_FULLTEXT_INDEX,ALTER_FULLTEXT_INDEX,DROP_FULLTEXT_INDEX,DROP_FULLTEXT_CATALOG", Types(sim));
        Contains("<EventType>CREATE_FULLTEXT_CATALOG</EventType><ObjectName>fc</ObjectName><ObjectType>FULLTEXT CATALOG</ObjectType>", (string)sim.ExecuteScalar("select s from ev where n = 2")!);
        Contains("<SchemaName>dbo</SchemaName><ObjectName>t</ObjectName><ObjectType>TABLE</ObjectType>", (string)sim.ExecuteScalar("select s from ev where n = 4")!);
    }

    [TestMethod]
    public void XmlSchemaCollection_Events_ReportTheStatementWithoutItsSeparator()
    {
        var sim = Logging("create schema s");
        _ = sim.ExecuteNonQuery("""
            create xml schema collection s.xc as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="a"/></xs:schema>';
            drop xml schema collection s.xc;
            """);
        AreEqual("CREATE_SCHEMA,CREATE_XML_SCHEMA_COLLECTION,DROP_XML_SCHEMA_COLLECTION", Types(sim));
        AreEqual(
            "<EVENT_INSTANCE><EventType>DROP_XML_SCHEMA_COLLECTION</EventType><SchemaName>s</SchemaName><ObjectName>xc</ObjectName><ObjectType>XML SCHEMA COLLECTION</ObjectType>"
            + "<TSQLCommand><CommandText>drop xml schema collection s.xc</CommandText></TSQLCommand></EVENT_INSTANCE>",
            Last(sim));
    }

    [TestMethod]
    public void ExtendedProperty_OnAColumn()
    {
        var sim = Logging("create table t (a int)");
        _ = sim.ExecuteNonQuery("exec sp_addextendedproperty @name = N'c', @value = 'cv', @level0type = 'schema', @level0name = 'dbo', @level1type = 'table', @level1name = 't', @level2type = 'column', @level2name = 'a'");
        AreEqual(
            "<EVENT_INSTANCE><EventType>CREATE_EXTENDED_PROPERTY</EventType><SchemaName>dbo</SchemaName><ObjectName>a</ObjectName><ObjectType>COLUMN</ObjectType>"
            + "<TargetObjectName>t</TargetObjectName><TargetObjectType>TABLE</TargetObjectType><PropertyName>c</PropertyName><PropertyValue>cv</PropertyValue>"
            + "<Parameters><Param>c</Param><Param>cv</Param><Param>schema</Param><Param>dbo</Param><Param>table</Param><Param>t</Param><Param>column</Param><Param>a</Param></Parameters>"
            + "<TSQLCommand><CommandText>exec sp_addextendedproperty @name = N'c', @value = 'cv', @level0type = 'schema', @level0name = 'dbo', @level1type = 'table', @level1name = 't', @level2type = 'column', @level2name = 'a'</CommandText></TSQLCommand></EVENT_INSTANCE>",
            Last(sim));
    }

    [TestMethod]
    public void ExtendedProperty_OnTheDatabase_UpdateAndDrop()
    {
        var sim = Logging("");
        _ = sim.ExecuteNonQuery("exec sp_addextendedproperty 'p', 'v'; exec sp_updateextendedproperty 'p', 'v2'; exec sp_dropextendedproperty 'p'");
        AreEqual("CREATE_EXTENDED_PROPERTY,ALTER_EXTENDED_PROPERTY,DROP_EXTENDED_PROPERTY", Types(sim));
        AreEqual(
            "<EVENT_INSTANCE><EventType>DROP_EXTENDED_PROPERTY</EventType><SchemaName/><ObjectName>simulated</ObjectName><ObjectType>DATABASE</ObjectType>"
            + "<TargetObjectName/><TargetObjectType/><PropertyName>p</PropertyName><Parameters><Param>p</Param><Param/><Param/><Param/><Param/><Param/><Param/></Parameters>"
            + "<TSQLCommand><CommandText>exec sp_dropextendedproperty 'p'</CommandText></TSQLCommand></EVENT_INSTANCE>",
            Last(sim));
    }

    [TestMethod]
    public void Binding_Events()
    {
        var sim = Logging("create table t (a int, b int)");
        sim.ExecuteBatches("create default d1 as 0", "create type ty from int");
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd1', 't.a'; exec sp_unbindefault 't.a'; exec sp_bindefault 'd1', 'ty', 'futureonly'; exec sp_unbindefault 'ty'");
        AreEqual("CREATE_TABLE,CREATE_DEFAULT,CREATE_TYPE,BIND_DEFAULT,UNBIND_DEFAULT,BIND_DEFAULT,UNBIND_DEFAULT", Types(sim));
        AreEqual(
            "<EVENT_INSTANCE><EventType>BIND_DEFAULT</EventType><SchemaName>dbo</SchemaName><ObjectName>d1</ObjectName><ObjectType>DEFAULT</ObjectType>"
            + "<Parameters><Param>d1</Param><Param>t.a</Param><Param/></Parameters><TSQLCommand><CommandText>exec sp_bindefault 'd1', 't.a'</CommandText></TSQLCommand></EVENT_INSTANCE>",
            sim.ExecuteScalar("select s from ev where n = 4"));
        Contains("<ObjectName>a</ObjectName><ObjectType>COLUMN</ObjectType><Parameters><Param>t.a</Param><Param/></Parameters>", (string)sim.ExecuteScalar("select s from ev where n = 5")!);
        Contains("<Param>ty</Param><Param>futureonly</Param>", (string)sim.ExecuteScalar("select s from ev where n = 6")!);
        Contains("<ObjectName>ty</ObjectName><ObjectType>TYPE</ObjectType>", (string)sim.ExecuteScalar("select s from ev where n = 7")!);
    }

    [TestMethod]
    [DataRow("ddl_gdr_database_events", "grant select on t to u", "GRANT_DATABASE")]
    [DataRow("ddl_application_role_events", "create application role ar with password = 'Pw!12345678'", "CREATE_APPLICATION_ROLE")]
    [DataRow("ddl_extended_property_events", "exec sp_addextendedproperty 'p', 'v'", "CREATE_EXTENDED_PROPERTY")]
    [DataRow("ddl_xml_schema_collection_events", "create xml schema collection xc as '<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>'", "CREATE_XML_SCHEMA_COLLECTION")]
    [DataRow("ddl_fulltext_catalog_events", "create fulltext catalog fc", "CREATE_FULLTEXT_CATALOG")]
    [DataRow("ddl_default_events", "exec sp_bindefault 'd1', 't.a'", "CREATE_DEFAULT,BIND_DEFAULT")]
    public void EventGroups_Cover_TheNewEvents(string group, string statement, string expected)
    {
        var sim = Logging("create table t (a int); create user u without login", group);
        sim.ExecuteBatches("create default d1 as 0");
        sim.ExecuteBatches(statement);
        AreEqual(expected, Types(sim));
    }

    /// <summary>
    /// The elements a principal event carries after <c>ObjectType</c>, by kind
    /// (probed 2026-10-21 against SQL Server 2025); <c>SID</c>'s value is
    /// stripped, its presence kept.
    /// </summary>
    [TestMethod]
    public void PrincipalEvents_CarryTheirDefaultSchemaLanguageAndSid()
    {
        var sim = Logging("");
        sim.ExecuteBatches(
            "create user u without login with default_schema = sys",
            "create role r",
            "create role r2",
            "alter role r add member u",
            "alter role r add member r2",
            "alter role r drop member u",
            "alter role r drop member r2",
            "drop user u",
            "drop role r");
        AreEqual(
            "CREATE_USER:<DefaultSchema>sys</DefaultSchema><DefaultLanguage/><SID>|"
            + "CREATE_ROLE:<SID>|"
            + "CREATE_ROLE:<SID>|"
            + "ADD_ROLE_MEMBER:<DefaultSchema>sys</DefaultSchema><SID>|"
            + "ADD_ROLE_MEMBER:<DefaultSchema/><SID>|"
            + "DROP_ROLE_MEMBER:<DefaultSchema>sys</DefaultSchema><SID>|"
            + "DROP_ROLE_MEMBER:<DefaultSchema/><SID>|"
            + "DROP_USER:<DefaultSchema>sys</DefaultSchema><DefaultLanguage/><SID>|"
            + "DROP_ROLE:<SID/>",
            sim.ExecuteScalar("""
                select string_agg(cast(s as xml).value('(/EVENT_INSTANCE/EventType)[1]', 'sysname') + ':'
                    + substring(s, charindex('</ObjectType>', s) + 13, charindex('<SID', s) - charindex('</ObjectType>', s) - 13)
                    + iif(s like '%<SID/>%', '<SID/>', '<SID>'), '|') within group (order by n)
                from ev
                """));
    }

    [TestMethod]
    [DataRow("create user u2 without login with default_language = English")]
    [DataRow("alter user u with default_language = English")]
    public void DefaultLanguage_IsRefusedForAUserOfADatabaseThatIsntContained(string statement)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login");
        sim.AssertSqlError(statement, 33234, "The parameter DEFAULT_LANGUAGE cannot be provided for users that cannot authenticate in a database.");
    }

    [TestMethod]
    [DataRow("alter table t add d int, e int null", "<Create><Columns><Name>d</Name><Name>e</Name></Columns></Create>")]
    [DataRow("alter table t add h int, constraint zx check (h > 0)", "<Create><Columns><Name>h</Name></Columns><Constraints><Name>zx</Name></Constraints></Create>")]
    [DataRow("alter table t add j int not null constraint zuj unique, k int constraint zck check (k > 0)", "<Create><Columns><Name>j</Name><Name>k</Name></Columns><Constraints><Name>zuj</Name><Name>zck</Name></Constraints></Create>")]
    [DataRow("alter table t alter column b bigint", "<Alter><Columns><Name>b</Name></Columns></Alter>")]
    [DataRow("alter table t drop constraint ck, column b", "<Drop><Columns><Name>b</Name></Columns><Constraints><Name>ck</Name></Constraints></Drop>")]
    [DataRow("alter table t drop constraint df, ck", "<Drop><Constraints><Name>df</Name><Name>ck</Name></Constraints></Drop>")]
    [DataRow("alter table t nocheck constraint all", "<Disable><Constraints all=\"true\"/></Disable>")]
    [DataRow("alter table t with check check constraint ck", "<Enable><Constraints><Name>ck</Name></Constraints></Enable>")]
    [DataRow("alter table t disable trigger tr", "<Disable><Triggers><Name>tr</Name></Triggers></Disable>")]
    [DataRow("alter table t enable trigger all", "<Enable><Triggers all=\"true\"/></Enable>")]
    [DataRow("alter table t rebuild", "")]
    public void AlterTable_ListsItsActions(string statement, string actions)
    {
        var sim = Logging("create table t (a int not null, b int, c int constraint df default 1, constraint ck check (a > 0))");
        sim.ExecuteBatches("create trigger tr on t for insert as select 1", statement);
        var document = (string)sim.ExecuteScalar("select s from ev where s like '%ALTER_TABLE%'")!;
        AreEqual(actions.Length == 0 ? "" : "<AlterTableActionList>" + actions + "</AlterTableActionList>",
            document[(document.IndexOf("</ObjectType>", StringComparison.Ordinal) + 13)..document.IndexOf("<TSQLCommand>", StringComparison.Ordinal)]);
    }

    [TestMethod]
    [DataRow("create synonym s for dbo.t", "<TargetServerName/><TargetDatabaseName/><TargetSchemaName>dbo</TargetSchemaName><TargetObjectName>t</TargetObjectName>")]
    [DataRow("create synonym s for t", "<TargetServerName/><TargetDatabaseName/><TargetSchemaName/><TargetObjectName>t</TargetObjectName>")]
    [DataRow("create synonym s for simulated..t", "<TargetServerName/><TargetDatabaseName>simulated</TargetDatabaseName><TargetSchemaName/><TargetObjectName>t</TargetObjectName>")]
    [DataRow("create synonym s for srv.db.sch.obj", "<TargetServerName>srv</TargetServerName><TargetDatabaseName>db</TargetDatabaseName><TargetSchemaName>sch</TargetSchemaName><TargetObjectName>obj</TargetObjectName>")]
    public void Synonym_Events_NameTheTargetsQualifiersAsWritten(string statement, string target)
    {
        var sim = Logging("create table t (a int)");
        _ = sim.ExecuteNonQuery(statement);
        Contains("<ObjectType>SYNONYM</ObjectType>" + target, (string)sim.ExecuteScalar("select s from ev where s like '%CREATE_SYNONYM%'")!);
    }

    [TestMethod]
    public void SpAddRoleMember_ReportsTheAlterRoleItRuns_AndItsTriggersCountNothing()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ev (n int identity, s nvarchar(max)); create role r; create user u without login",
            "create trigger dt on database for add_role_member, drop_role_member as insert ev (s) select eventdata().value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'nvarchar(max)') + '|' + eventdata().value('(/EVENT_INSTANCE/TSQLCommand/SetOptions/@QUOTED_IDENTIFIER)[1]', 'nvarchar(3)')");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set quoted_identifier off").ExecuteNonQuery();
        AreEqual(-1, connection.CreateCommand("exec sp_addrolemember 'r', 'u'; exec sp_droprolemember 'r', 'u'").ExecuteNonQuery());
        AreEqual("alter role [r] add member [u]|ON,alter role [r] drop member [u]|ON", sim.ExecuteScalar("select string_agg(s, ',') within group (order by n) from ev"));
    }
}
