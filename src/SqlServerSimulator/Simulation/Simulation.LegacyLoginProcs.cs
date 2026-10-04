using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_addlogin / sp_droplogin / sp_password / sp_defaultdb / sp_defaultlanguage —
// the pre-DDL spellings of login management. Each checks what real's procedure
// checks itself, then runs the CREATE / DROP / ALTER LOGIN statement real builds,
// so what the statement refuses (a duplicate name, a missing database, a weak
// password) comes back from it at line 1 in no procedure.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] AddLoginParameters =
    [
        new("loginame", SqlType.NVarchar, 128),
        new("passwd", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("defdb", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("deflanguage", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("sid", SqlType.NVarchar, 32, SqlValue.Null(SqlType.NVarchar)),
        new("encryptopt", SqlType.NVarchar, 20, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] LoginNameParameter =
    [
        new("loginame", SqlType.NVarchar, 128),
    ];

    private static readonly SystemProcedureParameter[] DefaultDatabaseParameters =
    [
        new("loginame", SqlType.NVarchar, 128),
        new("defdb", SqlType.NVarchar, 128),
    ];

    private static readonly SystemProcedureParameter[] DefaultLanguageParameters =
    [
        new("loginame", SqlType.NVarchar, 128),
        new("language", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] PasswordParameters =
    [
        new("old", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("new", SqlType.NVarchar, 128),
        new("loginame", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    /// <summary>
    /// Real's <c>sys.sp_validname</c>: a NULL or empty name is Msg 15004, raised
    /// from that procedure's line 17 whichever procedure called it.
    /// </summary>
    private static string RequireValidName(SqlValue name)
    {
        if (name.IsNull || name.AsString.Length == 0)
            throw AtProcedureLine(SimulatedSqlException.NameCannotBeNull(), "sys.sp_validname", 17);
        return name.AsString;
    }

    private static string QuoteText(string text) => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static bool IsRegisteredLogin(BatchContext batch, string name) =>
        batch.Connection.Simulation.Logins.ContainsKey(name) || BuiltInToken.Comparer.Equals(name, "sa");

    /// <summary>
    /// <c>sp_addlogin @loginame [, @passwd [, @defdb [, @deflanguage [, @sid [, @encryptopt]]]]]</c>
    /// is <c>CREATE LOGIN … WITH PASSWORD, DEFAULT_DATABASE, DEFAULT_LANGUAGE</c> with
    /// <c>master</c> and us_english the defaults. <c>@encryptopt</c> may only be NULL: the
    /// skip options hand the statement a password that isn't a hash (Msg 15021), and
    /// anything else is Msg 15600. <c>@sid</c> is read and dropped — a login's SID is
    /// derived from its name here.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddLogin(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_addlogin", calledAs, arguments, AddLoginParameters);
        var name = RequireValidName(values[0]);
        if (!values[5].IsNull)
        {
            var option = values[5].AsString.TrimEnd(' ');
            if (option.Equals("skip_encryption", StringComparison.OrdinalIgnoreCase) || option.Equals("skip_encryption_old", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.PasswordParameterInvalid().PinLine(1);
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption("sp_addlogin"), 48, 15600);
        }

        var statement = new StringBuilder("create login ").Append(QuoteIdentifier(name)).Append(" with password = ")
            .Append(QuoteText(values[1].IsNull ? "" : values[1].AsString))
            .Append(", default_database = ").Append(QuoteIdentifier(values[2].IsNull ? "master" : values[2].AsString))
            .Append(", default_language = ").Append(QuoteIdentifier(values[3].IsNull ? "us_english" : values[3].AsString)).ToString();
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_droplogin @loginame</c> is <c>DROP LOGIN</c> for a name that is a login — a
    /// role or a missing name is Msg 15007 from the procedure's line 26 — with
    /// <c>sa</c> refused by the statement.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDropLogin(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_droplogin", calledAs, arguments, LoginNameParameter);
        var name = RequireValidName(values[0]);
        if (!IsRegisteredLogin(batch, name))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAValidLogin(name), 26);
        foreach (var outcome in this.ExecuteDynamicBatch(batch, "drop login " + QuoteIdentifier(name), preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_defaultdb @loginame, @defdb</c> is <c>ALTER LOGIN … WITH DEFAULT_DATABASE</c>;
    /// a NULL database is Msg 15010 and an unknown principal Msg 15007 from the
    /// procedure, and a role it finds is the statement's Msg 15405.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDefaultDb(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_defaultdb", calledAs, arguments, DefaultDatabaseParameters);
        var name = RequireValidName(values[0]);
        if (values[1].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.DefaultDatabaseDoesNotExist("(null)"), 26);
        if (!batch.Connection.Simulation.TryResolveServerPrincipalId(name, out _))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAValidLogin(name), 41);
        var statement = "alter login " + QuoteIdentifier(name) + " with default_database = " + QuoteIdentifier(values[1].AsString);
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_defaultlanguage @loginame [, @language]</c> is <c>ALTER LOGIN … WITH
    /// DEFAULT_LANGUAGE</c>, us_english when the language is NULL.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDefaultLanguage(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_defaultlanguage", calledAs, arguments, DefaultLanguageParameters);
        var name = RequireValidName(values[0]);
        if (!batch.Connection.Simulation.TryResolveServerPrincipalId(name, out _))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAValidLogin(name), 34);
        var statement = "alter login " + QuoteIdentifier(name) + " with default_language = " + QuoteIdentifier(values[1].IsNull ? "us_english" : values[1].AsString);
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_password [@old], @new [, @loginame]</c> is <c>ALTER LOGIN … WITH PASSWORD</c>,
    /// with <c>OLD_PASSWORD</c> when <c>@old</c> is given; a login it names that doesn't exist
    /// is Msg 15007 from the procedure's line 29. Without <c>@loginame</c> it changes the
    /// session's own login.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpPassword(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_password", calledAs, arguments, PasswordParameters);
        var name = values[2].IsNull ? batch.Connection.Security.Effective.LoginName : values[2].AsString;
        if (!values[2].IsNull && !IsRegisteredLogin(batch, name))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAValidLogin(name), 29);
        var statement = new StringBuilder("alter login ").Append(QuoteIdentifier(name)).Append(" with password = ")
            .Append(QuoteText(values[1].IsNull ? "" : values[1].AsString));
        if (!values[0].IsNull)
            _ = statement.Append(" old_password = ").Append(QuoteText(values[0].AsString));
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement.ToString(), preDeclaredVariables: null))
            yield return outcome;
    }
}
