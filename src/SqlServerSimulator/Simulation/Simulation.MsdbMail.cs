using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Database Mail objects msdb ships: the sysmail tables, the rows a fresh
// instance holds in them, the six views over the mail items and the event log,
// and the SELECT grants DatabaseMailUserRole holds on the views, in SQL Server
// 2025's shapes (probed 2026-10-16). The procedures that fill them live in
// Simulation.DatabaseMail*.cs.
partial class Simulation
{
    private static readonly (string Name, string[] Columns)[] MsdbMailTables =
    [
        ("sysmail_account", [
            "account_id int identity", "name sysname", "description nvarchar(256) null", "email_address nvarchar(128)",
            "display_name nvarchar(128) null", "replyto_address nvarchar(128) null", "last_mod_datetime datetime", "last_mod_user sysname",
        ]),
        ("sysmail_attachments", [
            "attachment_id int identity", "mailitem_id int", "filename nvarchar(260)", "filesize int", "attachment varbinary(max) null",
            "last_mod_date datetime", "last_mod_user sysname",
        ]),
        ("sysmail_attachments_transfer", [
            "transfer_id int identity", "uid uniqueidentifier", "filename nvarchar(260)", "filesize int", "attachment varbinary(max) null",
            "create_date datetime",
        ]),
        ("sysmail_configuration", [
            "paramname nvarchar(256)", "paramvalue nvarchar(256) null", "description nvarchar(256) null", "last_mod_datetime datetime",
            "last_mod_user sysname",
        ]),
        ("sysmail_log", [
            "log_id int identity", "event_type int", "log_date datetime", "description nvarchar(max) null", "process_id int null",
            "mailitem_id int null", "account_id int null", "last_mod_date datetime", "last_mod_user sysname",
        ]),
        ("sysmail_mailitems", [
            "mailitem_id int identity", "profile_id int", "recipients varchar(max) null", "copy_recipients varchar(max) null",
            "blind_copy_recipients varchar(max) null", "subject nvarchar(255) null", "from_address varchar(max) null", "reply_to varchar(max) null",
            "body nvarchar(max) null", "body_format varchar(20) null", "importance varchar(6) null", "sensitivity varchar(12) null",
            "file_attachments nvarchar(max) null", "attachment_encoding varchar(20) null", "query nvarchar(max) null",
            "execute_query_database sysname null", "attach_query_result_as_file bit null", "query_result_header bit null",
            "query_result_width int null", "query_result_separator char(1) null", "exclude_query_output bit null",
            "append_query_error bit null", "send_request_date datetime", "send_request_user sysname", "sent_account_id int null",
            "sent_status tinyint null", "sent_date datetime null", "last_mod_date datetime", "last_mod_user sysname",
        ]),
        ("sysmail_principalprofile", [
            "profile_id int", "principal_sid varbinary(85)", "is_default bit", "last_mod_datetime datetime", "last_mod_user sysname",
        ]),
        ("sysmail_profile", [
            "profile_id int identity", "name sysname", "description nvarchar(256) null", "last_mod_datetime datetime", "last_mod_user sysname",
        ]),
        ("sysmail_profileaccount", [
            "profile_id int", "account_id int", "sequence_number int null", "last_mod_datetime datetime", "last_mod_user sysname",
        ]),
        ("sysmail_query_transfer", ["uid uniqueidentifier", "text_data nvarchar(max) null", "create_date datetime"]),
        ("sysmail_send_retries", [
            "conversation_handle uniqueidentifier", "mailitem_id int", "send_attempts int", "last_send_attempt_date datetime",
        ]),
        ("sysmail_server", [
            "account_id int", "servertype sysname", "servername sysname", "port int", "username nvarchar(128) null", "credential_id int null",
            "use_default_credentials bit", "enable_ssl bit", "flags int", "timeout int null", "last_mod_datetime datetime", "last_mod_user sysname",
        ]),
        ("sysmail_servertype", ["servertype sysname", "is_incoming bit", "is_outgoing bit", "last_mod_datetime datetime", "last_mod_user sysname"]),
    ];

    /// <summary>The settings <c>sysmail_configuration</c> ships, as <c>(paramname, paramvalue, description)</c>.</summary>
    private static readonly (string Name, string Value, string Description)[] MailConfigurationSeeds =
    [
        ("AccountRetryAttempts", "1", "Number of retry attempts for a mail server"),
        ("AccountRetryDelay", "60", "Delay between each retry attempt to mail server"),
        ("DatabaseMailExeMinimumLifeTime", "600", "Minimum process lifetime in seconds"),
        ("DefaultAttachmentEncoding", "MIME", "Default attachment encoding"),
        ("LoggingLevel", "2", "Database Mail logging level: normal - 1, extended - 2 (default), verbose - 3"),
        ("MaxFileSize", "1000000", "Default maximum file size"),
        ("ProhibitedExtensions", "exe,dll,vbs,js", "Extensions not allowed in outgoing mails"),
    ];

    /// <summary>msdb's <c>DatabaseMailUserRole</c>, whose members send mail and read their own items.</summary>
    internal const int DatabaseMailUserRoleId = 9;

    /// <summary>
    /// Seeds the tables of <see cref="MsdbMailTables"/>, the <c>SMTP</c> server
    /// type and the seven settings, the views <c>sysmail_allitems</c>,
    /// <c>sysmail_sentitems</c>, <c>sysmail_unsentitems</c>,
    /// <c>sysmail_faileditems</c>, <c>sysmail_event_log</c> and
    /// <c>sysmail_mailattachments</c>, and <c>DatabaseMailUserRole</c>'s
    /// <c>SELECT</c> on each view. A view lists to a login outside
    /// <c>sysadmin</c> only the items it sent, their attachments and the log
    /// rows naming them.
    /// </summary>
    private void SeedMsdbMailObjects(Database msdb)
    {
        var schema = msdb.Schemas[Database.DefaultSchemaName];
        foreach (var (name, specs) in MsdbMailTables)
        {
            var columns = new HeapColumn[specs.Length];
            for (var i = 0; i < specs.Length; i++)
                columns[i] = MsdbColumn(msdb, specs[i]);
            schema.HeapTables[name] = new HeapTable(name, columns, msdb.AllocateObjectId()) { OwningDatabase = msdb, IsMsShipped = true };
        }

        var installed = SqlValue.FromDateTime(WholeMilliseconds(this.SeedDate));
        var sa = SqlValue.FromNVarchar("sa");
        SeedMsdbRow(schema.HeapTables["sysmail_servertype"], [SqlValue.FromNVarchar("SMTP"), SqlValue.FromBoolean(false), SqlValue.FromBoolean(true), installed, sa]);
        var configuration = schema.HeapTables["sysmail_configuration"];
        foreach (var (name, value, description) in MailConfigurationSeeds)
            SeedMsdbRow(configuration, [SqlValue.FromNVarchar(name), SqlValue.FromNVarchar(value), SqlValue.FromNVarchar(description), installed, sa]);

        const string sysadmin = "isnull(is_srvrolemember(N'sysadmin'), 0) = 1";
        const string ownItem = "(" + sysadmin + " or send_request_user = suser_sname())";
        var items = schema.HeapTables["sysmail_mailitems"];
        string[] itemColumns =
        [
            "mailitem_id", "profile_id", "recipients", "copy_recipients", "blind_copy_recipients", "subject", "body", "body_format",
            "importance", "sensitivity", "file_attachments", "attachment_encoding", "query", "execute_query_database",
            "attach_query_result_as_file", "query_result_header", "query_result_width", "query_result_separator", "exclude_query_output",
            "append_query_error", "send_request_date", "send_request_user", "sent_account_id",
        ];
        HeapColumn[] itemViewColumns =
        [
            .. ViewColumns(items, itemColumns),
            new("sent_status", VarcharSqlType.Get(8, msdb.Collation, Coercibility.Implicit), maxLength: 8, nullable: false),
            .. ViewColumns(items, ["sent_date", "last_mod_date", "last_mod_user"]),
        ];
        var itemSelect = "select " + string.Join(", ", itemColumns)
            + ", convert(varchar(8), case sent_status when 0 then 'unsent' when 1 then 'sent' when 3 then 'retrying' else 'failed' end) as sent_status, "
            + "sent_date, last_mod_date, last_mod_user from dbo.sysmail_mailitems where " + ownItem;
        var views = new List<int>
        {
            SeedMsdbView(schema, "sysmail_allitems", itemViewColumns, itemSelect).ObjectId,
            SeedMsdbView(schema, "sysmail_sentitems", itemViewColumns, itemSelect + " and sent_status = 1").ObjectId,
            SeedMsdbView(schema, "sysmail_unsentitems", itemViewColumns, itemSelect + " and (sent_status = 0 or sent_status = 3)").ObjectId,
            SeedMsdbView(schema, "sysmail_faileditems", itemViewColumns, itemSelect + " and sent_status = 2").ObjectId,
        };

        const string ownLogRow = "(" + sysadmin + " or exists (select 1 from dbo.sysmail_mailitems as items "
            + "where items.mailitem_id = mail.mailitem_id and items.send_request_user = suser_sname()))";
        var log = schema.HeapTables["sysmail_log"];
        views.Add(SeedMsdbView(schema, "sysmail_event_log",
            [
                .. ViewColumns(log, ["log_id"]),
                new("event_type", VarcharSqlType.Get(11, msdb.Collation, Coercibility.Implicit), maxLength: 11, nullable: false),
                .. ViewColumns(log, ["log_date", "description", "process_id", "mailitem_id", "account_id", "last_mod_date", "last_mod_user"]),
            ],
            "select log_id, convert(varchar(11), case event_type when 0 then 'success' when 1 then 'information' when 2 then 'warning' else 'error' end) as event_type, "
            + "log_date, description, process_id, mailitem_id, account_id, last_mod_date, last_mod_user from dbo.sysmail_log as mail where " + ownLogRow).ObjectId);
        var attachments = schema.HeapTables["sysmail_attachments"];
        views.Add(SeedMsdbView(schema, "sysmail_mailattachments",
            ViewColumns(attachments, ["attachment_id", "mailitem_id", "filename", "filesize", "attachment", "last_mod_date", "last_mod_user"]),
            "select attachment_id, mailitem_id, filename, filesize, attachment, last_mod_date, last_mod_user from dbo.sysmail_attachments as mail where "
            + ownLogRow).ObjectId);

        foreach (var objectId in views)
            msdb.Permissions.Add(new(PermissionChecker.ClassObject, objectId, 0, DatabaseMailUserRoleId, Database.DboPrincipalId, Permission.Select, PermissionState.Grant));
    }
}
