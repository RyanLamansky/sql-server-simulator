using System.Text.RegularExpressions;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// sys.fn_xe_file_target_read_file: the events the event_file targets wrote,
// read back from the files the simulation's Extended Events state keeps
// (probed 2026-10-16 against SQL Server 2025); see
// docs/claude/extended-events.md#targets.
partial class Selection
{
    private static readonly NVarcharSqlType XeFileObjectNameType = NVarcharSqlType.Get(60, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly NVarcharSqlType XeFileNameType = NVarcharSqlType.Get(260, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly NVarcharSqlType XeFileDataType = NVarcharSqlType.Get(SqlType.MaxLengthSentinel, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly SqlType[] XeFileSchema =
    [
        SqlType.UniqueIdentifier, SqlType.UniqueIdentifier, XeFileObjectNameType, XeFileDataType, XeFileNameType, SqlType.BigInt, SqlType.GetDateTime2(7),
    ];

    private static readonly string[] XeFileColumnNames =
    [
        "module_guid", "package_guid", "object_name", "event_data", "file_name", "file_offset", "timestamp_utc",
    ];

    /// <summary>
    /// <c>sys.fn_xe_file_target_read_file(path, mdpath, initial_file_name,
    /// initial_offset)</c>: every event in the files <c>path</c> matches — a
    /// relative pattern under real's log directory, <c>*</c> matching any run
    /// of characters — in the order written, or only those past
    /// <c>initial_offset</c> in <c>initial_file_name</c> and the files after
    /// it when both are given.
    /// </summary>
    public static Selection ParseXeFileTargetReadFile(ParserContext context, string functionName)
    {
        var arguments = ParseIntegerTvfArguments(context, functionName, 4, insufficientState: 3);
        return new Selection(XeFileSchema, XeFileColumnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateXeFile(arguments, batch, outerResolver));
    }

    /// <summary>
    /// A NULL path is Msg 25718 naming <c>(null)</c>, and an initial file
    /// name without an offset, or the reverse, Msg 25719; a pattern matching
    /// no file reads nothing (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static List<byte[]> EnumerateXeFile(Expression?[] arguments, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        string? Text(Expression? expression) => expression?.Run(runtime) is { IsNull: false } value ? value.AsString : null;
        var path = Text(arguments[0]) ?? throw SimulatedSqlException.XeLogFileNameInvalid("(null)");
        var initialFile = Text(arguments[2]);
        long? initialOffset = arguments[3]?.Run(runtime) is { IsNull: false } offset ? offset.CoerceTo(SqlType.BigInt).AsInt64 : null;
        if ((initialFile is null) != (initialOffset is null))
            throw SimulatedSqlException.XeInitialFileAndOffsetUnpaired();

        var rows = new List<byte[]>();
        if (batch.Connection.Simulation.ExtendedEventsIfMade is not { } server)
            return rows;
        if (!path.StartsWith('/'))
            path = EventFileTarget.LogDirectory + path;
        var pattern = new Regex("^" + Regex.Escape(path).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
        var files = server.Files.ToArray().Select(pair => pair.Value).Where(file => pattern.IsMatch(file.Path)).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        var reached = initialFile is null;
        foreach (var file in files)
        {
            (XeObject Event, DateTime Timestamp, string Xml, long Offset)[] events;
            lock (file.Gate)
                events = [.. file.Events];
            var isInitial = initialFile is not null && file.Path.EndsWith(initialFile, StringComparison.Ordinal);
            if (!reached && !isInitial)
                continue;
            reached = true;
            foreach (var (@event, timestamp, xml, eventOffset) in events)
            {
                if (isInitial && eventOffset <= initialOffset)
                    continue;
                rows.Add(RowEncoder.EncodeRow(XeFileSchema,
                [
                    SqlValue.FromGuid(Guid.Parse(@event.Package.ModuleGuid)),
                    SqlValue.FromGuid(@event.Package.Guid),
                    SqlValue.FromNVarchar(XeFileObjectNameType, @event.Name),
                    SqlValue.FromNVarchar(XeFileDataType, xml),
                    SqlValue.FromNVarchar(XeFileNameType, file.Path),
                    SqlValue.FromInt64(eventOffset),
                    SqlValue.FromDateTime2(XeFileSchema[6], new DateTime(timestamp.Ticks - (timestamp.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc)),
                ]));
            }
        }
        return rows;
    }
}
