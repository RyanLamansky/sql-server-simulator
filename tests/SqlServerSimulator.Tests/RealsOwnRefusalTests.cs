using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Shapes real refuses with an error of its own, each of which the simulator
/// once answered as an unbuilt feature (probed 2026-10-21 against SQL Server
/// 2025).
/// </summary>
[TestClass]
public sealed class RealsOwnRefusalTests
{
    [TestMethod]
    [DataRow("backup database d to disk = 'x.bak' with stopat = '2026-01-01'", 155, 3, "'stopat' is not a recognized BACKUP option.")]
    [DataRow("backup log d to disk = 'x.bak' with PARTIAL", 155, 1, "'PARTIAL' is not a recognized BACKUP option.")]
    [DataRow("backup database d to disk = 'x.bak' with filestream (directory_name = 'x')", 155, 1, "'filestream' is not a recognized BACKUP option.")]
    [DataRow("restore database d from disk = 'x.bak' with expiredate = '2026-01-01'", 155, 3, "'expiredate' is not a recognized RESTORE option.")]
    [DataRow("restore log d from disk = 'x.bak' with retaindays = 1", 155, 2, "'retaindays' is not a recognized RESTORE option.")]
    [DataRow("restore database d from disk = 'x.bak' with no_truncate", 155, 1, "'no_truncate' is not a recognized RESTORE option.")]
    [DataRow("backup database d to disk = 'x.bak' with CREDENTIAL", 153, 1, "Invalid usage of the option CREDENTIAL in the BACKUP statement.")]
    [DataRow("restore database d from disk = 'x.bak' with credential", 153, 1, "Invalid usage of the option credential in the RESTORE statement.")]
    [DataRow("backup database d to disk = 'x.bak' with standby = 'n', password = 'x', no_truncate, mediapassword = 'm', norecovery", 3032, 2,
        "One or more of the options (norecovery, no_truncate, mediapassword, password, standby) are not supported for this statement. Review the documentation for supported options.")]
    [DataRow("backup log d to disk = 'x.bak' with mediapassword = 'm', metadata_only", 3032, 2,
        "One or more of the options (snapshot, mediapassword) are not supported for this statement. Review the documentation for supported options.")]
    [DataRow("restore headeronly from disk = 'x.bak' with stats, restricted_user, keep_cdc, replace, recovery, norecovery, keep_replication, new_broker", 3032, 2,
        "One or more of the options (stats, norecovery, broker_option, keep_replication, recovery, restricted_user, replace, keep_cdc) are not supported for this statement. Review the documentation for supported options.")]
    [DataRow("backup log nosuch to disk = 'x.bak'", 911, 10, "Database 'nosuch' does not exist. Make sure that the name is entered correctly.")]
    [DataRow("exec sp_executesql N'select ''a'' c' with result sets ((c varchar(10) collate Bogus_CI_AS))", 448, 2, "Invalid collation 'Bogus_CI_AS'.")]
    [DataRow("create database d collate Bogus_CI_AS", 448, 3, "Invalid collation 'Bogus_CI_AS'.")]
    [DataRow("select hierarchyid::parse('/1/')", 6506, 10, "Could not find method 'parse' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select geometry::point(1, 2, 0)", 6506, 10, "Could not find method 'point' for type 'Microsoft.SqlServer.Types.SqlGeometry' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select geometry::BogusFromText('POINT(1 2)', 0)", 6506, 10, "Could not find method 'BogusFromText' for type 'Microsoft.SqlServer.Types.SqlGeometry' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("declare @x xml = '<a/>', @v int = 1; set @x.modify('count(sql:variable(\"@v\"))')", 6305, 1, "XQuery data manipulation expression required in XML data type method.")]
    [DataRow("declare @h int; exec sp_xml_preparedocument @h output, '<a/>'; select * from openxml(@h, '/zz') with (c nvarchar(10) '@mp:BoGus')", 7007, 1,
        "OPENXML encountered unknown metaproperty '@mp:BoGus'.")]
    [DataRow("create application role r with password = 123", 102, 1, "Incorrect syntax near '123'.")]
    [DataRow("create login l from bogus", 102, 1, "Incorrect syntax near 'bogus'.")]
    [DataRow("create login l from [windows]", 102, 1, "Incorrect syntax near 'windows'.")]
    [DataRow("create login l from external provider", 37525, 1, "Command 'CREATE LOGIN FROM EXTERNAL PROVIDER' is not supported as Azure Active Directory is not configured for this instance.")]
    [DataRow("create table t (a int); dbcc checktable('t', repair_fast)", 7919, 3, "Repair statement not processed. Database needs to be in single user mode.")]
    [DataRow("create table t (a int) with (bogus = on)", 102, 1, "Incorrect syntax near 'bogus'.")]
    [DataRow("create table t (a int); alter table t set (bogus = on)", 102, 1, "Incorrect syntax near 'bogus'.")]
    public void RaisesRealsError(string batch, int number, int state, string message)
    {
        var ex = new Simulation().AssertSqlError(batch, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual((byte)state, ex.State);
    }

    /// <summary>
    /// These refusals end the batch while it compiles: nothing in it runs,
    /// an un-taken branch's statement included.
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 0 backup database d to disk = 'x.bak' with password = 'x'", 3032)]
    [DataRow("if 1 = 0 restore headeronly from disk = 'x.bak' with replace", 3032)]
    [DataRow("alter database current collate Bogus_CI_AS", 448)]
    [DataRow("begin try alter database current collate Bogus_CI_AS end try begin catch select 1 end catch", 448)]
    public void RefusedWhileTheBatchCompiles(string statement, int number)
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create table ran (a int); " + statement, number);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 'ran'"));
    }

    /// <summary>
    /// <c>CREATE DATABASE</c>'s Msg 448 names the line of the token after the
    /// statement, or the last token at the end of the text.
    /// </summary>
    [TestMethod]
    [DataRow("print 'a'\ncreate database d collate Bogus_CI_AS\n\n\nprint 'b'", 5)]
    [DataRow("create database d\ncollate Bogus_CI_AS; print 1", 2)]
    [DataRow("create database d collate Bogus_CI_AS\n\n\n", 1)]
    public void CreateDatabasesInvalidCollation_NamesTheLineAfterTheStatement(string batch, int line)
        => AreEqual(line, new Simulation().AssertSqlError(batch, 448).LineNumber);
}
