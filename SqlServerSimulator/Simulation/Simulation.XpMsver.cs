using System.Globalization;
using System.Runtime.InteropServices;
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
    /// The <c>xp_msver</c> rows, materialized once. Host-dependent cells
    /// (platform, OS version, processor mask/count, physical memory) are
    /// computed exception-safe at type-init; version-identity cells use the
    /// simulator's claimed 17.0.0.0 identity rather than a live server build.
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

    private static SqlValue[][] BuildXpMsverRows()
    {
        var processorCount = Environment.ProcessorCount;
        var (osValue, osText) = ComputeOsVersion();
        var (memoryMb, memoryBytes) = ComputePhysicalMemory();
        return
        [
            // ProductVersion Internal_Value packs the major version as
            // (major << 16) — 17 << 16 = 1114112 — matching real xp_msver.
            XpMsverRow(1, "ProductName", null, "Microsoft SQL Server"),
            XpMsverRow(2, "ProductVersion", 17 << 16, "17.0.0.0"),
            XpMsverRow(3, "Language", 1033, "English (United States)"),
            XpMsverRow(4, "Platform", null, ComputePlatform()),
            XpMsverRow(5, "Comments", null, "SQL"),
            XpMsverRow(6, "CompanyName", null, "Microsoft Corporation"),
            XpMsverRow(7, "FileDescription", null, OperatingSystem.IsWindows()
                ? "SQL Server Windows NT - 64 Bit"
                : "SQL Server Linux - 64 Bit"),
            XpMsverRow(8, "FileVersion", null, "2025.0170.0000.00"),
            XpMsverRow(9, "InternalName", null, "SQLSERVR"),
            XpMsverRow(10, "LegalCopyright", null, "Microsoft. All rights reserved."),
            XpMsverRow(11, "LegalTrademarks", null, "Microsoft SQL Server is a registered trademark of Microsoft Corporation."),
            XpMsverRow(12, "OriginalFilename", null, "SQLSERVR.EXE"),
            XpMsverRow(13, "PrivateBuild", null, null),
            XpMsverRow(14, "SpecialBuild", 0, null),
            XpMsverRow(15, "WindowsVersion", osValue, osText),
            XpMsverRow(16, "ProcessorCount", processorCount, processorCount.ToString(CultureInfo.InvariantCulture)),
            XpMsverRow(17, "ProcessorActiveMask", null, ComputeProcessorActiveMask()),
            XpMsverRow(18, "ProcessorType", 8664, null),
            XpMsverRow(19, "PhysicalMemory", memoryMb, $"{memoryMb} ({memoryBytes})"),
            XpMsverRow(20, "Product ID", null, null),
        ];
    }

    private static string ComputePlatform()
    {
        // Lowercase architecture token to mirror real xp_msver's 'NT x64';
        // the common arches are mapped explicitly (CA1308 forbids
        // ToLowerInvariant, and the switch reads honestly per meaning).
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToUpperInvariant(),
        };
        return OperatingSystem.IsWindows() ? $"NT {architecture}" : $"Linux {architecture}";
    }

    private static (int Value, string Text) ComputeOsVersion()
    {
        var version = Environment.OSVersion.Version;
        var packed = (version.Major << 16) | (version.Build & 0xFFFF);
        return (packed, $"{version.Major}.{version.Minor} ({version.Build})");
    }

    private static string ComputeProcessorActiveMask()
    {
        var count = Environment.ProcessorCount;
        var mask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
        return mask.ToString("x", CultureInfo.InvariantCulture);
    }

    private static (int Megabytes, long Bytes) ComputePhysicalMemory()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return ((int)(bytes / (1024 * 1024)), bytes);
    }
}
