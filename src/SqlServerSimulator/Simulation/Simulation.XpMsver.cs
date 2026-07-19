using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Column schema for the <c>xp_msver</c> result set:
    /// <c>Index smallint</c>, <c>Name nvarchar</c>, <c>Internal_Value int</c>
    /// (nullable), <c>Character_Value nvarchar</c> (nullable).
    /// </summary>
    private static readonly SqlType[] XpMsverSchema =
        [SqlType.SmallInt, SqlType.NVarchar, SqlType.Int32, SqlType.NVarchar];

    private static readonly string[] XpMsverColumnNames =
        ["Index", "Name", "Internal_Value", "Character_Value"];

    /// <summary>
    /// The <c>xp_msver</c> rows, materialized once. Every cell is a fixed value
    /// mirroring the SQL Server 2025 reference instance (17.0.4065.4, RTM-CU7):
    /// version-identity cells carry the real build, and the host-shaped cells
    /// (platform, OS version, processor count/type/mask, physical memory) report
    /// the reference's fixed values rather than the simulator's live host —
    /// matching real, whose xp_msver reports Windows-style host strings even on
    /// Linux. The processor/memory figures are documented as fixed placeholders.
    /// </summary>
    private static readonly SqlValue[][] XpMsverRows = BuildXpMsverRows();

    /// <summary>
    /// Handles <c>EXEC xp_msver</c> (also <c>dbo.xp_msver</c> /
    /// <c>master.dbo.xp_msver</c> from any current database, and the
    /// name-form RPC path via a synthesized EXEC). Each argument is an
    /// <c>@optname</c> value naming a single row to return; the result carries
    /// only the requested rows, always in <c>Index</c> order regardless of
    /// argument order (probe-confirmed against SQL Server 2025). With no
    /// arguments every row is returned. An argument naming no row (an unknown
    /// optname) is silently skipped — real SQL Server returns an empty set for
    /// <c>EXEC xp_msver 'bogus'</c> rather than raising — and a duplicated
    /// optname yields its row once. Name matching is case-insensitive. Yields
    /// through the standard outcome path so an <c>INSERT … EXEC</c> consumer
    /// sees a normal result-set enumerator. SSMS calls this on connect; DacFx's
    /// bacpac export calls it by RPC with five repeated <c>@optname</c> params.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpMsver(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        yield return new SimulatedSqlResultSet(XpMsverSchema, XpMsverColumnNames, FilterXpMsverRows(arguments));
    }

    /// <summary>
    /// Selects the <see cref="XpMsverRows"/> named by the <c>@optname</c>
    /// arguments (case-insensitive by the row's <c>Name</c> cell), preserving
    /// the source <c>Index</c> order. An empty / all-NULL argument set returns
    /// every row.
    /// </summary>
    private static IReadOnlyList<SqlValue[]> FilterXpMsverRows(List<ProcArgument> arguments)
    {
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (argument.Value.IsNull)
                continue;
            _ = requested.Add(argument.Value.CoerceTo(SqlType.NVarchar).AsString);
        }

        if (requested.Count == 0)
            return XpMsverRows;

        var filtered = new List<SqlValue[]>();
        foreach (var row in XpMsverRows)
        {
            if (requested.Contains(row[1].AsString))
                filtered.Add(row);
        }

        return filtered;
    }

    private static SqlValue[] XpMsverRow(short index, string name, int? internalValue, string? characterValue) =>
    [
        SqlValue.FromInt16(index),
        SqlValue.FromNVarchar(name),
        internalValue is { } iv ? SqlValue.FromInt32(iv) : SqlValue.Null(SqlType.Int32),
        characterValue is { } cv ? SqlValue.FromNVarchar(cv) : SqlValue.Null(SqlType.NVarchar),
    ];

    private static SqlValue[][] BuildXpMsverRows() =>
        [
            // ProductVersion Internal_Value packs the major version as
            // (major << 16) — 17 << 16 = 1114112 — matching real xp_msver.
            XpMsverRow(1, "ProductName", null, "Microsoft SQL Server"),
            XpMsverRow(2, "ProductVersion", ReferenceBuild.Version.Major << 16, ReferenceBuild.ProductVersion),
            XpMsverRow(3, "Language", null, "English"),
            XpMsverRow(4, "Platform", null, "NT x64"),
            XpMsverRow(5, "Comments", null, "SQL"),
            XpMsverRow(6, "CompanyName", null, "Microsoft Corporation"),
            XpMsverRow(7, "FileDescription", null, "SQL Server Windows NT - 64 Bit"),
            XpMsverRow(8, "FileVersion", null, ReferenceBuild.FileVersion),
            XpMsverRow(9, "InternalName", null, "SQLSERVR"),
            XpMsverRow(10, "LegalCopyright", null, "Microsoft. All rights reserved."),
            XpMsverRow(11, "LegalTrademarks", null, "Microsoft SQL Server is a registered trademark of Microsoft Corporation."),
            XpMsverRow(12, "OriginalFilename", null, "SQLSERVR.EXE"),
            XpMsverRow(13, "PrivateBuild", null, null),
            XpMsverRow(14, "SpecialBuild", 266403844, null),
            // Fixed host-shaped placeholders (see XpMsverRows summary): the
            // reference reports a Windows OS version, 16 processors, and 3 GB
            // even on Linux.
            XpMsverRow(15, "WindowsVersion", 266403844, "6.3 (20348)"),
            XpMsverRow(16, "ProcessorCount", 16, "16"),
            XpMsverRow(17, "ProcessorActiveMask", null, "ffff"),
            XpMsverRow(18, "ProcessorType", 8664, null),
            XpMsverRow(19, "PhysicalMemory", 3072, "3072 (3221225472)"),
            XpMsverRow(20, "Product ID", null, null),
        ];
}
