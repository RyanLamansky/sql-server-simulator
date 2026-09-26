using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A system procedure's own errors and messages, attributed as real
/// attributes them: to the procedure by the name it was called by, at the
/// line of real's source that raises them. Every expectation probed
/// 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SystemProcedureDiagnosticsTests
{
    [TestMethod]
    [DataRow("exec sp_help 'nope'", 15009, "sp_help", 79)]
    [DataRow("exec sys.sp_helptext 'nope'", 15009, "sys.sp_helptext", 54)]
    [DataRow("exec sp_helptext 't'", 15197, "sp_helptext", 107)]
    [DataRow("exec sp_spaceused 'nope'", 15009, "sp_spaceused", 153)]
    [DataRow("exec sp_rename 't', 'u', 'COLUMN'", 15248, "sp_rename", 269)]
    [DataRow("exec sp_refreshview 'nope'", 15165, "sys.sp_refreshsqlmodule_internal", 62)]
    [DataRow("exec sp_columns", 201, "sp_columns", 0)]
    [DataRow("exec sp_pkeys @table_owner = 'dbo'", 201, "sp_pkeys", 0)]
    [DataRow("exec sp_addrolemember 'nope', 'x'", 15410, "sp_addrolemember", 35)]
    [DataRow("exec sp_releaseapplock 'r'", 3918, "sys.xp_userlock", 1)]
    [DataRow("exec sp_getapplock @LockMode = 'Exclusive', @LockOwner = 'Session'", 1224, "sys.xp_userlock", 1)]
    public void Errors_NameTheProcedureAndItsLine(string sql, int number, string procedure, int line)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key)");
        var error = simulation.AssertSqlError(sql, number).Errors[0];
        AreEqual(procedure, error.Procedure);
        AreEqual(line, error.LineNumber);
    }

    [TestMethod]
    public void AnArgumentError_StaysTheBatchs()
        => AreEqual("", new Simulation().AssertSqlError("exec sp_help @nope", 137).Errors[0].Procedure);

    [TestMethod]
    [DataRow("exec @r = sp_getapplock 'r', 'bogus'", "15625@sp_getapplock:26 Option 'bogus' not recognized for '@LockMode' parameter.")]
    [DataRow("exec @r = sp_getapplock 'r', 'Exclusive', 'Bogus'", "15625@sp_getapplock:39 Option 'Bogus' not recognized for '@LockOwner' parameter.")]
    [DataRow("exec @r = sp_getapplock 'r', 'Exclusive'", "15626@sp_getapplock:52 You attempted to acquire a transactional application lock without an active transaction.")]
    [DataRow("exec @r = sp_releaseapplock 'r', 'Bogus'", "15625@sp_releaseapplock:20 Option 'Bogus' not recognized for '@LockOwner' parameter.")]
    public void AppLockValidation_PrintsItsMessage_AndAnswersMinus999(string exec, string expected)
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add($"{error.Number}@{error.Procedure}:{error.LineNumber} {error.Message}");
        };
        using var command = connection.CreateCommand();
        command.CommandText = $"declare @r int; {exec}; select @r";
        AreEqual(-999, command.ExecuteScalar());
        AreEqual(expected, string.Join(" | ", messages));
    }

    [TestMethod]
    public void ReleaseApplock_OfANotHeldLock_StillAnswersMinus999()
        => AreEqual(-999, new Simulation().ExecuteScalar(
            "declare @r int; begin try exec @r = sp_releaseapplock 'r', 'Session'; end try begin catch end catch; select @r"));
}
