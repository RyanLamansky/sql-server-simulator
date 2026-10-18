using System.Globalization;
using System.Text.RegularExpressions;
using SqlServerSimulator.Audit;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// sys.fn_get_audit_file and sys.fn_get_audit_file_v2: the records the
// simulation's audits wrote, read back from the files its audit state keeps
// (probed 2026-10-17 against SQL Server 2025); see docs/claude/audit.md.
partial class Selection
{
    private static readonly Collation AuditFileCollation = Collation.Get("SQL_Latin1_General_CP1_CI_AS");

    private static NVarcharSqlType AuditText(int length) => NVarcharSqlType.Get(length, AuditFileCollation, Coercibility.Implicit);

    /// <summary>The functions' columns: each one's name, type and, for one an <c>audit_event</c> field carries, that field's name.</summary>
    private static readonly (string Name, SqlType Type)[] AuditFileColumns =
    [
        ("event_time", SqlType.GetDateTime2(7)),
        ("sequence_number", SqlType.Int32),
        ("action_id", VarcharSqlType.Get(4, AuditFileCollation, Coercibility.Implicit)),
        ("succeeded", SqlType.Bit),
        ("permission_bitmask", VarbinarySqlType.Get(16)),
        ("is_column_permission", SqlType.Bit),
        ("session_id", SqlType.SmallInt),
        ("server_principal_id", SqlType.Int32),
        ("database_principal_id", SqlType.Int32),
        ("target_server_principal_id", SqlType.Int32),
        ("target_database_principal_id", SqlType.Int32),
        ("object_id", SqlType.Int32),
        ("class_type", VarcharSqlType.Get(2, AuditFileCollation, Coercibility.Implicit)),
        ("session_server_principal_name", AuditText(4000)),
        ("server_principal_name", AuditText(4000)),
        ("server_principal_sid", VarbinarySqlType.Get(85)),
        ("database_principal_name", AuditText(128)),
        ("target_server_principal_name", AuditText(128)),
        ("target_server_principal_sid", VarbinarySqlType.Get(85)),
        ("target_database_principal_name", AuditText(128)),
        ("server_instance_name", AuditText(128)),
        ("database_name", AuditText(128)),
        ("schema_name", AuditText(128)),
        ("object_name", AuditText(128)),
        ("statement", AuditText(4000)),
        ("additional_information", AuditText(4000)),
        ("file_name", AuditText(260)),
        ("audit_file_offset", SqlType.BigInt),
        ("user_defined_event_id", SqlType.SmallInt),
        ("user_defined_information", AuditText(4000)),
        ("audit_schema_version", SqlType.Int32),
        ("sequence_group_id", VarbinarySqlType.Get(85)),
        ("transaction_id", SqlType.BigInt),
        ("client_ip", AuditText(128)),
        ("application_name", AuditText(128)),
        ("duration_milliseconds", SqlType.BigInt),
        ("response_rows", SqlType.BigInt),
        ("affected_rows", SqlType.BigInt),
        ("connection_id", SqlType.UniqueIdentifier),
        ("data_sensitivity_information", AuditText(4000)),
        ("host_name", AuditText(128)),
        ("session_context", AuditText(4000)),
        ("client_tls_version", SqlType.BigInt),
        ("client_tls_version_name", AuditText(128)),
        ("database_transaction_id", SqlType.BigInt),
        ("ledger_start_sequence_number", SqlType.BigInt),
        ("external_policy_permissions_checked", AuditText(4000)),
        ("obo_middle_tier_app_id", AuditText(128)),
        ("is_local_secondary_replica", SqlType.Bit),
    ];

    private static readonly SqlType[] AuditFileSchema = [.. AuditFileColumns.Select(static column => column.Type)];

    private static readonly string[] AuditFileColumnNames = [.. AuditFileColumns.Select(static column => column.Name)];

    /// <summary>
    /// <c>sys.fn_get_audit_file(file_pattern, initial_file_name,
    /// audit_record_offset)</c> and its <c>_v2</c>, which adds a start and an
    /// end time to filter by.
    /// </summary>
    public static Selection ParseAuditFile(ParserContext context, string functionName, bool v2)
    {
        var arguments = ParseIntegerTvfArguments(context, functionName, v2 ? 5 : 3, insufficientState: 3);
        return new Selection(AuditFileSchema, AuditFileColumnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateAuditFile(arguments, batch, outerResolver));
    }

    /// <summary>
    /// Every record in the files the pattern matches, file by file in name
    /// order: a relative pattern reads under the log directory, <c>*</c>
    /// matches any run of characters and <c>?</c> one, and a pattern naming
    /// a directory alone matches nothing. A NULL pattern, or one under a
    /// directory the server doesn't hold, is Msg 33224; an initial file and
    /// offset must name one of the matched files' records, or Msg 33225, and
    /// read on from past that record (probed 2026-10-17 against SQL Server
    /// 2025). <c>_v2</c>'s filters take <c>YYYY-MM-DDThh:mm:ssZ</c> alone:
    /// another time is Msg 37620, anything else Msg 241.
    /// </summary>
    private static List<byte[]> EnumerateAuditFile(Expression?[] arguments, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerSecurityAudit(batch);
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        string? Text(Expression? expression) => expression?.Run(runtime) is { IsNull: false } value ? value.AsString : null;
        var simulation = batch.Connection.Simulation;
        var path = Text(arguments[0]) ?? throw SimulatedSqlException.AuditFilePatternInvalid();
        if (!path.StartsWith('/'))
            path = EventFileTarget.LogDirectory + path;
        var slash = path.LastIndexOf('/');
        if (Simulation.CanonicalServerPath(path[..(slash + 1)]) is not { } directory || !simulation.IsServerDirectory(directory))
            throw SimulatedSqlException.AuditFilePatternInvalid();
        var initialFile = Text(arguments[1]);
        long? initialOffset = arguments[2]?.Run(runtime) is { IsNull: false } offset ? offset.CoerceTo(SqlType.BigInt).AsInt64 : null;
        DateTime? start = null, end = null;
        if (arguments.Length == 5)
        {
            start = TimeFilter(Text(arguments[3]), "StartTimeFilter");
            end = TimeFilter(Text(arguments[4]), "EndTimeFilter");
        }

        var rows = new List<byte[]>();
        var files = new List<AuditFile>();
        if (simulation.AuditsIfMade is { } server)
        {
            var pattern = new Regex("^" + Regex.Escape(path).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            files.AddRange(server.Files.ToArray().Select(static pair => pair.Value).Where(file => pattern.IsMatch(file.Path)).OrderBy(static file => file.Path, StringComparer.Ordinal));
        }
        var reached = initialFile is null && initialOffset is null;
        if (!reached && (initialFile is null || initialOffset is not AuditOffset
            || !files.Exists(file => string.Equals(file.Path, initialFile, StringComparison.OrdinalIgnoreCase))))
        {
            throw SimulatedSqlException.AuditFileLocationInvalid();
        }
        var columns = AuditEvents.Event.DataColumns;
        foreach (var file in files)
        {
            AuditRecord[] records;
            lock (file.Gate)
                records = [.. file.Records];
            if (!reached)
            {
                // Reading from a record reads on past it: every record of a
                // file sits in its first buffer here, so the rest of the
                // initial file is past the offset.
                reached = string.Equals(file.Path, initialFile, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            foreach (var record in records)
            {
                if ((start is { } from && record.EventTime < from) || (end is { } to && record.EventTime > to))
                    continue;
                rows.Add(RowEncoder.EncodeRow(AuditFileSchema, AuditFileRow(record, columns, file.Path)));
            }
        }
        return rows;

        static DateTime? TimeFilter(string? text, string filter)
        {
            if (text is null)
                return null;
            if (DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
            throw DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? SimulatedSqlException.AuditTimeFilterFormat(filter)
                : SimulatedSqlException.ConversionFailedDateTimeFromString();
        }
    }

    /// <summary>The offset every record reports: a file's first record buffer, past its 16 KB header.</summary>
    private const long AuditOffset = 16384;

    private static SqlValue[] AuditFileRow(AuditRecord record, XeObjectColumn[] columns, string path)
    {
        var values = new SqlValue[AuditFileColumns.Length];
        for (var i = 0; i < AuditFileColumns.Length; i++)
        {
            var (name, type) = AuditFileColumns[i];
            values[i] = name switch
            {
                "audit_file_offset" => SqlValue.FromInt64(AuditOffset),
                "event_time" => SqlValue.FromDateTime2(type, record.EventTime),
                "file_name" => SqlValue.FromNVarchar((NVarcharSqlType)type, path),
                _ => FieldValue(name, type, record, columns),
            };
        }
        return values;
    }

    /// <summary>
    /// One column off the record's <c>audit_event</c> field of its name: the
    /// action and class as their characters, an empty principal sid as NULL,
    /// a field the simulator keeps nothing for as its type's zero.
    /// </summary>
    private static SqlValue FieldValue(string name, SqlType type, AuditRecord record, XeObjectColumn[] columns)
    {
        XeValue? field = null;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].Name == name)
            {
                field = record.Data[i];
                break;
            }
        }
        switch (name)
        {
            case "action_id":
                return SqlValue.FromVarchar((VarcharSqlType)type, AuditEvents.UnpackActionId(field?.Signed ?? 0));
            case "class_type":
                return AuditEvents.UnpackClassType(field?.Signed ?? 0) is { } classType ? SqlValue.FromVarchar((VarcharSqlType)type, classType) : SqlValue.Null(type);
            case "server_principal_sid" or "target_server_principal_sid" or "sequence_group_id":
                return field?.Bytes is { Length: > 0 } bytes ? SqlValue.FromVarbinary(bytes) : SqlValue.Null(type);
        }
        if (field is not { } value)
        {
            return type switch
            {
                NVarcharSqlType text => SqlValue.FromNVarchar(text, ""),
                _ when type == SqlType.UniqueIdentifier => SqlValue.FromGuid(Guid.Empty),
                _ => SqlValue.FromInt32(0).CoerceTo(type),
            };
        }
        return value.Family switch
        {
            XeTypeFamily.UnicodeString => SqlValue.FromNVarchar((NVarcharSqlType)type, value.Text ?? ""),
            XeTypeFamily.Boolean => SqlValue.FromBoolean(value.Unsigned != 0),
            XeTypeFamily.BinaryData => SqlValue.FromVarbinary(value.Bytes ?? []),
            XeTypeFamily.Guid => SqlValue.FromGuid(value.Guid),
            XeTypeFamily.Unsigned => SqlValue.FromInt64((long)value.Unsigned).CoerceTo(type),
            _ => SqlValue.FromInt64(value.Signed).CoerceTo(type),
        };
    }
}
