using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// msdb's Database Mail as SQL Server 2025 on Linux runs it (probed
/// 2026-10-16): accounts, profiles and their grants kept in msdb's tables,
/// <c>sp_send_dbmail</c> refused while <c>Database Mail XPs</c> is off, and a
/// queued item failing as real's fails against a mail server it can't reach.
/// </summary>
[TestClass]
public sealed class DatabaseMailTests
{
    private const string EnableXps = """
        exec sp_configure 'show advanced options', 1; reconfigure;
        exec sp_configure 'Database Mail XPs', 1; reconfigure;
        """;

    /// <summary>A simulation with Database Mail enabled and a public default profile <c>p</c> sending through account <c>a</c>.</summary>
    private static Simulation Mail(Func<string, Stream?>? files = null)
    {
        var simulation = new Simulation { OpenBulkFile = files };
        _ = simulation.ExecuteNonQuery(EnableXps + """
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'a@example.invalid', @mailserver_name = N'smtp.example.invalid';
            exec msdb.dbo.sysmail_add_profile_sp @profile_name = N'p';
            exec msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'p', @account_name = N'a', @sequence_number = 1;
            exec msdb.dbo.sysmail_add_principalprofile_sp @principal_name = N'public', @profile_name = N'p', @is_default = 1;
            """);
        return simulation;
    }

    private static List<string> Messages(Simulation simulation, string commandText)
    {
        var messages = new List<string>();
        using var connection = simulation.CreateOpenConnection();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand(commandText).ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void SendDbMail_WithXpsOff_IsBlocked_LeavingTheReturnCode()
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError("declare @rc int; exec @rc = msdb.dbo.sp_send_dbmail @recipients = 'x@example.invalid', @subject = N's'", 15281);
        AreEqual("SQL Server blocked access to procedure 'dbo.sp_send_dbmail' of component 'Database Mail XPs' because this component is turned off as part of the security configuration for this server. A system administrator can enable the use of 'Database Mail XPs' by using sp_configure. For more information about enabling 'Database Mail XPs', search for 'Database Mail XPs' in SQL Server Books Online.", ex.Errors[0].Message);
        AreEqual("msdb.dbo.sp_send_dbmail", ex.Errors[0].Procedure);
        AreEqual(0, ex.Errors[0].LineNumber);
        AreEqual("15281||0", simulation.ExecuteScalar("""
            declare @rc int, @caught int;
            begin try exec @rc = msdb.dbo.sysmail_help_status_sp; end try begin catch set @caught = error_number(); end catch
            select concat(@caught, '|', @rc, '|', (select count(*) from msdb.dbo.sysmail_allitems where 1 = 0))
            """));
    }

    [TestMethod]
    [DataRow("xp_sysmail_activate")]
    [DataRow("xp_sysmail_attachment_load")]
    [DataRow("xp_sysmail_format_query")]
    public void SysmailExtendedProcedures_WithXpsOff_AreBlocked(string procedure)
    {
        var ex = new Simulation().AssertSqlError($"exec {procedure}", 15281);
        AreEqual($"SQL Server blocked access to procedure 'sys.{procedure}' of component 'Database Mail XPs' because this component is turned off as part of the security configuration for this server. A system administrator can enable the use of 'Database Mail XPs' by using sp_configure. For more information about enabling 'Database Mail XPs', search for 'Database Mail XPs' in SQL Server Books Online.", ex.Errors[0].Message);
        AreEqual(1, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void Configuration_ShipsRealsSettings_AndConfigureChangesThem()
        => AreEqual("AccountRetryAttempts=3 (x);AccountRetryDelay=60;DatabaseMailExeMinimumLifeTime=600;DefaultAttachmentEncoding=MIME;LoggingLevel=2;MaxFileSize=1000000;ProhibitedExtensions=exe,dll,vbs,js|3|", new Simulation().ExecuteScalar("""
            exec msdb.dbo.sysmail_configure_sp N'accountretryattempts', N'3', N'x';
            exec msdb.dbo.sysmail_configure_sp N'NoSuchSetting', N'1';
            declare @v nvarchar(256), @none nvarchar(256);
            exec msdb.dbo.sysmail_help_configure_value_sp N'AccountRetryAttempts', @v output;
            exec msdb.dbo.sysmail_help_configure_value_sp N'NoSuchSetting', @none output;
            select (select string_agg(paramname + '=' + paramvalue + iif(paramname = 'AccountRetryAttempts', ' (' + description + ')', ''), ';')
                within group (order by paramname) from msdb.dbo.sysmail_configuration) + '|' + @v + '|' + isnull(@none, '')
            """));

    [TestMethod]
    public void AddAccount_DefaultsItsServer_AndKeepsAUserNameInACredential()
        => AreEqual("1|SMTP|SIMULATED|25|0|0|NULL|2|u|1", new Simulation().ExecuteScalar("""
            declare @a int, @b int;
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'a@example.invalid', @account_id = @a output;
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'b', @email_address = N'b@example.invalid', @mailserver_name = N'm', @port = 587,
                @username = N'u', @password = N'secret', @enable_ssl = 1, @account_id = @b output;
            select concat(@a, '|', s.servertype, '|', s.servername, '|', s.port, '|', s.use_default_credentials, '|', s.enable_ssl, '|',
                isnull(cast(s.credential_id as varchar), 'NULL'), '|', @b, '|', (select username from msdb.dbo.sysmail_server where account_id = @b), '|',
                (select count(*) from sys.credentials c join msdb.dbo.sysmail_server s2 on s2.credential_id = c.credential_id where c.credential_identity = N'u'))
            from msdb.dbo.sysmail_server s where s.account_id = @a
            """));

    [TestMethod]
    public void AddAccount_Refusals_AnswerRealsCodes_AndSpendTheIdentity()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table codes (n int, rc int)");
        var ex = simulation.AssertSqlError("""
            declare @a int, @b int, @c int;
            exec @a = msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'x';
            exec @a = msdb.dbo.sysmail_add_account_sp @account_name = N'A', @email_address = N'x';
            insert codes values (1, @a);
            exec @b = msdb.dbo.sysmail_add_account_sp @account_name = N'b', @email_address = N'x', @mailserver_type = N'POP3';
            insert codes values (2, @b);
            exec @c = msdb.dbo.sysmail_add_account_sp @account_name = N'c', @email_address = N'x', @mailserver_type = NULL;
            insert codes values (3, @c);
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'd', @email_address = N'x';
            """, 2627);
        AreEqual("Violation of UNIQUE KEY constraint 'SYSMAIL_ACCOUNT_NameMustBeUnique'. Cannot insert duplicate key in object 'dbo.sysmail_account'. The duplicate key value is (A).", ex.Errors[0].Message);
        AreEqual(37, ex.Errors[0].LineNumber);
        AreEqual("1:2;2:4;3:1|a:1,d:4", simulation.ExecuteScalar("""
            select (select string_agg(concat(n, ':', rc), ';') within group (order by n) from codes) + '|'
                + (select string_agg(concat(name, ':', account_id), ',') within group (order by account_id) from msdb.dbo.sysmail_account)
            """));
    }

    [TestMethod]
    [DataRow("@mailserver_type = NULL", 14614, "(null) is not a valid mailserver_type", 18)]
    [DataRow("@use_default_credentials = 1, @username = N'u'", 14666, "User name cannot be supplied when using default credentials", 25)]
    [DataRow("@password = N'p'", 14615, "The @username parameter needs to be supplied if the @password is supplied.", 32)]
    public void AddAccount_VerifiesItsParameters(string arguments, int number, string message, int line)
    {
        var ex = new Simulation().AssertSqlError($"exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'x', {arguments}", number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual("msdb.dbo.sysmail_verify_accountparams_sp", ex.Errors[0].Procedure);
        AreEqual(line, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void UpdateAccount_KeepsWhatIsLeftNull_ButDropsTheCredentialWithoutAUserName()
        => AreEqual("b|e2@x|D|m|26|1|30|NULL|0", new Simulation().ExecuteScalar("""
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'x', @username = N'u';
            exec msdb.dbo.sysmail_update_account_sp @account_id = 1, @account_name = N'b', @email_address = N'e@x', @display_name = N'D',
                @mailserver_name = N'm', @port = 26, @enable_ssl = 1, @timeout = 30, @username = N'u', @no_credential_change = 1;
            exec msdb.dbo.sysmail_update_account_sp @account_name = N'b', @email_address = N'e2@x';
            select concat(a.name, '|', a.email_address, '|', a.display_name, '|', s.servername, '|', s.port, '|', s.enable_ssl, '|', s.timeout, '|',
                isnull(s.username, 'NULL'), '|', (select count(*) from sys.credentials))
            from msdb.dbo.sysmail_account a join msdb.dbo.sysmail_server s on s.account_id = a.account_id
            """));

    [TestMethod]
    [DataRow("@account_id = 9", 14606, "account id is not valid", 33)]
    [DataRow("@account_name = N'nope'", 14607, "account name is not valid", 42)]
    [DataRow("@account_id = 1, @account_name = N'nope'", 14605, "Both account parameters (id and name) do not point to the same object", 24)]
    [DataRow("@account_id = NULL", 14604, "Both account parameters (id and name) cannot be NULL", 14)]
    public void DeleteAccount_VerifiesTheAccount(string arguments, int number, string message, int line)
    {
        var ex = new Simulation().AssertSqlError($"""
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'x';
            exec msdb.dbo.sysmail_delete_account_sp {arguments}
            """, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual("msdb.dbo.sysmail_verify_account_sp", ex.Errors[0].Procedure);
        AreEqual(line, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void Profiles_RefuseADuplicateAnsweringZero_AndDeleteTakesTheirGrants()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table codes (rc int, id int)");
        simulation.AssertSqlError("""
            declare @rc int = -1, @id int;
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'x';
            exec msdb.dbo.sysmail_add_profile_sp N'p', N'd', @id output;
            exec @rc = msdb.dbo.sysmail_add_profile_sp N'P';
            insert codes values (@rc, @id);
            """, 2627, "Violation of UNIQUE KEY constraint 'SYSMAIL_PROFILE_NameMustBeUnique'. Cannot insert duplicate key in object 'dbo.sysmail_profile'. The duplicate key value is (P).");
        AreEqual("0|1|3|p2:x;q:|0|0", simulation.ExecuteScalar("""
            exec msdb.dbo.sysmail_add_profile_sp N'q';
            exec msdb.dbo.sysmail_update_profile_sp @profile_id = 1, @profile_name = N'p2', @description = N'x';
            exec msdb.dbo.sysmail_add_profile_sp N'gone';
            exec msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'gone', @account_name = N'a', @sequence_number = 1;
            exec msdb.dbo.sysmail_add_principalprofile_sp @principal_name = N'public', @profile_name = N'gone', @is_default = 1;
            exec msdb.dbo.sysmail_delete_profile_sp @profile_name = N'gone';
            select concat((select rc from codes), '|', (select id from codes), '|', (select max(profile_id) from msdb.dbo.sysmail_profile), '|',
                (select string_agg(name + ':' + isnull(description, ''), ';') within group (order by name) from msdb.dbo.sysmail_profile), '|',
                (select count(*) from msdb.dbo.sysmail_profileaccount), '|', (select count(*) from msdb.dbo.sysmail_principalprofile))
            """));
    }

    [TestMethod]
    public void UpdateProfile_WithNothingToChange_IsMsg14610()
        => new Simulation().AssertSqlError("""
            exec msdb.dbo.sysmail_add_profile_sp N'p';
            exec msdb.dbo.sysmail_update_profile_sp @profile_id = 1
            """, 14610, "Either @profile_name or @description parameter needs to be specified for update");

    [TestMethod]
    public void ProfileAccounts_ListInSequence_AndRefuseAsReal()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'a', @email_address = N'x';
            exec msdb.dbo.sysmail_add_account_sp @account_name = N'b', @email_address = N'x';
            exec msdb.dbo.sysmail_add_profile_sp N'p';
            exec msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'p', @account_name = N'a', @sequence_number = 2;
            exec msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'p', @account_name = N'b', @sequence_number = 1;
            exec msdb.dbo.sysmail_update_profileaccount_sp @profile_name = N'p', @account_name = N'a', @sequence_number = 0;
            """);
        using (var reader = simulation.ExecuteReader("exec msdb.dbo.sysmail_help_profileaccount_sp @profile_name = N'p'"))
        {
            var rows = reader.EnumerateRecords().Select(static r => $"{r.GetString(1)}:{r.GetString(3)}:{r.GetInt32(4)}").ToList();
            AreEqual("p:a:0,p:b:1", string.Join(",", rows));
        }
        simulation.AssertSqlError("exec msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'p', @account_name = N'a', @sequence_number = 5", 2627,
            "Violation of PRIMARY KEY constraint 'SYSMAIL_ACCOUNT_ProfileAccountMustBeUnique'. Cannot insert duplicate key in object 'dbo.sysmail_profileaccount'. The duplicate key value is (1, 1).");
        _ = simulation.AssertSqlError("exec msdb.dbo.sysmail_update_profileaccount_sp @profile_name = N'p', @account_name = N'a', @sequence_number = NULL", 14611);
        simulation.AssertSqlError("exec msdb.dbo.sysmail_delete_profileaccount_sp", 14608, "Either profile or account parameter needs to be supplied");
        _ = simulation.ExecuteNonQuery("create table codes (n int, rc int)");
        _ = simulation.AssertSqlError("""
            declare @a int, @b int, @c int;
            exec @a = msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'p', @account_name = N'nope', @sequence_number = 1;
            exec @b = msdb.dbo.sysmail_add_profileaccount_sp @profile_name = N'nope', @account_name = N'a', @sequence_number = 1;
            exec @c = msdb.dbo.sysmail_delete_profileaccount_sp;
            insert codes values (1, @a), (2, @b), (3, @c);
            """, 14607);
        AreEqual("1:2;2:1;3:3|0", simulation.ExecuteScalar("""
            exec msdb.dbo.sysmail_delete_profileaccount_sp @profile_name = N'p';
            select (select string_agg(concat(n, ':', rc), ';') within group (order by n) from codes) + '|'
                + cast((select count(*) from msdb.dbo.sysmail_profileaccount) as varchar)
            """));
    }

    [TestMethod]
    public void PrincipalProfiles_KeepPublicAsSidZero_AndOneDefaultPerPrincipal()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create login l with password = 'Pa55word!x';
            use msdb;
            create user u for login l;
            exec sysmail_add_profile_sp N'p1';
            exec sysmail_add_profile_sp N'p2';
            exec sysmail_add_principalprofile_sp @principal_name = N'public', @profile_name = N'p1', @is_default = 1;
            exec sysmail_add_principalprofile_sp @principal_name = N'u', @profile_name = N'p1', @is_default = 1;
            exec sysmail_add_principalprofile_sp @principal_name = N'u', @profile_name = N'p2', @is_default = 1;
            """);
        AreEqual("1:0x00:1;1:u:0;2:u:1", simulation.ExecuteScalar("""
            select string_agg(concat(profile_id, ':', iif(principal_sid = 0x00, '0x00', 'u'), ':', is_default), ';')
                within group (order by profile_id, principal_sid)
            from msdb.dbo.sysmail_principalprofile
            """));
        using (var reader = simulation.ExecuteReader("exec msdb.dbo.sysmail_help_principalprofile_sp @profile_name = N'p1'"))
            AreEqual("2:guest,25:u", string.Join(",", reader.EnumerateRecords().Select(static r => $"{r.GetInt32(0)}:{r.GetString(1)}")));
        using (var reader = simulation.ExecuteReader("exec msdb.dbo.sysmail_help_principalprofile_sp @principal_name = N'public'"))
            AreEqual("0:public:p1", string.Join(",", reader.EnumerateRecords().Select(static r => $"{r.GetInt32(0)}:{r.GetString(1)}:{r.GetString(3)}")));
        var ex = simulation.AssertSqlError("exec msdb.dbo.sysmail_add_principalprofile_sp @principal_name = N'DatabaseMailUserRole', @profile_name = N'p1', @is_default = 0", 14607);
        AreEqual("msdb.dbo.sysmail_verify_principal_sp", ex.Errors[0].Procedure);
        AreEqual(49, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void SendDbMail_QueuesAnItem_ThatFailsAsAnUnreachableServerFails()
    {
        var simulation = Mail();
        var messages = Messages(simulation, """
            declare @rc int, @id int;
            exec @rc = msdb.dbo.sp_send_dbmail @recipients = 'x@example.invalid', @subject = N'hello', @body = N'b', @mailitem_id = @id output;
            if @rc <> 0 or @id <> 1 throw 50000, 'unexpected', 1;
            """);
        AreEqual("0:Mail (Id: 1) queued.", string.Join("\n", messages));
        AreEqual("1|1|TEXT|NORMAL|NORMAL|MIME|failed|0|1|1", simulation.ExecuteScalar("""
            select concat(mailitem_id, '|', profile_id, '|', body_format, '|', importance, '|', sensitivity, '|', attachment_encoding, '|', sent_status, '|',
                datepart(millisecond, sent_date), '|', (select count(*) from msdb.dbo.sysmail_faileditems),
                '|', (select send_attempts from msdb.dbo.sysmail_send_retries where mailitem_id = 1))
            from msdb.dbo.sysmail_allitems
            """));
        AreEqual("information:DatabaseMail process is started:;error:The mail could not be sent to the recipients because of the mail server failure. (Sending Mail using Account 1:1;error:The mail could not be sent to the recipients because of the mail server failure. (Sending Mail using Account 1:1", simulation.ExecuteScalar("""
            select string_agg(concat(event_type, ':', left(description, iif(event_type = 'error', 110, 100)), ':', mailitem_id), ';') within group (order by log_id)
            from msdb.dbo.sysmail_event_log
            """));
        Contains("Message: Could not connect to mail server. (No such host is known)", (string)simulation.ExecuteScalar("select max(description) from msdb.dbo.sysmail_log")!);
    }

    [TestMethod]
    public void SendDbMail_ThroughAProfileWithoutAccounts_FailsWithAnErrorNamingNoItem()
        => AreEqual("failed|1", Mail().ExecuteScalar("""
            exec msdb.dbo.sysmail_add_profile_sp N'empty';
            exec msdb.dbo.sp_send_dbmail @profile_name = N'empty', @recipients = 'x@example.invalid', @subject = N's';
            select concat((select sent_status from msdb.dbo.sysmail_allitems),
                '|', (select count(*) from msdb.dbo.sysmail_event_log where mailitem_id is null and description like '%the list of accounts which could send this mail is empty%'))
            """));

    [TestMethod]
    [DataRow("@profile_name = N'nope', @recipients = 'x', @subject = N's'", 14607, 42, 4)]
    [DataRow("@recipients = 'x', @subject = N's', @attach_query_result_as_file = NULL", 14618, 151, 2)]
    [DataRow("@recipients = 'x', @subject = N's', @exclude_query_output = NULL", 14618, 158, 2)]
    [DataRow("@recipients = 'x', @subject = N's', @body_format = 'rtf'", 14626, 194, 13)]
    [DataRow("@recipients = 'x', @subject = N's', @importance = 'urgent'", 14622, 211, 16)]
    [DataRow("@recipients = 'x', @subject = N's', @sensitivity = 'secret'", 14623, 227, 18)]
    [DataRow("@recipients = 'x', @subject = N''", 14624, 242, 19)]
    [DataRow("@recipients = '', @subject = N's'", 14624, 260, 20)]
    [DataRow("@recipients = 'x', @file_attachments = N'/f.exe'", 14630, 62, 1)]
    public void SendDbMail_RefusesInRealsOrder_AnsweringRealsCode(string arguments, int number, int line, int returnCode)
    {
        var simulation = Mail();
        _ = simulation.ExecuteNonQuery("create table codes (rc int)");
        var ex = simulation.AssertSqlError($"declare @rc int; exec @rc = msdb.dbo.sp_send_dbmail {arguments}; insert codes values (@rc)", number);
        AreEqual(line, ex.Errors[0].LineNumber);
        AreEqual(returnCode, simulation.ExecuteScalar("select rc from codes"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from msdb.dbo.sysmail_mailitems"));
    }

    [TestMethod]
    public void SendDbMail_WithoutAProfileOrDefault_IsMsg14636()
        => Mail().AssertSqlError("""
            exec msdb.dbo.sysmail_update_principalprofile_sp @principal_name = N'public', @profile_name = N'p', @is_default = 0;
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's'
            """, 14636, "No global profile is configured. Specify a profile name in the @profile_name parameter.");

    [TestMethod]
    public void SendDbMail_QueryOutput_IsFormattedAsSqlcmdFormatsIt()
        => AreEqual("Head\r\na           bb \r\n----------- ---\r\n          1 xyz\r\n         22 NUL\r\n\r\n(2 rows affected)\r\n|1,xyz\r\n\r\n(1 rows affected)\r\n", Mail().ExecuteScalar("""
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @body = N'Head', @query = N'select 1 as a, N''xyz'' as bb union all select 22, null';
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @query = N'select 1 as a, N''xyz'' as bb', @query_result_header = 0,
                @query_result_separator = ',', @query_result_no_padding = 1;
            select (select body from msdb.dbo.sysmail_mailitems where mailitem_id = 1) + '|' + (select substring(body, 3, 100) from msdb.dbo.sysmail_mailitems where mailitem_id = 2)
            """));

    [TestMethod]
    public void SendDbMail_QueryWrapsAtTheResultWidth()
        => AreEqual("\r\na         \r\n  b       \r\n   \r\n----------\r\n- --------\r\n---\r\n          \r\n1         \r\n  2\r\n\r\n(1 rows affected)\r\n", Mail().ExecuteScalar("""
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @query = N'select 1 a, 2 b', @query_result_width = 10;
            select body from msdb.dbo.sysmail_mailitems
            """));

    [TestMethod]
    public void SendDbMail_QueryAsFile_AttachesUtf16WithAByteOrderMark()
        => AreEqual("see|res.txt|122|0xFFFE6100", Mail().ExecuteScalar("""
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @body = N'see', @query = N'select 1 as a', @attach_query_result_as_file = 1,
                @query_attachment_filename = N'res.txt';
            select concat(i.body, '|', a.filename, '|', a.filesize, '|', convert(varchar(10), substring(a.attachment, 1, 4), 1))
            from msdb.dbo.sysmail_mailitems i join msdb.dbo.sysmail_mailattachments a on a.mailitem_id = i.mailitem_id
            """));

    [TestMethod]
    public void SendDbMail_QueryErrors_AreMsg22050_SentPastTheTry()
    {
        var simulation = Mail();
        _ = simulation.ExecuteNonQuery("create table codes (n int, rc int, caught int)");
        var ex = simulation.AssertSqlError("""
            declare @rc int, @caught int;
            begin try exec @rc = msdb.dbo.sp_send_dbmail @recipients = 'x', @query = N'select 1/0'; end try begin catch set @caught = 1; end catch
            insert codes values (1, @rc, @caught);
            exec @rc = msdb.dbo.sp_send_dbmail @recipients = 'x', @query = N'select 1', @query_result_width = 8;
            insert codes values (2, @rc, null);
            """, 22050);
        AreEqual("Failed to initialize sqlcmd library with error number -2147467259.", ex.Errors[0].Message);
        AreEqual("Failed to initialize sqlcmd library with error number -2147024809.", ex.Errors[^1].Message);
        AreEqual("1:102:;2:102:|0", simulation.ExecuteScalar("""
            select (select string_agg(concat(n, ':', rc, ':', caught), ';') within group (order by n) from codes) + '|'
                + cast((select count(*) from msdb.dbo.sysmail_mailitems) as varchar)
            """));
    }

    [TestMethod]
    public void SendDbMail_FileAttachments_ReadThroughTheServerDisk()
    {
        var simulation = Mail(path => path == "/data/a.txt" ? new MemoryStream("hello"u8.ToArray()) : null);
        AreEqual("/data/a.txt|5|0x68656C6C6F", simulation.ExecuteScalar("""
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's', @file_attachments = N'/data/a.txt';
            select concat(filename, '|', filesize, '|', convert(varchar(20), attachment, 1)) from msdb.dbo.sysmail_mailattachments
            """));
        simulation.AssertSqlError("exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's', @file_attachments = N'/data/none.txt'", 22051,
            "Attachment file /data/none.txt is invalid.");
        simulation.AssertSqlError("""
            exec msdb.dbo.sysmail_configure_sp N'MaxFileSize', N'3';
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's', @file_attachments = N'/data/a.txt'
            """, 22051, "File attachment or query results size exceeds allowable value of 3 bytes.");
    }

    [TestMethod]
    public void DatabaseMailUserRole_SendsThroughPublicProfiles_AndSeesOnlyItsOwnItems()
    {
        var simulation = Mail(path => new MemoryStream([1]));
        _ = simulation.ExecuteNonQuery("""
            create login l with password = 'Pa55word!x';
            create login m with password = 'Pa55word!x';
            use msdb;
            create user u for login l;
            create user v for login m;
            alter role DatabaseMailUserRole add member u;
            exec sysmail_add_profile_sp N'private';
            exec sp_send_dbmail @recipients = 'x', @subject = N'mine';
            """);
        _ = simulation.AssertSqlError("use msdb; execute as login = 'm'; exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's'", 229);
        _ = simulation.AssertSqlError("use msdb; execute as login = 'l'; exec msdb.dbo.sysmail_help_profile_sp", 229);
        var ex = simulation.AssertSqlError("use msdb; execute as login = 'l'; exec msdb.dbo.sp_send_dbmail @profile_name = N'private', @recipients = 'x', @subject = N's'", 14607);
        AreEqual(141, ex.Errors[0].LineNumber);
        simulation.AssertSqlError("use msdb; execute as login = 'l'; exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's', @file_attachments = N'/f.txt'", 22051,
            "The client connection security context could not be impersonated. Attaching files require an integrated client login");
        AreEqual("2:l|2|STARTED", simulation.ExecuteScalar("""
            use msdb;
            execute as login = 'l';
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N'theirs';
            declare @status table (s varchar(7));
            insert @status exec msdb.dbo.sysmail_help_status_sp;
            select concat((select string_agg(concat(mailitem_id, ':', send_request_user), ',') from msdb.dbo.sysmail_allitems), '|',
                (select count(*) from msdb.dbo.sysmail_event_log), '|', (select s from @status))
            """));
    }

    [TestMethod]
    public void SendDbMail_RollsBackWithItsTransaction()
        => AreEqual("0|0", Mail().ExecuteScalar("""
            begin tran;
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's';
            rollback;
            select concat((select count(*) from msdb.dbo.sysmail_mailitems), '|', (select count(*) from msdb.dbo.sysmail_log))
            """));

    [TestMethod]
    public void ExcludeQueryOutput_SuppressesTheQueuedMessage()
        => IsEmpty(Messages(Mail(), "exec msdb.dbo.sp_send_dbmail @recipients = 'x', @query = N'select 1', @exclude_query_output = 1"));

    [TestMethod]
    public void StopAndStart_LogWhoDidIt_AndAStoppedQueueRefusesMail()
    {
        var simulation = Mail();
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's'; exec msdb.dbo.sysmail_stop_sp");
        AreEqual("STOPPED|status", simulation.ExecuteScalar("""
            declare @status table (s varchar(7));
            declare @queues table (queue_type nvarchar(6), length int, state nvarchar(64), e datetime, a datetime);
            insert @status exec msdb.dbo.sysmail_help_status_sp;
            insert @queues exec msdb.dbo.sysmail_help_queue_sp;
            select (select s from @status) + '|' + (select string_agg(queue_type, ',') from @queues)
            """));
        simulation.AssertSqlError("exec msdb.dbo.sp_send_dbmail @recipients = 'x', @subject = N's'", 14641,
            "Mail not queued. Database Mail is stopped. Use sysmail_start_sp to start Database Mail.");
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sysmail_start_sp");
        AreEqual("The mail queue stopped by login \"sa\".;DatabaseMail process is shutting down;The mail queue was started by login \"sa\".", simulation.ExecuteScalar("""
            select string_agg(description, ';') within group (order by log_id) from msdb.dbo.sysmail_log
            where log_id > 3 and description not like '%Exception%'
            """));
    }

    [TestMethod]
    public void DeleteMailItems_RemovesItemsAndAttachments_AndLogsTheCount()
    {
        var simulation = Mail();
        _ = simulation.ExecuteNonQuery("""
            exec msdb.dbo.sp_send_dbmail @recipients = 'x', @query = N'select 1', @attach_query_result_as_file = 1;
            exec msdb.dbo.sysmail_delete_mailitems_sp @sent_status = 'failed';
            """);
        AreEqual("0|0|0|Mail items deletion is initiated by user \"sa\". 1 items deleted.", simulation.ExecuteScalar("""
            select concat((select count(*) from msdb.dbo.sysmail_mailitems), '|', (select count(*) from msdb.dbo.sysmail_attachments), '|',
                (select count(*) from msdb.dbo.sysmail_send_retries), '|', (select top (1) description from msdb.dbo.sysmail_log order by log_id desc))
            """));
        simulation.AssertSqlError("exec msdb.dbo.sysmail_delete_mailitems_sp @sent_status = 'bogus'", 14266,
            "The specified '@sent_status' is invalid (valid values are: unsent, sent, failed, retrying).");
        simulation.AssertSqlError("exec msdb.dbo.sysmail_delete_mailitems_sp", 14608, "Either @sent_before or @sent_status parameter needs to be supplied");
        AreEqual(0, simulation.ExecuteScalar("exec msdb.dbo.sysmail_delete_log_sp @event_type = 'information'; exec msdb.dbo.sysmail_delete_log_sp @event_type = 'error'; select count(*) from msdb.dbo.sysmail_log"));
    }
}
