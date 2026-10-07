using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One row of <c>sys.messages</c> the instance's user has registered with
/// <c>sp_addmessage</c>: a message id at or above 50001 in one language. An
/// <c>sp_altermessage</c> or a replacing <c>sp_addmessage</c> stores a new
/// instance rather than changing one, so a reader holding a row sees it whole.
/// </summary>
internal sealed class UserMessage(byte severity, bool isEventLogged, string text)
{
    public readonly byte Severity = severity;
    public readonly bool IsEventLogged = isEventLogged;
    public readonly string Text = text;
}

/// <summary>
/// A registered user message as a <c>RAISERROR</c> or <c>FORMATMESSAGE</c>
/// reads it in one session language: the us_english version, which supplies
/// the severity and, for a localized text, the specifiers its positional
/// placeholders name; and the text of the session's own language — a language
/// the message has no version in reads as empty, where real doesn't fall back
/// to us_english (probed 2026-09-30 against SQL Server 2025).
/// </summary>
internal sealed class RegisteredMessage(int messageId, UserMessage usEnglish, string text, bool localized)
{
    public readonly int MessageId = messageId;
    public readonly UserMessage UsEnglish = usEnglish;
    public readonly string Text = text;
    public readonly bool Localized = localized;

    public byte Severity => this.UsEnglish.Severity;

    public static RegisteredMessage? Find(Simulation simulation, int messageId, short languageId)
    {
        if (!simulation.UserMessages.TryGetValue((messageId, Simulation.UsEnglishMessageLanguageId), out var usEnglish))
            return null;
        if (languageId == Simulation.UsEnglishMessageLanguageId)
            return new(messageId, usEnglish, usEnglish.Text, localized: false);
        return new(messageId, usEnglish, simulation.UserMessages.TryGetValue((messageId, languageId), out var row) ? row.Text : string.Empty, localized: true);
    }

    /// <summary>
    /// Renders for <c>RAISERROR</c>: the text as an ordinary format string in
    /// us_english, and in another language its positional placeholders read
    /// against the us_english specifiers, with every argument judged against
    /// those specifiers' types first. Raises Msg 2786 / 2787 as the format
    /// string form does, the placeholder misfits at state 2. A text with no
    /// characters reads as one space.
    /// </summary>
    public string Format(List<SqlValue> arguments)
    {
        if (this.Text.Length == 0)
            return " ";
        if (!this.Localized)
            return MessageFormatter.Format(this.Text, arguments);
        if (!MessageFormatter.TryLocalize(MessageFormatter.SplitSpecifiers(this.UsEnglish.Text), this.Text, out var format, out var order, out var invalid))
            throw SimulatedSqlException.RaiserrorInvalidLocalizedFormatSpec(invalid);
        _ = MessageFormatter.Format(this.UsEnglish.Text, arguments);
        return MessageFormatter.Format(format, Permute(arguments, order));
    }

    /// <summary>
    /// For <c>FORMATMESSAGE</c>: the ordinary format string and arguments a
    /// localized text stands for, or false when its placeholders don't fit.
    /// </summary>
    public bool TryLocalize(SqlValue[] arguments, out string format, out SqlValue[] permuted)
    {
        permuted = arguments;
        if (!MessageFormatter.TryLocalize(MessageFormatter.SplitSpecifiers(this.UsEnglish.Text), this.Text, out format, out var order, out _))
            return false;
        permuted = [.. Permute([.. arguments], order)];
        return true;
    }

    private static List<SqlValue> Permute(List<SqlValue> arguments, List<int> order) =>
        order.ConvertAll(position => position < arguments.Count ? arguments[position] : SqlValue.Null(SqlType.Int32));
}

partial class Simulation
{
    /// <summary>The <c>msglangid</c> of <c>us_english</c>, the language every user message needs a version in.</summary>
    internal const short UsEnglishMessageLanguageId = 1033;

    /// <summary>
    /// The registered user messages, keyed by (message id, language id).
    /// Server-scope state: <c>sp_addmessage</c> from any database or session
    /// registers here and every session's <c>RAISERROR</c> and
    /// <c>FORMATMESSAGE</c> read it. System messages (id below 50000) aren't
    /// carried.
    /// </summary>
    internal readonly ConcurrentDictionary<(int MessageId, short LanguageId), UserMessage> UserMessages = new();

    private static readonly SystemProcedureParameter[] AddMessageParameters =
    [
        new("msgnum", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
        new("severity", SqlType.SmallInt, 0, SqlValue.Null(SqlType.SmallInt)),
        new("msgtext", SqlType.NVarchar, 255, SqlValue.Null(SqlType.NVarchar)),
        new("lang", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("with_log", SqlType.NVarchar, 5, SqlValue.Null(SqlType.NVarchar)),
        new("replace", SqlType.NVarchar, 7, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] DropMessageParameters =
    [
        new("msgnum", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
        new("lang", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] AlterMessageParameters =
    [
        new("message_id", SqlType.Int32),
        new("parameter", SqlType.NVarchar, 128),
        new("parameter_value", SqlType.NVarchar, 5),
    ];

    /// <summary>
    /// Whether the session's login is a member of <c>sysadmin</c> or the fixed
    /// server role <paramref name="roleId"/> — the role gate the message
    /// procedures apply, which a granted server permission (<c>CONTROL
    /// SERVER</c>, <c>ALTER SETTINGS</c>) doesn't satisfy (probed 2026-09-30
    /// against SQL Server 2025). An identity minted inside one database has no
    /// server role; the empty-registry dev mode holds every one.
    /// </summary>
    private static bool SessionInServerRole(BatchContext batch, int roleId)
    {
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        var effective = connection.Security.Effective;
        return !effective.IsDatabaseScoped
            && (simulation.Logins.IsEmptyLockFree()
                || simulation.IsLoginSysadmin(effective.LoginName)
                || simulation.IsLoginInServerRole(effective.LoginName, roleId));
    }

    /// <summary>
    /// Raises <paramref name="error"/> as a system procedure's own
    /// <c>RAISERROR</c> from <paramref name="line"/> of its source, which real
    /// reports under the name the call used (<paramref name="calledAs"/>).
    /// </summary>
    private static SimulatedSqlException AtSystemProcedureLine(string calledAs, SimulatedSqlException error, int line, int returnCode = 1)
    {
        error.SystemProcedureReturnCode = returnCode;
        // A class 15 error a procedure raises from its own body ends the procedure and
        // leaves the batch that called it running (probed 2026-09-30).
        error.EndedCalledBatch = true;
        return AtProcedureLine(error, calledAs, line);
    }

    /// <summary>Records the user messages to restore should the transaction roll back.</summary>
    private static void RecordMessageUndo(BatchContext batch)
    {
        if (batch.Connection.CurrentTransaction is null)
            return;
        var simulation = batch.Connection.Simulation;
        var snapshot = simulation.UserMessages.ToArray();
        RecordDdlUndo(batch, () =>
        {
            simulation.UserMessages.Clear();
            foreach (var (key, message) in snapshot)
                simulation.UserMessages[key] = message;
        });
    }

    /// <summary>
    /// The message language a <c>@lang</c> argument names, or null for a name
    /// that is no official language name or alias. A language's message id is
    /// its <c>msglangid</c>, so <c>British</c> shares <c>us_english</c>'s.
    /// </summary>
    private static Language? FindMessageLanguage(string name) => Language.Find(name.TrimEnd(' '));

    /// <summary>
    /// <c>sp_addmessage @msgnum, @severity, @msgtext [, @lang [, @with_log [, @replace]]]</c>
    /// registers a message in <c>sys.messages</c>. Real checks in this order,
    /// each refusal raised from its own line of the procedure's source
    /// (probed 2026-09-30 against SQL Server 2025): the caller's role, a NULL
    /// argument, the id, the severity, the language, <c>@with_log</c>,
    /// <c>@replace</c>, the us_english version's existence, its severity, and
    /// an existing row. A localized version needs the us_english one first
    /// and its severity. An omitted or NULL <c>@lang</c> is the session's
    /// language, not us_english (probed 2026-10-07).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAddMessage(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_addmessage";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, AddMessageParameters);
        if (!SessionInServerRole(batch, ServerAdminRoleId))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 18);
        if (values[0].IsNull || values[1].IsNull || values[2].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.AddMessageUsage(), 24);
        var messageId = values[0].AsInt32;
        if (messageId <= 50000)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserMessageIdTooLow(), 31);
        var severity = values[1].AsInt16;
        if (severity is < 1 or > 25)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserMessageSeverityOutOfRange(), 38);
        var languageName = values[3].IsNull ? batch.Connection.Language.Name : values[3].AsString;
        var language = FindMessageLanguage(languageName)
            ?? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAnOfficialLanguageName(languageName), 49);

        var withLogText = values[4].IsNull ? "false" : values[4].AsString.TrimEnd(' ');
        var withLog = withLogText.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!withLog && !withLogText.Equals("false", StringComparison.OrdinalIgnoreCase))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidWithLogValue(), 73);
        var replace = false;
        if (!values[5].IsNull)
        {
            replace = values[5].AsString.TrimEnd(' ').Equals("REPLACE", StringComparison.OrdinalIgnoreCase);
            if (!replace)
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.MessageReplaceRequired(), 84);
        }

        var simulation = batch.Connection.Simulation;
        var languageId = language.MsgLangId;
        UserMessage? usEnglish = null;
        var isUsEnglish = language.LangId == 0;
        if (!isUsEnglish)
        {
            if (!simulation.UserMessages.TryGetValue((messageId, UsEnglishMessageLanguageId), out usEnglish))
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UsEnglishMessageRequired(languageName), 97);
            if (usEnglish.Severity != severity)
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.LocalizedMessageSeverityMismatch(languageName, usEnglish.Severity), 107);
            if (!values[4].IsNull)
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, calledAs, 113, 15042, "The @with_log parameter is ignored for messages that are not us_english version."));
        }
        if (!replace && simulation.UserMessages.ContainsKey((messageId, languageId)))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.MessageReplaceRequired(), 133);

        RecordMessageUndo(batch);
        // Severity and event logging belong to the message, not to a language:
        // a localized version takes the us_english one's, and a new us_english
        // version moves every language's to its own.
        simulation.UserMessages[(messageId, languageId)] = new UserMessage((byte)severity, isUsEnglish ? withLog : usEnglish!.IsEventLogged, values[2].AsString);
        if (isUsEnglish)
        {
            foreach (var (key, other) in simulation.UserMessages)
            {
                if (key.MessageId == messageId && key.LanguageId != languageId)
                    simulation.UserMessages[key] = new UserMessage((byte)severity, withLog, other.Text);
            }
        }
        yield break;
    }

    /// <summary>
    /// <c>sp_dropmessage @msgnum [, @lang]</c> removes one language's version
    /// of a user message, or every version with <c>'ALL'</c>; the us_english
    /// version goes last. A message id of 50000 or less can't be dropped. An
    /// omitted or NULL <c>@lang</c> is the session's language.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDropMessage(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_dropmessage";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, DropMessageParameters);
        if (!SessionInServerRole(batch, ServerAdminRoleId))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 11);
        if (values[0].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.DropMessageUsage(), 18);
        var messageId = values[0].AsInt32;
        if (messageId <= 50000)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CannotDropSystemMessage(), 25);

        var languageName = values[1].IsNull ? batch.Connection.Language.Name : values[1].AsString;
        var all = languageName.TrimEnd(' ').Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var language = all ? null : FindMessageLanguage(languageName)
            ?? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAnOfficialLanguageName(languageName), 38);

        var simulation = batch.Connection.Simulation;
        var languageIds = new List<short>();
        foreach (var ((id, languageId), _) in simulation.UserMessages)
        {
            if (id == messageId && (all || languageId == language!.MsgLangId))
                languageIds.Add(languageId);
        }
        if (languageIds.Count == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.MessageDoesNotExist(messageId), 54);
        if (!all && languageIds.Contains(UsEnglishMessageLanguageId))
        {
            foreach (var ((id, languageId), _) in simulation.UserMessages)
            {
                if (id == messageId && languageId != UsEnglishMessageLanguageId)
                    throw AtSystemProcedureLine(calledAs, SimulatedSqlException.LocalizedMessagesRemain(), 62);
            }
        }

        RecordMessageUndo(batch);
        foreach (var languageId in languageIds)
            _ = simulation.UserMessages.TryRemove((messageId, languageId), out _);
        yield break;
    }

    /// <summary>
    /// <c>sp_altermessage @message_id, @parameter, @parameter_value</c> turns
    /// a message's event logging on or off for every language version. The one
    /// parameter is <c>WITH_LOG</c>, which a NULL <c>@parameter</c> also means.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAlterMessage(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_altermessage";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, AlterMessageParameters);
        if (!SessionInServerRole(batch, ServerAdminRoleId))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 14);
        if (!values[1].IsNull && !values[1].AsString.TrimEnd(' ').Equals("WITH_LOG", StringComparison.OrdinalIgnoreCase))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.AlterMessageParameterInvalid(), 21);
        var valueText = values[2].IsNull ? "" : values[2].AsString.TrimEnd(' ');
        var logged = valueText.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!logged && !valueText.Equals("false", StringComparison.OrdinalIgnoreCase))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.AlterMessageValueInvalid(), 32);
        if (values[0].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.AlterMessageNullId(), 38);
        var messageId = values[0].AsInt32;

        var simulation = batch.Connection.Simulation;
        var keys = new List<(int MessageId, short LanguageId)>();
        foreach (var (key, _) in simulation.UserMessages)
        {
            if (key.MessageId == messageId)
                keys.Add(key);
        }
        if (keys.Count == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.MessageDoesNotExist(messageId), 42);

        RecordMessageUndo(batch);
        foreach (var key in keys)
        {
            if (simulation.UserMessages.TryGetValue(key, out var message))
                simulation.UserMessages[key] = new UserMessage(message.Severity, logged, message.Text);
        }
    }
}
