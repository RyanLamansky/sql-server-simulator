using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_send_dbmail, the queue procedures (start, stop, status, queue), the
// deletion of mail items and log rows, and what Database Mail's external
// program makes of a queued item, as SQL Server 2025 on Linux has them
// (probed 2026-10-16).
partial class Simulation
{
    /// <summary>
    /// Whether <c>sysmail_stop_sp</c> has stopped the mail queue, which
    /// <c>sysmail_start_sp</c> undoes. Server-scoped, like the broker queue it
    /// stands for, and outside any transaction.
    /// </summary>
    private bool databaseMailStopped;

    /// <summary>
    /// Whether Database Mail's external program is running: started by the
    /// first item it takes, ended by <c>sysmail_stop_sp</c>, each logging its
    /// start or shutdown.
    /// </summary>
    private bool databaseMailProcessRunning;

    /// <summary>
    /// The process id the external program's log rows carry. Real's is the
    /// operating system's; any fixed number stands in for it.
    /// </summary>
    private const int DatabaseMailProcessId = 1188;

    private static readonly SystemProcedureParameter[] SpSendDbMailParameters =
    [
        AgentParameter("profile_name", AgentName, 128),
        AgentParameter("recipients", AgentVarchar),
        AgentParameter("copy_recipients", AgentVarchar),
        AgentParameter("blind_copy_recipients", AgentVarchar),
        AgentParameter("subject", AgentText, 255),
        AgentParameter("body", AgentText),
        AgentParameter("body_format", AgentVarchar, 20),
        AgentParameter("importance", AgentVarchar, 6),
        AgentParameter("sensitivity", AgentVarchar, 12),
        AgentParameter("file_attachments", AgentText),
        AgentParameter("query", AgentText),
        AgentParameter("execute_query_database", AgentName, 128),
        AgentParameter("attach_query_result_as_file", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("query_attachment_filename", AgentText, 260),
        AgentParameter("query_result_header", SqlType.Bit, SqlValue.FromBoolean(true)),
        AgentParameter("query_result_width", SqlType.Int32, SqlValue.FromInt32(256)),
        new("query_result_separator", AgentVarchar, 1, SqlValue.FromNVarchar(" ")),
        AgentParameter("exclude_query_output", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("append_query_error", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("query_no_truncate", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("query_result_no_padding", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("mailitem_id", SqlType.Int32),
        AgentParameter("from_address", AgentVarchar),
        AgentParameter("reply_to", AgentVarchar),
    ];

    /// <summary>
    /// The switches <c>sp_send_dbmail</c> refuses as NULL (Msg 14618,
    /// answering 2), in its order, by parameter index and the line of real's
    /// source that refuses each.
    /// </summary>
    private static readonly (int Index, string Name, int Line)[] SpSendDbMailRequiredSwitches =
    [
        (12, "attach_query_result_as_file", 151),
        (17, "exclude_query_output", 158),
        (14, "query_result_header", 165),
        (16, "query_result_separator", 172),
        (18, "append_query_error", 179),
    ];

    /// <summary>
    /// <c>sp_send_dbmail</c>: refused while <c>Database Mail XPs</c> is off
    /// (Msg 15281) or the queue is stopped (14641); then the profile — the
    /// one named, which a caller outside <c>sysadmin</c> may use only when it
    /// is public or granted to it (14607), else the caller's default private
    /// profile, else the default public one (14636) — and the arguments in
    /// real's order, each with its own return code. The attachments are read
    /// through <see cref="OpenBulkFile"/> and the query runs as the caller,
    /// its output formatted as <c>sqlcmd</c> would into the body or an
    /// attached file; a refusal of either is an extended procedure's Msg
    /// 22050 / 22051, sent past any <c>TRY</c>. The item queued, the external
    /// program's attempt to deliver it fails at once (see
    /// <see cref="DeliverQueuedMail"/>), and <c>Mail (Id: n) queued.</c> is
    /// printed unless <c>@exclude_query_output = 1</c>.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpSendDbMail(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sp_send_dbmail", SpSendDbMailParameters, userRole: true) is not { } call)
            yield break;
        this.RequireDatabaseMailXps("sp_send_dbmail");
        if (this.databaseMailStopped)
            throw MailRefusal(SimulatedSqlException.MailStopped(), 81, 1);
        var profileId = this.ResolveSendProfile(batch, call.Text(0));
        foreach (var (index, name, line) in SpSendDbMailRequiredSwitches)
        {
            if (call.IsNull(index))
                throw MailRefusal(SimulatedSqlException.MailParameterCannotBeNull(name), line, 2);
        }
        var bodyFormat = (call.Text(6) ?? "TEXT").ToUpperInvariant();
        if (bodyFormat is not ("TEXT" or "HTML"))
            throw MailRefusal(SimulatedSqlException.MailFormatInvalid(bodyFormat), 194, 13);
        var importance = (call.Text(7) ?? "NORMAL").ToUpperInvariant();
        if (importance is not ("LOW" or "NORMAL" or "HIGH"))
            throw MailRefusal(SimulatedSqlException.MailImportanceInvalid(importance), 211, 16);
        var sensitivity = (call.Text(8) ?? "NORMAL").ToUpperInvariant();
        if (sensitivity is not ("NORMAL" or "PERSONAL" or "PRIVATE" or "CONFIDENTIAL"))
            throw MailRefusal(SimulatedSqlException.MailSensitivityInvalid(sensitivity), 227, 18);
        if (string.IsNullOrEmpty(call.Text(5)) && string.IsNullOrEmpty(call.Text(10)) && string.IsNullOrEmpty(call.Text(9)) && string.IsNullOrEmpty(call.Text(4)))
            throw MailRefusal(SimulatedSqlException.MailAtLeastOneOf("@body, @query, @file_attachments, @subject"), 242, 19);
        if (string.IsNullOrEmpty(call.Text(1)) && string.IsNullOrEmpty(call.Text(2)) && string.IsNullOrEmpty(call.Text(3)))
            throw MailRefusal(SimulatedSqlException.MailAtLeastOneOf("@recipients, @copy_recipients, @blind_copy_recipients"), 260, 20);

        var maxFileSize = this.MailIntSetting(batch, "MaxFileSize", 1000000);
        var attachments = new List<(string Name, byte[] Content)>();
        if (call.Text(9) is { Length: > 0 } files)
        {
            var prohibited = (this.MailSetting(batch, "ProhibitedExtensions") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var extension = Path.GetExtension(path).TrimStart('.');
                if (Array.Exists(prohibited, entry => string.Equals(entry, extension, StringComparison.OrdinalIgnoreCase)))
                    throw MailRefusal(SimulatedSqlException.MailExtensionProhibited(path), 62, 1, "sp_GetAttachmentData");
                var content = this.ReadBulkFile(path);
                var refusal = content is null ? SimulatedSqlException.MailAttachmentInvalid(path)
                    : !AgentIsSysadmin(batch) ? SimulatedSqlException.MailAttachmentNeedsIntegratedLogin()
                    : content.Length > maxFileSize ? SimulatedSqlException.MailAttachmentTooLarge(maxFileSize)
                    : null;
                if (refusal is not null)
                {
                    AgentReturn(batch, returnCodeVar, 1);
                    yield return ExtendedProcedureRefusal(batch, refusal);
                    yield break;
                }
                attachments.Add((path, content!));
            }
        }

        var body = call.Text(5);
        if (call.Text(10) is { } query)
        {
            var output = call.Int(15) is < 9 ? null : this.RunMailQuery(batch, query, call);
            if (output is null)
            {
                AgentReturn(batch, returnCodeVar, 102);
                yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.MailQueryFailed(call.Int(15) is < 9 ? -2147024809 : -2147467259));
                yield break;
            }
            if (call.Int(12) == 1)
            {
                var content = new byte[2 + Encoding.Unicode.GetByteCount(output)];
                content[0] = 0xFF;
                content[1] = 0xFE;
                _ = Encoding.Unicode.GetBytes(output, 0, output.Length, content, 2);
                if (content.Length > maxFileSize)
                {
                    AgentReturn(batch, returnCodeVar, 1);
                    yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.MailAttachmentTooLarge(maxFileSize));
                    yield break;
                }
                attachments.Add((call.Text(13) ?? string.Create(CultureInfo.InvariantCulture, $"QueryResults{System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000)}.txt"), content));
            }
            else
            {
                body = (body ?? "") + "\r\n" + output;
            }
        }

        var user = MailUser(batch);
        var mailItemId = this.AgentScalar(batch, """
            insert dbo.sysmail_mailitems (profile_id, recipients, copy_recipients, blind_copy_recipients, subject, from_address, reply_to, body,
                body_format, importance, sensitivity, file_attachments, attachment_encoding, query, execute_query_database, attach_query_result_as_file,
                query_result_header, query_result_width, query_result_separator, exclude_query_output, append_query_error, send_request_date,
                send_request_user, sent_account_id, sent_status, sent_date, last_mod_date, last_mod_user)
            values (@profile, @to, @cc, @bcc, @subject, @from, @replyto, @body, @format, @importance, @sensitivity, @files, @encoding, @query,
                @database, @asfile, @header, @width, @separator, @exclude, @append, getdate(), @user, null, 0, null, getdate(), @user);
            select scope_identity();
            """,
            ("profile", SqlValue.FromInt32(profileId)), ("to", call.Values[1]), ("cc", call.Values[2]), ("bcc", call.Values[3]), ("subject", call.Values[4]),
            ("from", call.Values[22]), ("replyto", call.Values[23]), ("body", MailText(body)), ("format", SqlValue.FromNVarchar(bodyFormat)),
            ("importance", SqlValue.FromNVarchar(importance)), ("sensitivity", SqlValue.FromNVarchar(sensitivity)), ("files", call.Values[9]),
            ("encoding", MailText(this.MailSetting(batch, "DefaultAttachmentEncoding"))), ("query", call.Values[10]), ("database", call.Values[11]),
            ("asfile", call.Values[12]), ("header", call.Values[14]), ("width", call.Values[15]), ("separator", call.Values[16]), ("exclude", call.Values[17]),
            ("append", call.Values[18]), ("user", user))!.Value.CoerceTo(SqlType.Int32);
        foreach (var (name, content) in attachments)
        {
            _ = this.AgentSql(batch, """
                insert dbo.sysmail_attachments (mailitem_id, filename, filesize, attachment, last_mod_date, last_mod_user)
                values (@id, @name, @size, @content, getdate(), @user)
                """, ("id", mailItemId), ("name", SqlValue.FromNVarchar(name)), ("size", SqlValue.FromInt32(content.Length)),
                ("content", SqlValue.FromVarbinary(VarbinarySqlType.Get(SqlType.MaxLengthSentinel), content)), ("user", user));
        }
        this.DeliverQueuedMail(batch, mailItemId, profileId);
        call.SetOutput(21, mailItemId);
        AgentReturn(batch, returnCodeVar, 0);
        if (call.Int(17) != 1)
            yield return AgentPrint(batch, "sp_send_dbmail", 0, 0, string.Create(CultureInfo.InvariantCulture, $"Mail (Id: {mailItemId.AsInt32}) queued."));
    }

    /// <summary>
    /// The profile <c>sp_send_dbmail</c> sends through: the one named
    /// (<c>sysmail_verify_profile_sp</c>'s refusals answering 4), which a
    /// caller outside <c>sysadmin</c> may use only when it is granted to
    /// <c>public</c> or to the caller's msdb user (Msg 14607 at line 141);
    /// with none named, the caller's default private profile, else the
    /// default public one (Msg 14636).
    /// </summary>
    private int ResolveSendProfile(BatchContext batch, string? profileName)
    {
        var msdb = this.Msdb;
        var caller = MailSid(MailPrincipalSid(msdb, PermissionEnforcement.ResolveCrossDatabasePrincipal(batch.Connection, msdb)));
        if (profileName is null)
        {
            return this.AgentScalar(batch, """
                select top (1) profile_id from dbo.sysmail_principalprofile
                where is_default = 1 and (principal_sid = @caller or principal_sid = 0x00)
                order by case when principal_sid = @caller then 0 else 1 end
                """, ("caller", caller)) is { } found
                ? found.AsInt32
                : throw MailRefusal(SimulatedSqlException.MailNoGlobalProfile(), 112, 1);
        }
        var profileId = this.VerifyMailObject(batch, "profile", null, profileName, 4)!.Value;
        if (!AgentIsSysadmin(batch)
            && this.AgentScalar(batch, "select count(*) from dbo.sysmail_principalprofile where profile_id = @id and (principal_sid = @caller or principal_sid = 0x00)",
                ("id", SqlValue.FromInt32(profileId)), ("caller", caller))!.Value.AsInt32 == 0)
        {
            throw MailRefusal(SimulatedSqlException.MailNameNotValid("profile"), 141, 1);
        }
        return profileId;
    }

    /// <summary>
    /// Runs <c>sp_send_dbmail</c>'s query as the caller in
    /// <c>@execute_query_database</c> (the caller's database by default) and
    /// formats what it returns, or null when the database doesn't exist or
    /// the query raises any error.
    /// </summary>
    private string? RunMailQuery(BatchContext batch, string query, AgentCall call)
    {
        var width = call.Int(15) ?? 256;
        Database database;
        if (call.Text(11) is { } databaseName)
        {
            if (!this.Databases.TryGetValue(databaseName, out var named))
                return null;
            database = named;
        }
        else
        {
            database = batch.CurrentDatabase;
        }
        var format = new MailQueryFormat(call.Int(14) == 1, width, call.Text(16) is { Length: > 0 } separator ? separator[0] : ' ', call.Int(20) == 1);
        var text = new StringBuilder();
        try
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, query, null, runsIn: database))
            {
                switch (outcome)
                {
                    case SimulatedErrorOutcome:
                        return null;
                    case SimulatedInfoOutcome info:
                        format.AppendLine(text, info.Message.Message);
                        break;
                    case SimulatedSqlResultSet result:
                        var rows = format.AppendResultSet(text, result);
                        if (result.CountSuppressed != true)
                            AppendRowsAffected(text, rows);
                        break;
                    case SimulatedNonQuery { RecordsAffected: >= 0, CountSuppressed: not true } nonQuery:
                        AppendRowsAffected(text, nonQuery.RecordsAffected);
                        break;
                }
            }
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
        return text.ToString();
    }

    private static void AppendRowsAffected(StringBuilder text, int rows) =>
        _ = text.Append("\r\n(").Append(rows.ToString(CultureInfo.InvariantCulture)).Append(" rows affected)\r\n");

    /// <summary>
    /// How <c>sp_send_dbmail</c> lays out its query's output, the way the
    /// <c>sqlcmd</c> library it calls does: each column as wide as its type
    /// shows (or its name, if longer), numbers and the newer date and time
    /// types right-aligned, NULL as <c>NULL</c> cut to the column, columns
    /// apart by the separator, and every line cut into pieces of the result
    /// width. Without padding each value stands as it is.
    /// </summary>
    private sealed class MailQueryFormat(bool header, int width, char separator, bool noPadding)
    {
        private readonly bool header = header;
        private readonly int width = width;
        private readonly char separator = separator;
        private readonly bool noPadding = noPadding;

        /// <summary>Appends one line, cut into pieces of the result width, each closed by CRLF.</summary>
        public void AppendLine(StringBuilder text, string line)
        {
            if (line.Length == 0)
            {
                _ = text.Append("\r\n");
                return;
            }
            for (var start = 0; start < line.Length; start += this.width)
                _ = text.Append(line, start, Math.Min(this.width, line.Length - start)).Append("\r\n");
        }

        /// <summary>Appends a result set's header, dashes and rows, and returns how many rows it held.</summary>
        public int AppendResultSet(StringBuilder text, SimulatedSqlResultSet result)
        {
            var schema = result.Schema;
            var names = result.ColumnNames;
            var widths = new int[schema.Length];
            var rightAligned = new bool[schema.Length];
            for (var i = 0; i < schema.Length; i++)
            {
                widths[i] = Math.Max(names[i].Length, DisplayWidth(schema[i]));
                rightAligned[i] = RightAligned(schema[i]);
            }
            var line = new StringBuilder();
            if (this.header)
            {
                for (var i = 0; i < schema.Length; i++)
                    this.AppendCell(line, i, names[i], widths[i], rightAligned: false);
                this.AppendLine(text, line.ToString());
                _ = line.Clear();
                for (var i = 0; i < schema.Length; i++)
                    this.AppendCell(line, i, new string('-', this.noPadding ? names[i].Length : widths[i]), widths[i], rightAligned: false);
                this.AppendLine(text, line.ToString());
            }
            var cursor = result.CreateCursor();
            var rows = 0;
            while (cursor.MoveNext())
            {
                _ = line.Clear();
                for (var i = 0; i < schema.Length; i++)
                    this.AppendCell(line, i, Display(cursor[i]), widths[i], rightAligned[i]);
                this.AppendLine(text, line.ToString());
                rows++;
            }
            return rows;
        }

        private void AppendCell(StringBuilder line, int ordinal, string value, int columnWidth, bool rightAligned)
        {
            if (ordinal > 0)
                _ = line.Append(this.separator);
            if (this.noPadding)
            {
                _ = line.Append(value);
                return;
            }
            if (value.Length > columnWidth)
                value = value[..columnWidth];
            _ = rightAligned ? line.Append(' ', columnWidth - value.Length).Append(value) : line.Append(value).Append(' ', columnWidth - value.Length);
        }

        /// <summary>The width <c>sqlcmd</c> gives a column of <paramref name="type"/>, before its name widens it.</summary>
        private static int DisplayWidth(SqlType type) => type switch
        {
            DecimalSqlType decimalType => decimalType.precision + 2,
            CharSqlType fixedChar => fixedChar.length,
            NCharSqlType fixedNChar => fixedNChar.length,
            VarcharSqlType text => text.length == SqlType.MaxLengthSentinel ? 256 : text.length,
            NVarcharSqlType text => text.length == SqlType.MaxLengthSentinel ? 256 : text.length,
            BinarySqlType binary => 2 + (2 * binary.length),
            VarbinarySqlType binary => binary.length == SqlType.MaxLengthSentinel ? 0 : 2 + (2 * binary.length),
            TimeSqlType => 22,
            DateTime2SqlType => 38,
            DateTimeOffsetSqlType => 44,
            _ when type == SqlType.Bit => 1,
            _ when type == SqlType.TinyInt => 3,
            _ when type == SqlType.SmallInt => 6,
            _ when type == SqlType.Int32 => 11,
            _ when type == SqlType.BigInt => 20,
            _ when type == SqlType.Real => 14,
            _ when type == SqlType.Float => 24,
            _ when type == SqlType.Money => 21,
            _ when type == SqlType.SmallMoney => 12,
            _ when type == SqlType.DateTime => 23,
            _ when type == SqlType.SmallDateTime => 19,
            _ when type == SqlType.Date => 16,
            _ when type == SqlType.UniqueIdentifier => 36,
            _ when type is SystemNameSqlType => 128,
            _ => 256,
        };

        private static bool RightAligned(SqlType type) =>
            type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate
            || type is TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType || type == SqlType.Date;

        /// <summary>A value as <c>sqlcmd</c> shows it.</summary>
        private static string Display(SqlValue value)
        {
            if (value.IsNull)
                return "NULL";
            var type = value.Type;
            if (type == SqlType.Bit)
                return value.AsBoolean ? "1" : "0";
            if (type == SqlType.DateTime)
                return value.AsDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (type == SqlType.SmallDateTime)
                return value.AsSmallDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (type is BinarySqlType or VarbinarySqlType)
                return "0x" + Convert.ToHexString(value.AsBytes);
            if (type == SqlType.UniqueIdentifier)
                return value.AsGuid.ToString("D").ToUpperInvariant();
            return value.CoerceTo(SqlType.NVarchar).AsString;
        }
    }

    /// <summary>
    /// What Database Mail's external program makes of a queued item, written
    /// with the item in its sender's transaction. The simulator delivers no
    /// mail, so every attempt fails as real's does against a mail server it
    /// cannot reach: the program logs its start the first time, then a
    /// profile without accounts fails the item with one error row naming no
    /// item, and a profile with accounts logs one error per attempt — the
    /// first, then <c>AccountRetryAttempts</c> retries, through its first
    /// account — keeps the retry count in <c>sysmail_send_retries</c>, and
    /// fails the item. Real waits <c>AccountRetryDelay</c> between attempts,
    /// showing the item <c>retrying</c> meanwhile; here they all happen at once.
    /// </summary>
    private void DeliverQueuedMail(BatchContext batch, SqlValue mailItemId, int profileId)
    {
        this.StartDatabaseMailProcess(batch);
        var now = batch.CurrentStatement.UtcNow;
        var account = this.AgentScalar(batch, """
            select top (1) account_id from dbo.sysmail_profileaccount where profile_id = @id
            order by case when sequence_number is null then 1 else 0 end, sequence_number, account_id
            """, ("id", SqlValue.FromInt32(profileId)));
        if (account is null)
        {
            this.LogMailEvent(batch, 3, NoAccountsDescription, DatabaseMailProcessId, null, ProgramUser);
        }
        else
        {
            var retries = Math.Max(0, this.MailIntSetting(batch, "AccountRetryAttempts", 1));
            var description = string.Create(CultureInfo.InvariantCulture,
                $"The mail could not be sent to the recipients because of the mail server failure. (Sending Mail using Account {account.Value.AsInt32} ({now:yyyy-MM-dd'T'HH:mm:ss}). Exception Message: {UnreachableServerException})");
            for (var attempt = 0; attempt <= retries; attempt++)
                this.LogMailEvent(batch, 3, description, DatabaseMailProcessId, mailItemId, "sa");
            if (retries > 0)
            {
                _ = this.AgentSql(batch, "insert dbo.sysmail_send_retries (conversation_handle, mailitem_id, send_attempts, last_send_attempt_date) values (newid(), @id, @attempts, getdate())",
                    ("id", mailItemId), ("attempts", SqlValue.FromInt32(retries)));
            }
        }
        _ = this.AgentSql(batch, "update dbo.sysmail_mailitems set sent_status = 2, sent_date = @sent, last_mod_date = getdate(), last_mod_user = N'sa' where mailitem_id = @id",
            ("id", mailItemId), ("sent", SqlValue.FromDateTime(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), now.Kind))));
    }

    /// <summary>Logs the external program's start unless it is running.</summary>
    private void StartDatabaseMailProcess(BatchContext batch)
    {
        if (this.databaseMailProcessRunning)
            return;
        this.databaseMailProcessRunning = true;
        this.LogMailEvent(batch, 1, "DatabaseMail process is started", DatabaseMailProcessId, null, ProgramUser);
    }

    /// <summary>The login the external program's own rows name: the server's <c>Administrator</c>.</summary>
    private const string ProgramUser = AgentLocalServer + @"\Administrator";

    /// <summary>Adds a row to <c>sysmail_log</c>; <paramref name="eventType"/> 0 success, 1 information, 2 warning, 3 error.</summary>
    private void LogMailEvent(BatchContext batch, int eventType, string description, int? processId, SqlValue? mailItemId, string user) =>
        _ = this.AgentSql(batch, """
            insert dbo.sysmail_log (event_type, log_date, description, process_id, mailitem_id, account_id, last_mod_date, last_mod_user)
            values (@type, getdate(), @description, @process, @item, null, getdate(), @user)
            """, ("type", SqlValue.FromInt32(eventType)), ("description", SqlValue.FromNVarchar(description)), ("process", MailInt(processId)),
            ("item", mailItemId ?? SqlValue.Null(SqlType.Int32)), ("user", SqlValue.FromNVarchar(user)));

    // The external program's exception reports, as real writes them into the
    // log (probed 2026-10-16 against SQL Server 2025 on Linux, the server
    // named by a host that doesn't resolve).
    private const string UnreachableServerException =
        "\r\n\r\n1) Exception Information\r\n===================\r\nException Type: Microsoft.SqlServer.Management.SqlIMail.MailFramework.Exceptions.BaseMailFrameworkException\r\n"
        + "Message: Could not connect to mail server. (No such host is known)\r\nData: System.Collections.ListDictionaryInternal\r\nTargetSite: Void CheckServerValidity()\r\n"
        + "HelpLink: NULL\r\nSource: DatabaseMailProtocols\r\nHResult: -2146232832\r\n\r\nStackTrace Information\r\n===================\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.MailFramework.Smtp.SmtpMailSender.CheckServerValidity()\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.Server.Controller.ProfileMailSender.SendMailToAccount(Account a, IMessageSender ms, OutMailItem si)\r\n\r\n"
        + "2) Exception Information\r\n===================\r\nException Type: System.Net.Sockets.SocketException\r\nErrorCode: 11001\r\nMessage: No such host is known\r\n"
        + "SocketErrorCode: HostNotFound\r\nNativeErrorCode: 11001\r\nData: System.Collections.ListDictionaryInternal\r\nTargetSite: Void .ctor(System.String, Int32)\r\n"
        + "HelpLink: NULL\r\nSource: System\r\nHResult: -2147467259\r\n\r\nStackTrace Information\r\n===================\r\n"
        + "   at System.Net.Sockets.TcpClient..ctor(String hostname, Int32 port)\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.MailFramework.Smtp.SmtpMailSender.CheckServerValidity().\r\n";

    private const string NoAccountsDescription =
        "\r\n\r\n1) Exception Information\r\n===================\r\nException Type: Microsoft.SqlServer.Management.SqlIMail.Server.Common.BaseException\r\n"
        + "Message: Could not retrieve item from the queue. Reason: the list of accounts which could send this mail is empty (probably due to the use of invalid profile).\r\n"
        + "Data: System.Collections.ListDictionaryInternal\r\n"
        + "TargetSite: Microsoft.SqlServer.Management.SqlIMail.Server.Controller.ICommand CreateSendMailCommand(Microsoft.SqlServer.Management.SqlIMail.Server.DataAccess.DBSession)\r\n"
        + "HelpLink: NULL\r\nSource: DatabaseMailEngine\r\nHResult: -2146232832\r\n\r\nStackTrace Information\r\n===================\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.Server.Controller.CommandFactory.CreateSendMailCommand(DBSession dbSession)\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.Server.Controller.CommandRunner.Run(DBSession db)\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.IMailProcess.ThreadCallBack.MailOperation(Object obj)";

    private const string ShutdownDescription =
        "\r\n\r\n1) Exception Information\r\n===================\r\nException Type: Microsoft.SqlServer.Management.SqlIMail.Server.DataAccess.NoQueueDataException\r\n"
        + "Message: Database Mail shutting down. Mail queue has been stopped.\r\nData: System.Collections.ListDictionaryInternal\r\n"
        + "TargetSite: Microsoft.SqlServer.Management.SqlIMail.Server.Objects.QueueItem GetQueueItemFromCommand(Microsoft.Data.SqlClient.SqlCommand)\r\n"
        + "HelpLink: NULL\r\nSource: DatabaseMailEngine\r\nHResult: -2146232832\r\n\r\nStackTrace Information\r\n===================\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.Server.DataAccess.QueueDataReader.GetQueueItemFromCommand(SqlCommand c)\r\n"
        + "   at Microsoft.SqlServer.Management.SqlIMail.IMailProcess.QueueItemProcesser.GetDataFromQueue(DataAccessAdapter da, Int32 lifetimeMinimumSec)";

    // ---- the queue ----

    /// <summary>
    /// <c>sysmail_start_sp</c> / <c>sysmail_stop_sp</c>: each call logs who
    /// started or stopped the queue; a stop also shuts down a running
    /// external program, which logs its shutdown.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailStartStop(BatchContext batch, string? returnCodeVar, bool start)
    {
        var procedure = start ? "sysmail_start_sp" : "sysmail_stop_sp";
        if (this.BindMailCall(batch, procedure, []) is null)
            yield break;
        this.RequireDatabaseMailXps(procedure);
        var login = batch.Connection.Security.Effective.LoginName;
        this.LogMailEvent(batch, 1, start ? $"The mail queue was started by login \"{login}\"." : $"The mail queue stopped by login \"{login}\".", null, null, login);
        this.databaseMailStopped = !start;
        if (!start && this.databaseMailProcessRunning)
        {
            this.databaseMailProcessRunning = false;
            this.LogMailEvent(batch, 1, ShutdownDescription, DatabaseMailProcessId, null, ProgramUser);
            this.LogMailEvent(batch, 1, "DatabaseMail process is shutting down", DatabaseMailProcessId, null, ProgramUser);
        }
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary><c>sysmail_help_status_sp</c>: <c>STARTED</c> or <c>STOPPED</c>.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailHelpStatus(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_help_status_sp", [], userRole: true) is null)
            yield break;
        this.RequireDatabaseMailXps("sysmail_help_status_sp");
        yield return this.AgentResultSet(["Status varchar(7)"], [[SqlValue.FromNVarchar(this.databaseMailStopped ? "STOPPED" : "STARTED")]]);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// <c>sysmail_help_queue_sp</c>: the <c>mail</c> queue (absent while
    /// stopped) and the <c>status</c> queue, each empty and inactive, since
    /// every item leaves the queue as it is sent; a type other than the two
    /// is Msg 14266.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailHelpQueue(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_help_queue_sp", [AgentParameter("queue_type", AgentText, 6)]) is not { } call)
            yield break;
        var queueType = call.Text(0);
        if (queueType is not null && !string.Equals(queueType, "mail", StringComparison.OrdinalIgnoreCase) && !string.Equals(queueType, "status", StringComparison.OrdinalIgnoreCase))
            throw MailRefusal(SimulatedSqlException.AgentInvalidValue("@queue_type", "mail, status"), 12, 1);
        var since = SqlValue.FromDateTime(WholeMilliseconds(this.SeedDate));
        var rows = new List<SqlValue[]>();
        foreach (var queue in (ReadOnlySpan<string>)["mail", "status"])
        {
            if ((queue == "mail" && this.databaseMailStopped) || (queueType is not null && !string.Equals(queueType, queue, StringComparison.OrdinalIgnoreCase)))
                continue;
            rows.Add([SqlValue.FromNVarchar(queue), SqlValue.FromInt32(0), SqlValue.FromNVarchar("INACTIVE"), since, since]);
        }
        yield return this.AgentResultSet(
            ["queue_type nvarchar(6) null", "length int", "state nvarchar(64) null", "last_empty_rowset_time datetime null", "last_activated_time datetime null"], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    // ---- deleting items and log rows ----

    /// <summary>
    /// <c>sysmail_delete_mailitems_sp</c>: the items sent before a date, of a
    /// status, or both — a caller outside <c>sysadmin</c> only its own —
    /// with their attachments and retry rows, logging how many went.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailDeleteMailItems(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_delete_mailitems_sp", [AgentParameter("sent_before", SqlType.DateTime), AgentParameter("sent_status", AgentVarchar, 8)], userRole: true) is not { } call)
            yield break;
        int? status = call.Text(1)?.ToUpperInvariant() switch
        {
            null => null,
            "FAILED" => 2,
            "RETRYING" => 3,
            "SENT" => 1,
            "UNSENT" => 0,
            _ => throw MailRefusal(SimulatedSqlException.AgentInvalidValue("@sent_status", "unsent, sent, failed, retrying"), 14, 1),
        };
        if (call.IsNull(0) && status is null)
            throw MailRefusal(SimulatedSqlException.MailEitherParameter("@sent_before", "@sent_status"), 20, 1);
        var login = batch.Connection.Security.Effective.LoginName;
        var deleted = this.AgentScalar(batch, """
            declare @items table (mailitem_id int);
            insert @items select mailitem_id from dbo.sysmail_mailitems
            where (@before is null or send_request_date < @before) and (@status is null or sent_status = @status)
                and (@all = 1 or send_request_user = @login);
            delete dbo.sysmail_attachments where mailitem_id in (select mailitem_id from @items);
            delete dbo.sysmail_send_retries where mailitem_id in (select mailitem_id from @items);
            delete dbo.sysmail_mailitems where mailitem_id in (select mailitem_id from @items);
            select count(*) from @items;
            """, ("before", call.Values[0]), ("status", MailInt(status)), ("all", SqlValue.FromInt32(AgentIsSysadmin(batch) ? 1 : 0)),
            ("login", SqlValue.FromNVarchar(login)))!.Value.AsInt32;
        this.LogMailEvent(batch, 1, string.Create(CultureInfo.InvariantCulture, $"Mail items deletion is initiated by user \"{login}\". {deleted} items deleted."), null, null, login);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary><c>sysmail_delete_log_sp</c>: the log rows before a date, of an event type, or all of them.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailDeleteLog(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_delete_log_sp", [AgentParameter("logged_before", SqlType.DateTime), AgentParameter("event_type", AgentVarchar, 15)]) is not { } call)
            yield break;
        int? eventType = call.Text(1)?.ToUpperInvariant() switch
        {
            null => null,
            "ERROR" => 3,
            "INFORMATION" => 1,
            "SUCCESS" => 0,
            "WARNING" => 2,
            _ => throw MailRefusal(SimulatedSqlException.AgentInvalidValue("@event_type", "success, warning, error, information"), 15, 1),
        };
        _ = this.AgentSql(batch, "delete dbo.sysmail_log where (@before is null or log_date < @before) and (@type is null or event_type = @type)",
            ("before", call.Values[0]), ("type", MailInt(eventType)));
        AgentReturn(batch, returnCodeVar, 0);
    }
}
