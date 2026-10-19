namespace SqlServerSimulator;

/// <summary>
/// The statements real SQL Server 2025's system procedures run without
/// <c>NOCOUNT</c> around their work, each closing in a DONEINPROC of its kind,
/// and the transactions some open — captured 2026-10-17 with a raw TDS client.
/// A response reads as letters: <c>I</c> a condition, <c>S</c> an assignment
/// counting its row, <c>N</c> a count-less SELECT-kind statement (a cursor's
/// <c>DECLARE</c> or <c>FETCH</c>), <c>O</c> a <c>SET … OFF</c>, <c>X</c> an
/// <c>EXEC</c>, <c>P</c> a <c>PRINT</c>, <c>R</c> a <c>RAISERROR</c>,
/// <c>D</c> a <c>DBCC</c>, <c>U</c> a <c>RETURN</c>, <c>T</c> / <c>E</c> a <c>TRY</c>'s start and
/// end, <c>(</c> / <c>)</c> a cursor's <c>OPEN</c> and <c>DEALLOCATE</c>,
/// <c>B</c> / <c>C</c> / <c>K</c> <c>BEGIN TRANSACTION</c>, <c>COMMIT</c> and
/// <c>ROLLBACK</c>, <c>&lt;</c> / <c>&gt;</c> / <c>^</c> their ENVCHANGEs,
/// <c>i</c> a message, <c>|</c> a result and <c>#</c> its DONE, <c>=n</c> the
/// return status, and anything else in braces.
/// </summary>
[TestClass]
public sealed class SystemProcedureStepTests
{
    private const string Setup = """
        create table z_t (id int primary key, v int); create index ix on z_t (v); create table lone (id int);
        create type ty from int; create application role ar with password = 'Zz!passw0rd1'
        """;

    [TestMethod]
    [DataRow("", "exec sp_tables @table_type = \"'TABLE'\"", "IIIISIISISIISSIII|#=0")]
    [DataRow("", "exec sp_tables @table_type = \"'VIEW'\"", "IIIISIISIISISSIII|#=0")]
    [DataRow("", "exec sp_tables 'z_t', @table_type = \"'TABLE','VIEW'\"", "IIIISIISISISIIISSSIII|#=0")]
    [DataRow("", "exec sp_tables 'z_x', @table_type = \"'TABLE'\"", "IIIISIISISIIIISSIII|#=0")]
    [DataRow("", "exec sp_tables 'z_t', 'dbo', @table_type = \"'TABLE'\"", "IIIISIISISIIISSIII|#=0")]
    [DataRow("", "exec sp_tables @table_type = \"TABLE\"", "IIIISIISIIISSIII|#=0")]
    [DataRow("", "exec sp_tables 'z_t', @table_type = \"'TABLE'\", @fUsePattern = 0", "IIIISIISISIIIISSSII|#=0")]
    [DataRow("", "exec sp_tables 'z_t'", "IIIISISIIISSSIII|#=0")]
    [DataRow("", "exec sp_tables @fUsePattern = 0", "IIIISISISSII|#=0")]
    [DataRow("", "exec sp_spaceused", "IIISIISI|#|#=0")]
    [DataRow("", "exec sp_spaceused 'z_t', 'true'", "IIISISIi|#=0")]
    [DataRow("", "exec sp_who", "SII|#S=0")]
    [DataRow("", "exec sp_who 'active'", "SI|#S=0")]
    [DataRow("", "exec sp_who @@spid", "SIIIS|#S=0")]
    [DataRow("", "exec sp_monitor", "IOISSSI|#|#|#|#=0")]
    [DataRow("", "exec sp_depends 'v'", "SISISi|#OS=0")]
    [DataRow("", "exec sp_depends 'lone'", "SISISiOS=0")]
    [DataRow("", "exec sp_droptype 'ty'", "SIISXS=0")]
    [DataRow("", "exec sp_refreshview 'v'", "SIISII<BTIIE>CSXS=0")]
    [DataRow("", "exec sp_autostats 'z_t'", "SISSISIIiPiPiPiPiPiP|#S=0")]
    [DataRow("", "exec sp_autostats 'z_t', 'OFF'", "SISSISII<BIIN(NININI)>CS=0")]
    [DataRow("", "exec sp_addmessage 77001, 16, 'm'", "IIIIISSSSSIIISI<BIIIIIISS>CS=0")]
    [DataRow("", "exec sp_addmessage 77001, 16, 'm', @with_log = 'true'", "IIIIISSSSSII<BIIIIIIS>CS=0")]
    [DataRow("exec sp_addmessage 77001, 16, 'm'", "exec sp_addmessage 77001, 16, 'n', @replace = 'replace'", "IIIIISSSSSIIISII<BIIIIIIISS>CS=0")]
    [DataRow("exec sp_addmessage 77001, 16, 'm'", "exec sp_altermessage 77001, 'WITH_LOG', 'true'", "SSIISI<BIS>CS=0")]
    [DataRow("exec sp_addmessage 77001, 16, 'm'", "exec sp_dropmessage 77001", "IIIISIIS<BIISI>CS=0")]
    [DataRow("exec sp_addmessage 77001, 16, 'm'", "exec sp_dropmessage 77001, 'all'", "IIIII<BIISI>CS=0")]
    [DataRow("", "exec sp_addumpdevice 'disk', 'd', '/x.bak'", "SOIISIIISXIII<BII>CS=0")]
    [DataRow("exec sp_addumpdevice 'disk', 'd', '/x.bak'", "exec sp_dropdevice 'd'", "SIOIIIIDIiRS=0")]
    [DataRow("", "exec sp_approlepassword 'ar', 'Zz!passw0rd2'", "IIOISSSS<BXI>CS=0")]
    [DataRow("", "exec sp_bindefault 'df', 'z_t.v'", "O<i>=0")]
    [DataRow("exec sp_bindefault 'df', 'z_t.v'", "exec sp_unbindefault 'z_t.v'", "OSSIIIISSIII<BISI>CiRS=0")]
    [DataRow("exec sp_bindrule 'ru', 'z_t.v'", "exec sp_unbindrule 'z_t.v'", "OSSIIIISSII<BISI>CiRIS=0")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_dropserver 'ls'", "SOIII<BIIISSIIIIXISIII>CS=0")]
    public void SystemProcedure_ClosesItsStatements(string arrange, string sql, string expected)
    {
        var simulation = new Simulation();
        simulation.AddRemoteSimulation("ls", new Simulation());
        using var fixture = new TdsSessionFixture(simulation);
        _ = fixture.RunBatch(Setup);
        _ = fixture.RunBatch("create view v as select id, v from z_t");
        _ = fixture.RunBatch("create default df as 0");
        _ = fixture.RunBatch("create rule ru as @x > 0");
        if (arrange.Length > 0)
            _ = fixture.RunBatch(arrange);
        var tokens = TdsSessionFixture.Describe(fixture.RunBatch(sql));
        Assert.AreEqual(expected, Letters(tokens), string.Join(" / ", tokens));
    }

    /// <summary>
    /// <c>sp_addlinkedserver</c>'s statements follow the product — a SQL
    /// Server one assigns its provider twice over — and its refusals: the
    /// product's and the transaction's ahead of its own transaction, the
    /// name's, the provider's and the style's inside it, rolled back, and a
    /// name that exists after the rollback; <c>sp_dropserver</c> refuses a
    /// missing server or one mapping logins after rolling its transaction
    /// back. Each refusal goes on to the procedure's last statement
    /// (captured 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("", "exec sp_addlinkedserver 'ls'", "SIIISOIII<BISXIISISSIIISXI>CS=0")]
    [DataRow("", "exec sp_addlinkedserver 'ls', '', 'SQLNCLI', 'host', @catalog = 'c'", "SIIIOIII<BISXIISISIIISXI>CS=0")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_addlinkedserver 'ls'", "SIIISOIII<BISXIISISSISXII^K{ERR 15028}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("exec sp_addlinkedserver 'ls', '', 'SQLNCLI'", "exec sp_addlinkedserver 'ls', '', 'SQLNCLI'", "SIIIOIII<BISXIISISISXII^K{ERR 15028}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addlinkedserver 'ls', '', 'MSDASQL'", "SIIIOIII<BISXIISI{ERR 7222}{DONEINPROC AA MORE|ERROR 0}SIIISXII^KS=1")]
    [DataRow("", "exec sp_addlinkedserver ''", "SIIISOIII<BII{ERR 15004}{DONEINPROC F6 MORE|ERROR 0}SXISXII^KS=1")]
    [DataRow("", "exec sp_addlinkedserver null, '', 'SQLNCLI'", "SIIIOIII<BII{ERR 15004}{DONEINPROC F6 MORE|ERROR 0}SXISXII^KS=1")]
    [DataRow("", "exec sp_addlinkedserver null, 'SQL Server', 'SQLNCLI'", "SII{ERR 15428}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addlinkedserver 'ls', @datasrc = 'x'", "SII{ERR 15426}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addlinkedserver 'ls', 'x'", "SIII{ERR 15427}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addlinkedserver 'ls', null, 'SQLNCLI'", "SIII{ERR 15429}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("begin tran", "exec sp_addlinkedserver 'ls'", "SIIISOI{ERR 15002}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addlinkedserver 'ls', '', 'SQLNCLI', @linkedstyle = 0", "SIIIOIII<BISXIISI{ERR 15663}^{DONE FD ERROR 0}")]
    [DataRow("", "exec sp_dropserver 'nope'", "SOIII<BI^K{ERR 15015}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("exec sp_addlinkedserver 'ls'; exec sp_addlinkedsrvlogin 'ls', 'false', 'sa', 'x', 'y'", "exec sp_dropserver 'ls'", "SOIII<BII^K{ERR 15190}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("exec sp_addlinkedserver 'ls'; begin tran", "exec sp_dropserver 'ls'", "SOI{ERR 15002}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    public void LinkedServerProcedures_CloseTheirStatementsAndRefusals(string arrange, string sql, string expected) =>
        this.SystemProcedure_ClosesItsStatements(arrange, sql, expected);

    /// <summary>
    /// The login-mapping pair refuses a caller's transaction, then the
    /// permission (and <c>sp_addlinkedsrvlogin</c> an <c>@useself</c> other
    /// than true or false) ahead of its own transaction, and a missing server
    /// or login inside it, rolled back; a self-mapping assigns once more ahead
    /// of the transaction, and a named login is looked up in it (captured
    /// 2026-10-18 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_addlinkedsrvlogin 'ls'", "OIISIIS<BISIISI>CS=0")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_addlinkedsrvlogin 'ls', 'false', null, 'ru', 'rp'", "OIISII<BISIISI>CS=0")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_addlinkedsrvlogin 'ls', 'true', 'sa'", "OIISIIS<BISIISIISI>CS=0")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_addlinkedsrvlogin 'ls', 'x'", "OIISI{ERR 15600}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addlinkedsrvlogin 'nope'", "OIISIIS<BI^K{ERR 15015}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_addlinkedsrvlogin 'ls', 'true', 'nobody'", "OIISIIS<BISIIII^K{ERR 15007}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("exec sp_addlinkedserver 'ls'; begin tran", "exec sp_addlinkedsrvlogin 'ls'", "OI{ERR 15002}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_droplinkedsrvlogin 'ls', null", "OII<BISII>CS=0")]
    [DataRow("exec sp_addlinkedserver 'ls'", "exec sp_droplinkedsrvlogin 'ls', 'sa'", "OII<BISIISII>CS=0")]
    [DataRow("", "exec sp_droplinkedsrvlogin 'nope', null", "OII<BI^K{ERR 15015}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    public void LinkedLoginProcedures_CloseTheirStatementsAndRefusals(string arrange, string sql, string expected) =>
        this.SystemProcedure_ClosesItsStatements(arrange, sql, expected);

    /// <summary>
    /// ODBC's enumerations through <c>sp_tables</c> — the databases under a
    /// qualifier of <c>%</c>, the owners under an owner of <c>%</c>, the
    /// table types under a type of <c>%</c>, the rest empty — each run their
    /// own conditions and return through a <c>RETURN</c>; an empty qualifier
    /// otherwise lists nothing after a condition and two assignments more
    /// (captured 2026-10-18 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("", "exec sp_tables '', '', '%'", "I|#U=0")]
    [DataRow("", "exec sp_tables '', '%', ''", "II|#U=0")]
    [DataRow("", "exec sp_tables '', '', '', '%'", "III|#U=0")]
    [DataRow("", "exec sp_tables 'z_t', 'dbo', ''", "IIIIIISSSISIISSIII|#=0")]
    public void SpTablesEnumerations_CloseTheirStatements(string arrange, string sql, string expected) =>
        this.SystemProcedure_ClosesItsStatements(arrange, sql, expected);

    /// <summary>
    /// A refusal a system procedure raises with a <c>RAISERROR</c> follows
    /// the statements ahead of its check and precedes those it goes on to;
    /// an ODBC catalog procedure's then returns -6, most of them through a
    /// <c>RETURN</c> (captured 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("", "exec sp_spaceused 'nosuch'", "IIISIISI{ERR 15009}{DONEINPROC F6 MORE|ERROR 0}=1")]
    [DataRow("", "exec sp_depends 'nosuch'", "SISIS{ERR 15009}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_droptype 'nosuch'", "SI{ERR 15036}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_autostats 'nosuch'", "SISSISI{ERR 15390}{DONEINPROC F6 MORE|ERROR 0}S=15390")]
    [DataRow("exec sp_addmessage 77001, 16, 'm'", "exec sp_addmessage 77001, 16, 'm'", "IIIIISSSSSIIISI<BIIIII>C{ERR 15043}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addmessage 1, 16, 'm'", "III{ERR 15040}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_dropmessage 77992", "IIIISIIS<BI>C{ERR 15179}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_altermessage 77992, 'WITH_LOG', 'true'", "SSIISI<BI>C{ERR 15179}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_addumpdevice 'nosuchtype', 'd', '/x.bak'", "SOIISI{ERR 15044}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_dropdevice 'nodev'", "SIOIII{ERR 15012}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_approlepassword 'nosuch', 'Zz!passw0rd2'", "IIOISSSS<B{ERR 15151}{DONEINPROC AA MORE|ERROR 0}XI^KS=1")]
    [DataRow("", "exec sp_unbindefault 'z_t.nosuch'", "OSSIIIISSII<BISII>C{ERR 15148}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_unbindefault 'z_t.v'", "OSSIIIISSII{ERR 15236}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_unbindrule 'z_t.v'", "OSSIIIISSII{ERR 15238}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_refreshview 'nosuch'", "SIISII{ERR 15165}{DONEINPROC F6 MORE|ERROR 0}SXS=15165")]
    [DataRow("", "exec sp_who 'nosuchlogin'", "SIIISI{ERR 15007}{DONEINPROC F6 MORE|ERROR 0}S=1")]
    [DataRow("", "exec sp_columns 'z_t', 'dbo', 'master'", "SISII{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_columns 'z_t', 'dbo', 'master', @ODBCVer = 3", "SIII{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_columns_90 'z_t', 'dbo', 'master'", "ISII{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_tables null, null, 'master'", "IIIIII{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_pkeys 'z_t', null, 'master'", "II{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_stored_procedures null, null, 'master'", "III{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_sproc_columns 'p', 'dbo', 'master'", "III{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_statistics 'z_t', 'dbo', 'master'", "{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}=-6")]
    [DataRow("", "exec sp_fkeys", "{ERR 15252}{DONEINPROC F6 MORE|ERROR 0}=-6")]
    [DataRow("", "exec sp_fkeys 'z_t', 'dbo', 'master'", "{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}=-6")]
    [DataRow("", "exec sp_table_privileges 'z_t', 'dbo', 'master'", "II{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_column_privileges 'z_t', 'dbo', 'master'", "ISII{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_special_columns 'z_t', 'dbo', 'master'", "IIISIII{ERR 15250}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_special_columns 'z_t', @col_type = 'Q'", "I{ERR 15251}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_special_columns 'z_t', @scope = 'Q'", "III{ERR 15251}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    [DataRow("", "exec sp_special_columns 'z_t', @nullable = 'Q'", "IIISI{ERR 15251}{DONEINPROC F6 MORE|ERROR 0}U=-6")]
    public void SystemProcedureRefusals_CloseTheStatementsAroundThem(string arrange, string sql, string expected) =>
        this.SystemProcedure_ClosesItsStatements(arrange, sql, expected);

    private static string Letters(List<string> tokens)
    {
        var letters = new System.Text.StringBuilder();
        var afterRows = false;
        foreach (var token in tokens)
        {
            _ = letters.Append(token switch
            {
                "DONEINPROC C0 MORE 0" => "I",
                "DONEINPROC C1 MORE|COUNT 1" when !afterRows => "S",
                "DONEINPROC C1 MORE 0" when !afterRows => "N",
                "DONEINPROC BA MORE 0" => "O",
                "DONEINPROC E0 MORE 0" => "X",
                "DONEINPROC F7 MORE 0" => "P",
                "DONEINPROC F6 MORE 0" => "R",
                "DONEINPROC E6 MORE 0" => "D",
                "DONEINPROC 15D MORE 0" => "T",
                "DONEINPROC 15F MORE 0" => "E",
                "DONEINPROC 20 MORE 0" => "(",
                "DONEINPROC 2C MORE 0" => ")",
                "DONEINPROC D4 MORE 0" => "B",
                "DONEINPROC D5 MORE 0" => "C",
                "DONEINPROC D2 MORE 0" => "K",
                "DONEINPROC DB MORE 0" => "U",
                "ENV8" => "<",
                "ENV9" => ">",
                "ENV10" => "^",
                "ROWS" => "|",
                _ when token.StartsWith("ORDER", StringComparison.Ordinal) => "",
                _ when token.StartsWith("INFO", StringComparison.Ordinal) => "i",
                _ when token.StartsWith("DONEINPROC C1", StringComparison.Ordinal) && afterRows => "#",
                _ when token.StartsWith("RET ", StringComparison.Ordinal) => "=" + token[4..],
                _ when token.StartsWith("DONEPROC", StringComparison.Ordinal) => "",
                _ => "{" + token + "}",
            });
            afterRows = token == "ROWS" || (afterRows && token.StartsWith("ORDER", StringComparison.Ordinal));
        }
        return letters.ToString();
    }
}
