using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SystemProcedureParameter[] DbCmptLevelParameters =
    [
        new("dbname", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("new_cmptlevel", SqlType.TinyInt, 0, SqlValue.Null(SqlType.TinyInt)),
    ];

    private static readonly SystemProcedureParameter[] RenameDbParameters =
    [
        new("dbname", SqlType.NVarchar, 128),
        new("newname", SqlType.NVarchar, 128),
    ];

    /// <summary>
    /// <c>sp_dbcmptlevel [@dbname [, @new_cmptlevel]]</c>, the pre-2005 spelling
    /// of <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL</c> old install scripts
    /// call: with no database it lists the valid levels (Msg 15048 at class 0),
    /// with no level it reports the current one (Msg 15054), and a missing
    /// database is Msg 15010, a level outside 100 through 170 Msg 15416 and a
    /// call inside a transaction Msg 15002 — each from real's line in the
    /// procedure and returning 1 (probed 2026-10-15 against SQL Server 2025).
    /// Real's procedure goes on past the first two, sending a blank line and
    /// then the database list or the valid levels, which aren't sent here:
    /// a system procedure's error ends it.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDbCmptLevel(BatchContext batch, string calledAs, string? returnCode)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_dbcmptlevel", calledAs, arguments, DbCmptLevelParameters);
        if (values[0].IsNull)
        {
            QueueProcedureMessage(batch, calledAs, 15048, SimulatedSqlException.InvalidCompatibilityLevel().Message, 42);
            SetProcedureReturnCode(batch, returnCode, 0);
            yield break;
        }

        var name = values[0].AsString;
        if (!this.Databases.TryGetValue(name, out var database))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpDatabaseDoesNotExist(name), 54);

        if (values[1].IsNull)
        {
            QueueProcedureMessage(batch, calledAs, 15054, $"The current compatibility level is {(int)database.CompatibilityLevel}.", 69);
            SetProcedureReturnCode(batch, returnCode, 0);
            yield break;
        }

        var level = values[1].CoerceTo(SqlType.TinyInt).AsByte;
        if (level is not (100 or 110 or 120 or 130 or 140 or 150 or 160 or 170))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.DbCmptLevelUsage(), 77);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction("sp_dbcmptlevel"), 97);
        foreach (var outcome in this.RunStatements(batch, [$"alter database {QuoteIdentifier(name)} set compatibility_level = {level}"]))
            yield return outcome;
        SetProcedureReturnCode(batch, returnCode, 0);
    }

    /// <summary>
    /// <c>sp_renamedb @dbname, @newname</c>: <c>sp_rename</c>'s database form
    /// — the rename <c>ALTER DATABASE … MODIFY NAME</c> makes, with its Msg
    /// 5021 notice — attributing its own refusals to the procedure as called.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpRenameDb(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_renamedb", calledAs, arguments, RenameDbParameters);
        RenameDatabase(batch, RequireValidName(values[0]), RequireValidName(values[1]), calledAs);
    }

    /// <summary>An informational message a procedure sends from its own line.</summary>
    private static void QueueProcedureMessage(BatchContext batch, string calledAs, int number, string message, int line)
    {
        var info = batch.InfoMessage(@class: 10, state: 1, number: number, message: message);
        info.LineNumber = line;
        info.Procedure = calledAs;
        batch.Connection.PendingMessages.Enqueue(info);
    }
}
