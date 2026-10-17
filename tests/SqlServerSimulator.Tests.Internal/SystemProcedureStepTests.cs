namespace SqlServerSimulator;

/// <summary>
/// The statements real SQL Server 2025's system procedures run without
/// <c>NOCOUNT</c> around their work, each closing in a DONEINPROC of its kind,
/// and the transactions some open — captured 2026-10-17 with a raw TDS client.
/// A response reads as letters: <c>I</c> a condition, <c>S</c> an assignment
/// counting its row, <c>N</c> a count-less SELECT-kind statement (a cursor's
/// <c>DECLARE</c> or <c>FETCH</c>), <c>O</c> a <c>SET … OFF</c>, <c>X</c> an
/// <c>EXEC</c>, <c>P</c> a <c>PRINT</c>, <c>R</c> a <c>RAISERROR</c>,
/// <c>D</c> a <c>DBCC</c>, <c>T</c> / <c>E</c> a <c>TRY</c>'s start and
/// end, <c>(</c> / <c>)</c> a cursor's <c>OPEN</c> and <c>DEALLOCATE</c>,
/// <c>B</c> / <c>C</c> <c>BEGIN TRANSACTION</c> and <c>COMMIT</c>, <c>&lt;</c>
/// / <c>&gt;</c> their ENVCHANGEs, <c>i</c> a message, <c>|</c> a result and
/// <c>#</c> its DONE, <c>=n</c> the return status.
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
                "ENV8" => "<",
                "ENV9" => ">",
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
