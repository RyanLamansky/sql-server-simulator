using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// msdb's Database Mail configuration procedures: accounts, profiles, the
// accounts a profile sends through, the principals a profile serves, and the
// settings, each as SQL Server 2025 runs them (probed 2026-10-16). They ride
// the Agent procedures' msdb dispatch, binding and error attribution
// (Simulation.AgentProcedures.cs); sp_send_dbmail and the queue procedures
// live in Simulation.DatabaseMailSend.cs.
partial class Simulation
{
    /// <summary>The Database Mail procedures the msdb dispatch resolves beside the Agent ones.</summary>
    private static readonly string[] DatabaseMailProcedureNames =
    [
        "sp_send_dbmail", "sysmail_add_account_sp", "sysmail_add_principalprofile_sp", "sysmail_add_profile_sp", "sysmail_add_profileaccount_sp",
        "sysmail_configure_sp", "sysmail_delete_account_sp", "sysmail_delete_log_sp", "sysmail_delete_mailitems_sp",
        "sysmail_delete_principalprofile_sp", "sysmail_delete_profile_sp", "sysmail_delete_profileaccount_sp", "sysmail_help_account_sp",
        "sysmail_help_configure_sp", "sysmail_help_configure_value_sp", "sysmail_help_principalprofile_sp", "sysmail_help_profile_sp",
        "sysmail_help_profileaccount_sp", "sysmail_help_queue_sp", "sysmail_help_status_sp", "sysmail_start_sp", "sysmail_stop_sp",
        "sysmail_update_account_sp", "sysmail_update_principalprofile_sp", "sysmail_update_profile_sp", "sysmail_update_profileaccount_sp",
    ];

    private IEnumerable<SimulatedStatementOutcome> DatabaseMailProcedure(BatchContext batch, string name, string? returnCodeVar) => name switch
    {
        "sp_send_dbmail" => this.InvokeSpSendDbMail(batch, returnCodeVar),
        "sysmail_add_account_sp" => this.InvokeSysmailAddAccount(batch, returnCodeVar),
        "sysmail_add_principalprofile_sp" => this.InvokeSysmailPrincipalProfile(batch, returnCodeVar, MailChange.Add),
        "sysmail_add_profile_sp" => this.InvokeSysmailAddProfile(batch, returnCodeVar),
        "sysmail_add_profileaccount_sp" => this.InvokeSysmailProfileAccount(batch, returnCodeVar, MailChange.Add),
        "sysmail_configure_sp" => this.InvokeSysmailConfigure(batch, returnCodeVar),
        "sysmail_delete_account_sp" => this.InvokeSysmailDeleteAccount(batch, returnCodeVar),
        "sysmail_delete_log_sp" => this.InvokeSysmailDeleteLog(batch, returnCodeVar),
        "sysmail_delete_mailitems_sp" => this.InvokeSysmailDeleteMailItems(batch, returnCodeVar),
        "sysmail_delete_principalprofile_sp" => this.InvokeSysmailPrincipalProfile(batch, returnCodeVar, MailChange.Delete),
        "sysmail_delete_profile_sp" => this.InvokeSysmailDeleteProfile(batch, returnCodeVar),
        "sysmail_delete_profileaccount_sp" => this.InvokeSysmailProfileAccount(batch, returnCodeVar, MailChange.Delete),
        "sysmail_help_account_sp" => this.InvokeSysmailHelpAccount(batch, returnCodeVar),
        "sysmail_help_configure_sp" => this.InvokeSysmailHelpConfigure(batch, returnCodeVar),
        "sysmail_help_configure_value_sp" => this.InvokeSysmailHelpConfigureValue(batch, returnCodeVar),
        "sysmail_help_principalprofile_sp" => this.InvokeSysmailPrincipalProfile(batch, returnCodeVar, MailChange.Help),
        "sysmail_help_profile_sp" => this.InvokeSysmailHelpProfile(batch, returnCodeVar),
        "sysmail_help_profileaccount_sp" => this.InvokeSysmailProfileAccount(batch, returnCodeVar, MailChange.Help),
        "sysmail_help_queue_sp" => this.InvokeSysmailHelpQueue(batch, returnCodeVar),
        "sysmail_help_status_sp" => this.InvokeSysmailHelpStatus(batch, returnCodeVar),
        "sysmail_start_sp" => this.InvokeSysmailStartStop(batch, returnCodeVar, start: true),
        "sysmail_stop_sp" => this.InvokeSysmailStartStop(batch, returnCodeVar, start: false),
        "sysmail_update_account_sp" => this.InvokeSysmailUpdateAccount(batch, returnCodeVar),
        "sysmail_update_principalprofile_sp" => this.InvokeSysmailPrincipalProfile(batch, returnCodeVar, MailChange.Update),
        "sysmail_update_profile_sp" => this.InvokeSysmailUpdateProfile(batch, returnCodeVar),
        "sysmail_update_profileaccount_sp" => this.InvokeSysmailProfileAccount(batch, returnCodeVar, MailChange.Update),
        _ => throw new InvalidOperationException($"{name} is an msdb procedure without a dispatch arm."),
    };

    /// <summary>Which of a family's four procedures runs.</summary>
    private enum MailChange
    {
        Add,
        Update,
        Delete,
        Help,
    }

    /// <summary>
    /// Binds a Database Mail procedure's call and checks <c>EXECUTE</c> on it:
    /// only a <c>sysadmin</c> (or msdb's <c>dbo</c>) runs the configuration
    /// procedures, while <c>DatabaseMailUserRole</c> holds
    /// <c>sp_send_dbmail</c>, <c>sysmail_help_status_sp</c> and
    /// <c>sysmail_delete_mailitems_sp</c>. Null while the batch walks in skip mode.
    /// </summary>
    private AgentCall? BindMailCall(BatchContext batch, string procedure, SystemProcedureParameter[] parameters, bool userRole = false)
    {
        if (BindAgentCall(batch, procedure, parameters) is not { } call)
            return null;
        this.RequireAgentExecute(batch, procedure, userRole ? DatabaseMailUserRoleId : null);
        return call;
    }

    /// <summary>
    /// Refuses (Msg 15281 at line 0, the caller's return code unset) the
    /// procedures that reach Database Mail's external program —
    /// <c>sp_send_dbmail</c>, <c>sysmail_start_sp</c>, <c>sysmail_stop_sp</c>
    /// and <c>sysmail_help_status_sp</c> — while <c>Database Mail XPs</c> is
    /// off, as a stock server ships it.
    /// </summary>
    private void RequireDatabaseMailXps(string procedure)
    {
        if (this.ConfigurationInUse(DatabaseMailXpsConfigurationId) != 0)
            return;
        var refusal = SimulatedSqlException.ComponentBlocked(procedure, "Database Mail XPs", Database.DefaultSchemaName).AtAgentSite(0);
        refusal.SystemProcedureBindingError = true;
        throw refusal;
    }

    /// <summary><c>configuration_id</c> of <c>Database Mail XPs</c>.</summary>
    private const int DatabaseMailXpsConfigurationId = 16386;

    private static SqlValue MailInt(int? value) => value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);

    private static SqlValue MailText(string? value) => value is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(value);

    /// <summary>The caller's login name, which every <c>last_mod_user</c> column records.</summary>
    private static SqlValue MailUser(BatchContext batch) => SqlValue.FromNVarchar(batch.Connection.Security.Effective.LoginName);

    /// <summary>A refusal raised at <paramref name="line"/> of <paramref name="procedure"/>, answering <paramref name="returnCode"/>.</summary>
    private static SimulatedSqlException MailRefusal(SimulatedSqlException refusal, int line, int returnCode, string? procedure = null)
    {
        _ = refusal.AtAgentSite(line, procedure);
        refusal.SystemProcedureReturnCode = returnCode;
        return refusal;
    }

    /// <summary>
    /// A refusal of one of the procedure's own writes — a duplicate key, a
    /// NULL in a NOT NULL column — which real follows with Msg 3621 and goes
    /// on past to its <c>RETURN</c>.
    /// </summary>
    private static SimulatedSqlException MailWriteRefusal(BatchContext batch, SimulatedSqlException refusal, int line, int returnCode)
    {
        batch.CurrentStatement.WritesRows = true;
        return MailRefusal(refusal, line, returnCode);
    }

    /// <summary>Spends one value of <paramref name="table"/>'s identity, as real's refused INSERT does.</summary>
    private void SpendMailIdentity(string table) =>
        _ = this.MsdbTable(table).StoredColumns[0].Identity!.GenerateNext(SqlType.Int32);

    /// <summary>
    /// The id of the account or profile a call names, as
    /// <c>sysmail_verify_account_sp</c> / <c>sysmail_verify_profile_sp</c>
    /// find it: Msg 14604 for neither identifier (unless
    /// <paramref name="allowBothNull"/>, answering null), 14605 for an id and
    /// a name naming two, 14606 for an id and 14607 for a name naming none,
    /// each answering <paramref name="returnCode"/>.
    /// </summary>
    private int? VerifyMailObject(BatchContext batch, string what, int? id, string? name, int returnCode, bool allowBothNull = false)
    {
        var (table, key, procedure) = what == "account"
            ? ("sysmail_account", "account_id", "msdb.dbo.sysmail_verify_account_sp")
            : ("sysmail_profile", "profile_id", "msdb.dbo.sysmail_verify_profile_sp");
        if (id is null && name is null)
        {
            return allowBothNull ? null : throw MailRefusal(SimulatedSqlException.MailIdentifiersBothNull(what), 14, returnCode, procedure);
        }
        if (id is { } given)
        {
            if (this.AgentScalar(batch, $"select name from dbo.{table} where {key} = @id", ("id", SqlValue.FromInt32(given))) is not { } found)
            {
                throw name is null
                    ? MailRefusal(SimulatedSqlException.MailIdNotValid(what), 33, returnCode, procedure)
                    : MailRefusal(SimulatedSqlException.MailIdentifiersMismatch(what), 24, returnCode, procedure);
            }
            if (name is not null && !this.Databases[MsdbDatabaseName].Collation.Equals(found.AsString, name))
                throw MailRefusal(SimulatedSqlException.MailIdentifiersMismatch(what), 24, returnCode, procedure);
            return given;
        }
        return this.AgentScalar(batch, $"select {key} from dbo.{table} where name = @name", ("name", SqlValue.FromNVarchar(name!))) is { } byName
            ? byName.AsInt32
            : throw MailRefusal(SimulatedSqlException.MailNameNotValid(what), 42, returnCode, procedure);
    }

    /// <summary>
    /// The msdb principal a call names, as <c>sysmail_verify_principal_sp</c>
    /// finds it — a database user or <c>public</c>, never another role — and
    /// the sid a profile grant records for it: <c>0x00</c> for <c>public</c>
    /// (which <c>guest</c>'s sid shares), the user's own sid otherwise.
    /// </summary>
    private (DatabasePrincipal Principal, byte[] Sid)? VerifyMailPrincipal(int? id, string? name, int returnCode, bool allowBothNull = false)
    {
        const string procedure = "msdb.dbo.sysmail_verify_principal_sp";
        if (id is null && name is null)
            return allowBothNull ? null : throw MailRefusal(SimulatedSqlException.MailIdentifiersBothNull("principal"), 13, returnCode, procedure);
        var msdb = this.Databases[MsdbDatabaseName];
        DatabasePrincipal? byId = null;
        if (id is { } principalId)
        {
            foreach (var (_, candidate) in msdb.Principals)
            {
                if (candidate.PrincipalId == principalId && IsMailPrincipal(candidate))
                    byId = candidate;
            }
            if (byId is null)
            {
                throw name is null
                    ? MailRefusal(SimulatedSqlException.MailIdNotValid("principal"), 38, returnCode, procedure)
                    : MailRefusal(SimulatedSqlException.MailIdentifiersMismatch("principal"), 27, returnCode, procedure);
            }
        }
        var principal = byId;
        if (name is not null)
        {
            if (!msdb.Principals.TryGetValue(name, out var byName) || !IsMailPrincipal(byName))
            {
                throw byId is null
                    ? MailRefusal(SimulatedSqlException.MailNameNotValid("principal"), 49, returnCode, procedure)
                    : MailRefusal(SimulatedSqlException.MailIdentifiersMismatch("principal"), 27, returnCode, procedure);
            }
            if (byId is not null && !ReferenceEquals(byId, byName))
                throw MailRefusal(SimulatedSqlException.MailIdentifiersMismatch("principal"), 27, returnCode, procedure);
            principal = byName;
        }
        return (principal!, MailPrincipalSid(msdb, principal!));
    }

    private static bool IsMailPrincipal(DatabasePrincipal principal) =>
        principal.PrincipalId == Database.PublicPrincipalId || (principal.TypeCode != "R" && principal.TypeCode != "A");

    private static byte[] MailPrincipalSid(Database msdb, DatabasePrincipal principal) =>
        principal.PrincipalId == Database.PublicPrincipalId ? [0x00] : BuiltInResources.DatabasePrincipalSid(msdb, principal) ?? [0x00];

    private static SqlValue MailSid(byte[] sid) => SqlValue.FromVarbinary(VarbinarySqlType.Get(85), sid);

    // ---- accounts ----

    private static readonly SystemProcedureParameter[] SysmailAddAccountParameters =
    [
        AgentRequired("account_name", AgentName, 128),
        AgentRequired("email_address", AgentText, 128),
        AgentParameter("display_name", AgentText, 128),
        AgentParameter("replyto_address", AgentText, 128),
        AgentParameter("description", AgentText, 256),
        AgentParameter("mailserver_name", AgentName, 128),
        AgentParameter("mailserver_type", AgentName, SqlValue.FromNVarchar("SMTP")),
        AgentParameter("port", SqlType.Int32, SqlValue.FromInt32(25)),
        AgentParameter("username", AgentText, 128),
        AgentParameter("password", AgentText, 128),
        AgentParameter("use_default_credentials", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("enable_ssl", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("account_id", SqlType.Int32),
    ];

    /// <summary>
    /// <c>sysmail_verify_accountparams_sp</c>: a NULL server type (Msg 14614),
    /// a user name beside default credentials (14666), a password without a
    /// user name (14615), each answering 1. A type other than NULL passes,
    /// to meet the server-type foreign key.
    /// </summary>
    private static void VerifyMailAccountParameters(string? serverType, bool useDefaultCredentials, string? userName, string? password)
    {
        const string procedure = "msdb.dbo.sysmail_verify_accountparams_sp";
        if (serverType is null)
            throw MailRefusal(SimulatedSqlException.MailServerTypeInvalid(null), 18, 1, procedure);
        if (useDefaultCredentials && userName is not null)
            throw MailRefusal(SimulatedSqlException.MailUserNameWithDefaultCredentials(), 25, 1, procedure);
        if (password is not null && userName is null)
            throw MailRefusal(SimulatedSqlException.MailPasswordWithoutUserName(), 32, 1, procedure);
    }

    /// <summary>
    /// <c>sysmail_add_account_sp</c>: the account, then its SMTP server, in
    /// one transaction, so a refused server row (a server type
    /// <c>sysmail_servertype</c> lacks, Msg 547; a NULL port, Msg 515;
    /// answering 4) takes the account with it. A duplicate or NULL name and a
    /// NULL address are the account INSERT's own Msg 2627 / 515, answering 2.
    /// The server defaults to the instance's own name, and a user name is kept
    /// in a server credential named by a new GUID.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailAddAccount(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_add_account_sp", SysmailAddAccountParameters) is not { } call)
            yield break;
        var serverType = call.Text(6);
        var useDefault = call.Int(10) == 1;
        VerifyMailAccountParameters(serverType, useDefault, call.Text(8), call.Text(9));
        var name = call.Text(0);
        if (name is null || call.IsNull(1))
        {
            this.SpendMailIdentity("sysmail_account");
            throw MailWriteRefusal(batch, SimulatedSqlException.CannotInsertNull(name is null ? "name" : "email_address", "msdb.dbo.sysmail_account"), 37, 2);
        }
        if (this.AgentScalar(batch, "select count(*) from dbo.sysmail_account where name = @name", ("name", SqlValue.FromNVarchar(name)))!.Value.AsInt32 > 0)
        {
            this.SpendMailIdentity("sysmail_account");
            throw MailWriteRefusal(batch, SimulatedSqlException.ViolationOfKeyConstraint("UNIQUE KEY", "SYSMAIL_ACCOUNT_NameMustBeUnique", "dbo.sysmail_account", name), 37, 2);
        }
        var serverTypeKnown = this.AgentScalar(batch, "select count(*) from dbo.sysmail_servertype where servertype = @type", ("type", SqlValue.FromNVarchar(serverType!)))!.Value.AsInt32 > 0;
        if (!serverTypeKnown || call.IsNull(7))
        {
            this.SpendMailIdentity("sysmail_account");
            throw MailWriteRefusal(batch, serverTypeKnown
                ? SimulatedSqlException.CannotInsertNull("port", "msdb.dbo.sysmail_server")
                : SimulatedSqlException.MailServerTypeConflict(), 65, 4);
        }
        var accountId = this.AgentScalar(batch, """
            insert dbo.sysmail_account (name, description, email_address, display_name, replyto_address, last_mod_datetime, last_mod_user)
            values (@name, @description, @email, @display, @replyto, getdate(), @user);
            select scope_identity();
            """,
            ("name", SqlValue.FromNVarchar(name)), ("description", call.Values[4]), ("email", call.Values[1]), ("display", call.Values[2]),
            ("replyto", call.Values[3]), ("user", MailUser(batch)))!.Value.CoerceTo(SqlType.Int32).AsInt32;
        var credentialId = call.Text(8) is { } userName ? this.CreateMailCredential(batch, userName) : (int?)null;
        _ = this.AgentSql(batch, """
            insert dbo.sysmail_server (account_id, servertype, servername, port, username, credential_id, use_default_credentials, enable_ssl, flags,
                timeout, last_mod_datetime, last_mod_user)
            values (@id, @type, @server, @port, @username, @credential, @default, @ssl, 0, null, getdate(), @user)
            """,
            ("id", SqlValue.FromInt32(accountId)), ("type", SqlValue.FromNVarchar(serverType!)),
            ("server", SqlValue.FromNVarchar(call.Text(5) ?? AgentLocalServer)), ("port", call.Values[7]), ("username", call.Values[8]),
            ("credential", MailInt(credentialId)), ("default", SqlValue.FromBoolean(useDefault)), ("ssl", SqlValue.FromBoolean(call.Int(11) == 1)),
            ("user", MailUser(batch)));
        call.SetOutput(12, SqlValue.FromInt32(accountId));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>Creates the server credential an account's user name is kept in, named by a new GUID, and returns its id.</summary>
    private int CreateMailCredential(BatchContext batch, string userName)
    {
        RecordServerSecurityUndo(batch);
        var id = Interlocked.Increment(ref this.lastCredentialId);
        var now = batch.CurrentStatement.UtcNow;
        var name = Guid.NewGuid().ToString("D").ToUpperInvariant();
        _ = this.Credentials.TryAdd(name, new ServerCredential(id, name, userName, now, now));
        return id;
    }

    /// <summary>Drops the server credential <paramref name="credentialId"/> an account kept its user name in.</summary>
    private void DropMailCredential(BatchContext batch, SqlValue credentialId)
    {
        if (credentialId.IsNull)
            return;
        var id = credentialId.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var (name, credential) in this.Credentials)
        {
            if (credential.Id != id)
                continue;
            RecordServerSecurityUndo(batch);
            _ = this.Credentials.TryRemove(name, out _);
            return;
        }
    }

    private static readonly SystemProcedureParameter[] SysmailUpdateAccountParameters =
    [
        AgentParameter("account_id", SqlType.Int32),
        AgentParameter("account_name", AgentName, 128),
        AgentParameter("email_address", AgentText, 128),
        AgentParameter("display_name", AgentText, 128),
        AgentParameter("replyto_address", AgentText, 128),
        AgentParameter("description", AgentText, 256),
        AgentParameter("mailserver_name", AgentName, 128),
        AgentParameter("mailserver_type", AgentName, 128),
        AgentParameter("port", SqlType.Int32),
        AgentParameter("username", AgentName, 128),
        AgentParameter("password", AgentName, 128),
        AgentParameter("use_default_credentials", SqlType.Bit),
        AgentParameter("enable_ssl", SqlType.Bit),
        AgentParameter("timeout", SqlType.Int32),
        AgentParameter("no_credential_change", SqlType.Bit),
    ];

    /// <summary>
    /// <c>sysmail_update_account_sp</c>: the account by id or name (an id
    /// with a name renames it); every other parameter left NULL keeps its
    /// value — except the user name, whose absence drops the account's
    /// credential unless <c>@no_credential_change = 1</c>. A name another
    /// account holds is Msg 2627, answering 0.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailUpdateAccount(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_update_account_sp", SysmailUpdateAccountParameters) is not { } call)
            yield break;
        int accountId;
        string? newName = null;
        if (call.Int(0) is { } id)
        {
            accountId = this.VerifyMailObject(batch, "account", id, null, 1)!.Value;
            newName = call.Text(1);
        }
        else
        {
            accountId = this.VerifyMailObject(batch, "account", null, call.Text(1), 1)!.Value;
        }
        var server = this.AgentRows(batch, "sysmail_server", "account_id = @id", ("id", SqlValue.FromInt32(accountId))) is [var row, ..] ? row : null;
        var useDefault = call.Int(11) is { } flag ? flag == 1 : server?.Int("use_default_credentials") == 1;
        var keepCredential = call.Int(14) == 1;
        VerifyMailAccountParameters(call.Text(7) ?? server?.Text("servertype") ?? "SMTP", useDefault, keepCredential ? null : call.Text(9), keepCredential ? null : call.Text(10));
        if (newName is not null
            && this.AgentScalar(batch, "select count(*) from dbo.sysmail_account where name = @name and account_id <> @id",
                ("name", SqlValue.FromNVarchar(newName)), ("id", SqlValue.FromInt32(accountId)))!.Value.AsInt32 > 0)
        {
            throw MailWriteRefusal(batch, SimulatedSqlException.ViolationOfKeyConstraint("UNIQUE KEY", "SYSMAIL_ACCOUNT_NameMustBeUnique", "dbo.sysmail_account", newName), 98, 0);
        }
        _ = this.AgentSql(batch, """
            update dbo.sysmail_account set name = isnull(@name, name), email_address = isnull(@email, email_address),
                display_name = isnull(@display, display_name), replyto_address = isnull(@replyto, replyto_address),
                description = isnull(@description, description), last_mod_datetime = getdate(), last_mod_user = @user
            where account_id = @id
            """,
            ("id", SqlValue.FromInt32(accountId)), ("name", MailText(newName)), ("email", call.Values[2]), ("display", call.Values[3]),
            ("replyto", call.Values[4]), ("description", call.Values[5]), ("user", MailUser(batch)));
        var credential = server?["credential_id"] ?? SqlValue.Null(SqlType.Int32);
        var userName = server?["username"] ?? SqlValue.Null(SqlType.NVarchar);
        if (!keepCredential)
        {
            if (call.Text(9) is { } user)
            {
                if (credential.IsNull)
                    credential = SqlValue.FromInt32(this.CreateMailCredential(batch, user));
                userName = SqlValue.FromNVarchar(user);
            }
            else
            {
                this.DropMailCredential(batch, credential);
                credential = SqlValue.Null(SqlType.Int32);
                userName = SqlValue.Null(SqlType.NVarchar);
            }
        }
        _ = this.AgentSql(batch, """
            update dbo.sysmail_server set servertype = isnull(@type, servertype), servername = isnull(@server, servername), port = isnull(@port, port),
                username = @username, credential_id = @credential, use_default_credentials = isnull(@default, use_default_credentials),
                enable_ssl = isnull(@ssl, enable_ssl), timeout = isnull(@timeout, timeout), last_mod_datetime = getdate(), last_mod_user = @user
            where account_id = @id
            """,
            ("id", SqlValue.FromInt32(accountId)), ("type", call.Values[7]), ("server", call.Values[6]), ("port", call.Values[8]),
            ("username", userName), ("credential", credential), ("default", call.Values[11]), ("ssl", call.Values[12]), ("timeout", call.Values[13]),
            ("user", MailUser(batch)));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SysmailAccountIdentifierParameters =
    [
        AgentParameter("account_id", SqlType.Int32),
        AgentParameter("account_name", AgentName, 128),
    ];

    /// <summary><c>sysmail_delete_account_sp</c>: the account, its server, its place in every profile and its credential.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailDeleteAccount(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_delete_account_sp", SysmailAccountIdentifierParameters) is not { } call)
            yield break;
        var accountId = SqlValue.FromInt32(this.VerifyMailObject(batch, "account", call.Int(0), call.Text(1), 1)!.Value);
        if (this.AgentScalar(batch, "select credential_id from dbo.sysmail_server where account_id = @id", ("id", accountId)) is { } credential)
            this.DropMailCredential(batch, credential);
        _ = this.AgentSql(batch, """
            delete dbo.sysmail_profileaccount where account_id = @id;
            delete dbo.sysmail_server where account_id = @id;
            delete dbo.sysmail_account where account_id = @id;
            """, ("id", accountId));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary><c>sysmail_help_account_sp</c>: every account, or the one named, with its server.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailHelpAccount(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_help_account_sp", SysmailAccountIdentifierParameters) is not { } call)
            yield break;
        var accountId = this.VerifyMailObject(batch, "account", call.Int(0), call.Text(1), 1, allowBothNull: true);
        var rows = this.AgentSql(batch, """
            select a.account_id, a.name, a.description, a.email_address, a.display_name, a.replyto_address, s.servertype, s.servername, s.port,
                s.username, s.use_default_credentials, s.enable_ssl
            from dbo.sysmail_account as a join dbo.sysmail_server as s on s.account_id = a.account_id
            where @id is null or a.account_id = @id
            order by a.account_id
            """, ("id", MailInt(accountId)));
        yield return this.AgentResultSet(
            [
                "account_id int", "name nvarchar(128)", "description nvarchar(256) null", "email_address nvarchar(128)", "display_name nvarchar(128) null",
                "replyto_address nvarchar(128) null", "servertype nvarchar(128)", "servername nvarchar(128)", "port int", "username nvarchar(128) null",
                "use_default_credentials bit", "enable_ssl bit",
            ], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    // ---- profiles ----

    private static readonly SystemProcedureParameter[] SysmailAddProfileParameters =
    [
        AgentRequired("profile_name", AgentName, 128),
        AgentParameter("description", AgentText, 256),
        AgentParameter("profile_id", SqlType.Int32),
    ];

    /// <summary><c>sysmail_add_profile_sp</c>: a duplicate or NULL name is the INSERT's own Msg 2627 / 515, answering 0.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailAddProfile(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_add_profile_sp", SysmailAddProfileParameters) is not { } call)
            yield break;
        if (call.Text(0) is not { } name)
        {
            this.SpendMailIdentity("sysmail_profile");
            throw MailWriteRefusal(batch, SimulatedSqlException.CannotInsertNull("name", "msdb.dbo.sysmail_profile"), 10, 0);
        }
        if (this.AgentScalar(batch, "select count(*) from dbo.sysmail_profile where name = @name", ("name", SqlValue.FromNVarchar(name)))!.Value.AsInt32 > 0)
        {
            this.SpendMailIdentity("sysmail_profile");
            throw MailWriteRefusal(batch, SimulatedSqlException.ViolationOfKeyConstraint("UNIQUE KEY", "SYSMAIL_PROFILE_NameMustBeUnique", "dbo.sysmail_profile", name), 10, 0);
        }
        var profileId = this.AgentScalar(batch, """
            insert dbo.sysmail_profile (name, description, last_mod_datetime, last_mod_user) values (@name, @description, getdate(), @user);
            select scope_identity();
            """, ("name", SqlValue.FromNVarchar(name)), ("description", call.Values[1]), ("user", MailUser(batch)))!.Value.CoerceTo(SqlType.Int32);
        call.SetOutput(2, profileId);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SysmailUpdateProfileParameters =
    [
        AgentParameter("profile_id", SqlType.Int32),
        AgentParameter("profile_name", AgentName, 128),
        AgentParameter("description", AgentText, 256),
    ];

    /// <summary>
    /// <c>sysmail_update_profile_sp</c>: the profile by id (renamed by a name
    /// beside it) or by name; Msg 14610 with nothing to change, a name another
    /// profile holds Msg 2627 answering 0.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailUpdateProfile(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_update_profile_sp", SysmailUpdateProfileParameters) is not { } call)
            yield break;
        int profileId;
        string? newName = null;
        if (call.Int(0) is { } id)
        {
            profileId = this.VerifyMailObject(batch, "profile", id, null, 1)!.Value;
            newName = call.Text(1);
        }
        else
        {
            profileId = this.VerifyMailObject(batch, "profile", null, call.Text(1), 1)!.Value;
        }
        if (newName is null && call.IsNull(2))
            throw MailRefusal(SimulatedSqlException.MailProfileUpdateNeedsChange(), 32, 1);
        if (newName is not null
            && this.AgentScalar(batch, "select count(*) from dbo.sysmail_profile where name = @name and profile_id <> @id",
                ("name", SqlValue.FromNVarchar(newName)), ("id", SqlValue.FromInt32(profileId)))!.Value.AsInt32 > 0)
        {
            throw MailWriteRefusal(batch, SimulatedSqlException.ViolationOfKeyConstraint("UNIQUE KEY", "SYSMAIL_PROFILE_NameMustBeUnique", "dbo.sysmail_profile", newName), 21, 0);
        }
        _ = this.AgentSql(batch, """
            update dbo.sysmail_profile set name = isnull(@name, name), description = isnull(@description, description),
                last_mod_datetime = getdate(), last_mod_user = @user
            where profile_id = @id
            """, ("id", SqlValue.FromInt32(profileId)), ("name", MailText(newName)), ("description", call.Values[2]), ("user", MailUser(batch)));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SysmailDeleteProfileParameters =
    [
        AgentParameter("profile_id", SqlType.Int32),
        AgentParameter("profile_name", AgentName, 128),
        AgentParameter("force_delete", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary><c>sysmail_delete_profile_sp</c>: the profile with its accounts and principals.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailDeleteProfile(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_delete_profile_sp", SysmailDeleteProfileParameters) is not { } call)
            yield break;
        var profileId = SqlValue.FromInt32(this.VerifyMailObject(batch, "profile", call.Int(0), call.Text(1), 1)!.Value);
        _ = this.AgentSql(batch, """
            delete dbo.sysmail_profileaccount where profile_id = @id;
            delete dbo.sysmail_principalprofile where profile_id = @id;
            delete dbo.sysmail_profile where profile_id = @id;
            """, ("id", profileId));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SysmailProfileIdentifierParameters =
    [
        AgentParameter("profile_id", SqlType.Int32),
        AgentParameter("profile_name", AgentName, 128),
    ];

    /// <summary><c>sysmail_help_profile_sp</c>: every profile, or the one named.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailHelpProfile(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_help_profile_sp", SysmailProfileIdentifierParameters) is not { } call)
            yield break;
        var profileId = this.VerifyMailObject(batch, "profile", call.Int(0), call.Text(1), 1, allowBothNull: true);
        var rows = this.AgentSql(batch, "select profile_id, name, description from dbo.sysmail_profile where @id is null or profile_id = @id order by profile_id",
            ("id", MailInt(profileId)));
        yield return this.AgentResultSet(["profile_id int", "name nvarchar(128)", "description nvarchar(256) null"], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    // ---- the accounts a profile sends through ----

    private static readonly SystemProcedureParameter[] SysmailProfileAccountParameters =
    [
        AgentParameter("profile_id", SqlType.Int32),
        AgentParameter("profile_name", AgentName, 128),
        AgentParameter("account_id", SqlType.Int32),
        AgentParameter("account_name", AgentName, 128),
        AgentRequired("sequence_number", SqlType.Int32),
    ];

    private static readonly SystemProcedureParameter[] SysmailProfileAccountKeyParameters = SysmailProfileAccountParameters[..4];

    /// <summary>
    /// <c>sysmail_add_profileaccount_sp</c>, <c>sysmail_update_profileaccount_sp</c>,
    /// <c>sysmail_delete_profileaccount_sp</c> and <c>sysmail_help_profileaccount_sp</c>:
    /// the profile verified first (answering 1), then the account (2). Adding
    /// a pair already there is Msg 2627 answering 0; updating takes a
    /// sequence number (Msg 14611, answering 3) and changes nothing for a
    /// pair that isn't there; deleting needs one side (Msg 14608, answering
    /// 3) and removes every pair it names.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailProfileAccount(BatchContext batch, string? returnCodeVar, MailChange change)
    {
        var procedure = change switch
        {
            MailChange.Add => "sysmail_add_profileaccount_sp",
            MailChange.Update => "sysmail_update_profileaccount_sp",
            MailChange.Delete => "sysmail_delete_profileaccount_sp",
            _ => "sysmail_help_profileaccount_sp",
        };
        var parameters = change is MailChange.Add or MailChange.Update ? SysmailProfileAccountParameters : SysmailProfileAccountKeyParameters;
        if (this.BindMailCall(batch, procedure, parameters) is not { } call)
            yield break;
        var bothOptional = change is MailChange.Delete or MailChange.Help;
        if (change == MailChange.Delete && call.IsNull(0) && call.IsNull(1) && call.IsNull(2) && call.IsNull(3))
            throw MailRefusal(SimulatedSqlException.MailEitherParameter("profile", "account"), 36, 3);
        var profileId = this.VerifyMailObject(batch, "profile", call.Int(0), call.Text(1), 1, bothOptional);
        var accountId = this.VerifyMailObject(batch, "account", call.Int(2), call.Text(3), 2, bothOptional);
        var key = new[] { ("profile", MailInt(profileId)), ("account", MailInt(accountId)), ("user", MailUser(batch)) };
        switch (change)
        {
            case MailChange.Add:
                if (this.AgentScalar(batch, "select count(*) from dbo.sysmail_profileaccount where profile_id = @profile and account_id = @account", key)!.Value.AsInt32 > 0)
                {
                    throw MailWriteRefusal(batch, SimulatedSqlException.ViolationOfKeyConstraint("PRIMARY KEY", "SYSMAIL_ACCOUNT_ProfileAccountMustBeUnique",
                        "dbo.sysmail_profileaccount", $"{profileId}, {accountId}"), 24, 0);
                }
                _ = this.AgentSql(batch, """
                    insert dbo.sysmail_profileaccount (profile_id, account_id, sequence_number, last_mod_datetime, last_mod_user)
                    values (@profile, @account, @sequence, getdate(), @user)
                    """, [.. key, ("sequence", call.Values[4])]);
                break;
            case MailChange.Update:
                if (call.IsNull(4))
                    throw MailRefusal(SimulatedSqlException.MailSequenceNumberRequired(), 25, 3);
                _ = this.AgentSql(batch, """
                    update dbo.sysmail_profileaccount set sequence_number = @sequence, last_mod_datetime = getdate(), last_mod_user = @user
                    where profile_id = @profile and account_id = @account
                    """, [.. key, ("sequence", call.Values[4])]);
                break;
            case MailChange.Delete:
                _ = this.AgentSql(batch, """
                    delete dbo.sysmail_profileaccount where (@profile is null or profile_id = @profile) and (@account is null or account_id = @account)
                    """, key);
                break;
            default:
                var rows = this.AgentSql(batch, """
                    select p.profile_id, p.name, a.account_id, a.name, pa.sequence_number
                    from dbo.sysmail_profileaccount as pa join dbo.sysmail_profile as p on p.profile_id = pa.profile_id
                        join dbo.sysmail_account as a on a.account_id = pa.account_id
                    where (@profile is null or pa.profile_id = @profile) and (@account is null or pa.account_id = @account)
                    order by pa.profile_id, pa.sequence_number, pa.account_id
                    """, key);
                yield return this.AgentResultSet(
                    ["profile_id int", "profile_name nvarchar(128)", "account_id int", "account_name nvarchar(128)", "sequence_number int null"], rows);
                break;
        }
        AgentReturn(batch, returnCodeVar, 0);
    }

    // ---- the principals a profile serves ----

    private static readonly SystemProcedureParameter[] SysmailPrincipalProfileParameters =
    [
        AgentParameter("principal_id", SqlType.Int32),
        AgentParameter("principal_name", AgentName, 128),
        AgentParameter("profile_id", SqlType.Int32),
        AgentParameter("profile_name", AgentName, 128),
        AgentRequired("is_default", SqlType.Bit),
    ];

    private static readonly SystemProcedureParameter[] SysmailPrincipalProfileKeyParameters = SysmailPrincipalProfileParameters[..4];

    /// <summary>
    /// <c>sysmail_add_principalprofile_sp</c> and its update, delete and help
    /// siblings. A principal is an msdb user or <c>public</c>, which makes a
    /// profile public; a grant is kept by the principal's sid. Marking a
    /// profile a principal's default clears that principal's other defaults;
    /// a NULL <c>@is_default</c> is the write's own Msg 515, a pair already
    /// granted Msg 2627, each answering 0. The listing names a grant's
    /// principal by sid, so <c>public</c>'s reads as <c>guest</c>, whose sid
    /// it shares, unless the call names <c>public</c>.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailPrincipalProfile(BatchContext batch, string? returnCodeVar, MailChange change)
    {
        var procedure = change switch
        {
            MailChange.Add => "sysmail_add_principalprofile_sp",
            MailChange.Update => "sysmail_update_principalprofile_sp",
            MailChange.Delete => "sysmail_delete_principalprofile_sp",
            _ => "sysmail_help_principalprofile_sp",
        };
        var parameters = change is MailChange.Add or MailChange.Update ? SysmailPrincipalProfileParameters : SysmailPrincipalProfileKeyParameters;
        if (this.BindMailCall(batch, procedure, parameters) is not { } call)
            yield break;
        var bothOptional = change is MailChange.Delete or MailChange.Help;
        if (change == MailChange.Delete && call.IsNull(0) && call.IsNull(1) && call.IsNull(2) && call.IsNull(3))
            throw MailRefusal(SimulatedSqlException.MailEitherParameter("principal", "profile"), 50, 6);
        var (principalCode, profileCode) = change == MailChange.Add ? (2, 3) : change == MailChange.Help ? (2, 1) : (1, 2);
        var principal = this.VerifyMailPrincipal(call.Int(0), call.Text(1), principalCode, bothOptional);
        var profileId = this.VerifyMailObject(batch, "profile", call.Int(2), call.Text(3), profileCode, bothOptional);
        var sid = principal is { } named ? MailSid(named.Sid) : SqlValue.Null(VarbinarySqlType.Get(85));
        var key = new[] { ("profile", MailInt(profileId)), ("sid", sid), ("user", MailUser(batch)) };
        switch (change)
        {
            case MailChange.Add:
            case MailChange.Update:
                var line = change == MailChange.Add ? 35 : 34;
                if (call.IsNull(4))
                {
                    throw MailWriteRefusal(batch, SimulatedSqlException.CannotInsertNull("is_default", "msdb.dbo.sysmail_principalprofile",
                        change == MailChange.Add ? "INSERT" : "UPDATE"), line, 0);
                }
                var exists = this.AgentScalar(batch, "select count(*) from dbo.sysmail_principalprofile where profile_id = @profile and principal_sid = @sid", key)!.Value.AsInt32 > 0;
                if (change == MailChange.Add && exists)
                {
                    throw MailWriteRefusal(batch, SimulatedSqlException.ViolationOfKeyConstraint("PRIMARY KEY", "SYSMAIL_PRINCIPALPROFILE_ProfilePrincipalMustBeUnique",
                        "dbo.sysmail_principalprofile", $"{profileId}, 0x{Convert.ToHexStringLower(principal!.Value.Sid)}"), 35, 0);
                }
                if (change == MailChange.Update && !exists)
                    break;
                if (call.Int(4) == 1)
                {
                    _ = this.AgentSql(batch, """
                        update dbo.sysmail_principalprofile set is_default = 0, last_mod_datetime = getdate(), last_mod_user = @user
                        where principal_sid = @sid and is_default = 1 and profile_id <> @profile
                        """, key);
                }
                _ = this.AgentSql(batch, change == MailChange.Add
                    ? "insert dbo.sysmail_principalprofile (profile_id, principal_sid, is_default, last_mod_datetime, last_mod_user) values (@profile, @sid, @default, getdate(), @user)"
                    : "update dbo.sysmail_principalprofile set is_default = @default, last_mod_datetime = getdate(), last_mod_user = @user where profile_id = @profile and principal_sid = @sid",
                    [.. key, ("default", call.Values[4])]);
                break;
            case MailChange.Delete:
                _ = this.AgentSql(batch, "delete dbo.sysmail_principalprofile where (@profile is null or profile_id = @profile) and (@sid is null or principal_sid = @sid)", key);
                break;
            default:
                var rows = this.AgentSql(batch, """
                    select pp.principal_sid, p.profile_id, p.name, pp.is_default
                    from dbo.sysmail_principalprofile as pp join dbo.sysmail_profile as p on p.profile_id = pp.profile_id
                    where (@profile is null or pp.profile_id = @profile) and (@sid is null or pp.principal_sid = @sid)
                    order by p.name, pp.principal_sid
                    """, key);
                var listed = new List<SqlValue[]>(rows.Count);
                foreach (var row in rows)
                {
                    var holder = principal?.Principal ?? this.MailPrincipalBySid(row[0].AsBytes);
                    if (holder is null)
                        continue;
                    listed.Add([SqlValue.FromInt32(holder.PrincipalId), SqlValue.FromNVarchar(holder.Name), row[1], row[2], row[3]]);
                }
                yield return this.AgentResultSet(["principal_id int", "principal_name nvarchar(128)", "profile_id int", "profile_name nvarchar(128)", "is_default bit"], listed);
                break;
        }
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>The msdb user a grant's sid names, by principal id: <c>guest</c> for <c>0x00</c>, which <c>public</c>'s grants carry.</summary>
    private DatabasePrincipal? MailPrincipalBySid(byte[] sid)
    {
        var msdb = this.Databases[MsdbDatabaseName];
        DatabasePrincipal? found = null;
        foreach (var (_, principal) in msdb.Principals)
        {
            if (principal.PrincipalId == Database.PublicPrincipalId || !IsMailPrincipal(principal)
                || BuiltInResources.DatabasePrincipalSid(msdb, principal) is not { } candidate || !candidate.AsSpan().SequenceEqual(sid))
            {
                continue;
            }
            if (found is null || principal.PrincipalId < found.PrincipalId)
                found = principal;
        }
        return found;
    }

    // ---- settings ----

    private static readonly SystemProcedureParameter[] SysmailConfigureParameters =
    [
        AgentRequired("parameter_name", AgentText, 256),
        AgentRequired("parameter_value", AgentText, 256),
        AgentParameter("description", AgentText, 256),
    ];

    /// <summary>
    /// <c>sysmail_configure_sp</c>: sets a setting's value (NULL included,
    /// nothing checked) and, when given, its description; a name that is no
    /// setting changes nothing.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailConfigure(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_configure_sp", SysmailConfigureParameters) is not { } call)
            yield break;
        _ = this.AgentSql(batch, """
            update dbo.sysmail_configuration set paramvalue = @value, description = isnull(@description, description),
                last_mod_datetime = getdate(), last_mod_user = @user
            where paramname = @name
            """, ("name", call.Values[0]), ("value", call.Values[1]), ("description", call.Values[2]), ("user", MailUser(batch)));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary><c>sysmail_help_configure_sp</c>: every setting, or the one named (none for a name that is no setting).</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailHelpConfigure(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_help_configure_sp", [AgentParameter("parameter_name", AgentText, 256)]) is not { } call)
            yield break;
        var rows = this.AgentSql(batch, "select paramname, paramvalue, description from dbo.sysmail_configuration where @name is null or paramname = @name order by paramname",
            ("name", call.Values[0]));
        yield return this.AgentResultSet(["paramname nvarchar(256)", "paramvalue nvarchar(256) null", "description nvarchar(256) null"], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary><c>sysmail_help_configure_value_sp</c>: one setting's value into its <c>OUTPUT</c> parameter, NULL for a name that is no setting.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSysmailHelpConfigureValue(BatchContext batch, string? returnCodeVar)
    {
        if (this.BindMailCall(batch, "sysmail_help_configure_value_sp", [AgentRequired("parameter_name", AgentText, 256), AgentRequired("parameter_value", AgentText, 256)]) is not { } call)
            yield break;
        call.SetOutput(1, this.MailSetting(batch, call.Text(0)!) is { } value ? SqlValue.FromNVarchar(value) : SqlValue.Null(SqlType.NVarchar));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>The value of the setting <paramref name="name"/>, or null for none or a NULL value.</summary>
    private string? MailSetting(BatchContext batch, string name) =>
        this.AgentScalar(batch, "select paramvalue from dbo.sysmail_configuration where paramname = @name", ("name", SqlValue.FromNVarchar(name))) is { IsNull: false } value
            ? value.AsString
            : null;

    /// <summary>An integer setting, or <paramref name="fallback"/> when it is missing or isn't a number.</summary>
    private int MailIntSetting(BatchContext batch, string name, int fallback) =>
        int.TryParse(this.MailSetting(batch, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
