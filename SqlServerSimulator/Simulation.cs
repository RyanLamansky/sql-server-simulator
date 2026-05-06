using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;

namespace SqlServerSimulator;

/// <summary>
/// Simulates a SQL Server instance.
/// </summary>
/// <remarks>
/// Implementation is split across <c>Simulation.*.cs</c> partial-class files
/// by statement family (<c>Create</c>, <c>Insert</c>, <c>Output</c>,
/// <c>Merge</c>, <c>Set</c>, <c>Alter</c>, <c>Dbcc</c>, plus <c>Coerce</c>
/// for the value-coercion helpers shared between INSERT and MERGE). This file
/// holds the public surface (<see cref="CreateDbConnection"/>), the
/// simulation-wide state, and the top-level statement dispatcher.
/// </remarks>
public sealed partial class Simulation
{
    /// <summary>
    /// Creates a new simulated SQL Server instance with no tables or data.
    /// </summary>
    public Simulation()
    {
        RandomNumberGenerator.Fill(this.newSequentialIdAnchor);
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    /// <summary>User tables, keyed by name.</summary>
    internal readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);

    /// <summary>
    /// The database name woven into error messages that include a fully
    /// qualified table reference (e.g. Msg 515's <c>"&lt;db&gt;.dbo.&lt;t&gt;"</c>,
    /// Msg 547's <c>database "&lt;db&gt;"</c> wording). The simulator has no
    /// real per-database namespacing; this is a fixed placeholder so the
    /// emitted text stays well-formed and recognizable.
    /// </summary>
    internal const string DefaultDatabaseName = "simulated";

    /// <summary>
    /// Database compatibility level. New simulations default to the most recent
    /// supported level; user code switches via
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c>.
    /// </summary>
    internal CompatibilityLevel CompatibilityLevel = CompatibilityLevel.Sql170;

    /// <summary>
    /// Active session-scoped trace flags (the simulator doesn't model separate
    /// global vs session scope yet — flags set here apply simulation-wide).
    /// Toggled via <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>.
    /// </summary>
    internal readonly HashSet<int> TraceFlags = [];

    /// <summary>
    /// Last identity value produced by an INSERT in this simulation —
    /// the source for both <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c>.
    /// SQL Server scopes these per session/scope; the simulator collapses
    /// both to a single simulation-wide slot for the same reason
    /// <see cref="TraceFlags"/> does.
    /// </summary>
    /// <remarks>
    /// Cleared (set to <c>null</c>) by every INSERT that doesn't generate
    /// or accept an identity value — matching SQL Server's behavior of
    /// resetting <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c> when the
    /// most recent statement didn't touch an identity column.
    /// </remarks>
    internal decimal? LastIdentity;

    /// <summary>
    /// Name of the table currently under <c>SET IDENTITY_INSERT ... ON</c>,
    /// or <c>null</c> when no table is in that mode. SQL Server allows only
    /// one table at a time per session; the simulator enforces the same.
    /// </summary>
    internal string? IdentityInsertTable;

    /// <summary>
    /// Explicit override of the per-database <c>VERBOSE_TRUNCATION_WARNINGS</c>
    /// scoped configuration; <c>null</c> means follow the compatibility-level
    /// default. Set via
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// </summary>
    internal bool? VerboseTruncationWarnings;

    /// <summary>
    /// Decides whether string truncation should raise the verbose Msg 2628
    /// (with table, column, and truncated value) or the legacy Msg 8152
    /// (single line, no detail). Precedence: an explicit
    /// <see cref="VerboseTruncationWarnings"/> setting wins; otherwise trace
    /// flag 460 forces verbose; otherwise the compatibility level decides
    /// (verbose iff &gt;= <see cref="CompatibilityLevel.Sql160"/>, the level
    /// at which it became default in SQL Server 2022).
    /// </summary>
    internal bool IsVerboseTruncationActive() =>
        this.VerboseTruncationWarnings
        ?? (this.TraceFlags.Contains(460)
            || this.CompatibilityLevel >= CompatibilityLevel.Sql160);

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static Dictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    /// <summary>
    /// Random 12-byte tail (raw bytes [4..15] of the produced GUID) for
    /// <see cref="GenerateNewSequentialId"/>. Filled once at construction —
    /// stands in for SQL Server's "MAC address + boot timestamp" anchor that
    /// distinguishes one server's sequence from another's.
    /// </summary>
    private readonly byte[] newSequentialIdAnchor = new byte[12];

    /// <summary>
    /// Monotonic counter for <see cref="GenerateNewSequentialId"/>; each call
    /// reserves the next value via <see cref="Interlocked.Increment(ref long)"/>
    /// and packs it into raw bytes [0..3] of the produced GUID.
    /// </summary>
    private long newSequentialIdCounter;

    /// <summary>
    /// Produces the next <c>NEWSEQUENTIALID()</c> value: a
    /// <see cref="Guid"/> whose comparison under SQL Server's
    /// <c>uniqueidentifier</c> ordering rules is strictly greater than
    /// every value previously returned for this <see cref="Simulation"/>.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>uniqueidentifier</c> compares group-by-group from
    /// most significant to least: bytes <c>[10..15]</c>, then <c>[8..9]</c>,
    /// then <c>[6..7]</c>, then <c>[4..5]</c>, then <c>[0..3]</c>; within
    /// each group the lower-indexed byte is more significant. To get
    /// strict monotonicity the simulator fixes bytes <c>[4..15]</c> for the
    /// lifetime of the simulation and packs an incrementing 64-bit counter
    /// into bytes <c>[0..3]</c> big-endian (raw byte 0 = MSB, raw byte 3 =
    /// LSB). Each increment lands in the comparison-LSB position
    /// (raw byte 3) and carries propagate left toward higher comparison
    /// significance — matching real SQL Server's per-call delta.
    /// Monotonicity holds for the first 2^32 calls; beyond that the counter
    /// wraps and the cycle restarts. The GUID is constructed via
    /// <see cref="Guid(ReadOnlySpan{byte}, bool)"/> with <c>bigEndian</c>
    /// true, so its display order matches the raw byte order assembled here.
    /// </remarks>
    internal Guid GenerateNewSequentialId()
    {
        var counter = (uint)Interlocked.Increment(ref this.newSequentialIdCounter);
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)(counter >> 24);
        bytes[1] = (byte)(counter >> 16);
        bytes[2] = (byte)(counter >> 8);
        bytes[3] = (byte)counter;
        this.newSequentialIdAnchor.CopyTo(bytes[4..]);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Top-level statement dispatch. Iterates through the command's tokens,
    /// dispatching each statement to its dedicated parser by leading keyword.
    /// Yields outcomes for data-producing statements (SELECT, INSERT) and runs
    /// schema/control statements for side-effect only (CREATE, SET, ALTER,
    /// DBCC). The shape mirrors <c>Expression.ResolveBuiltIn</c>: a single
    /// switch with one case per keyword, each delegating to a focused method.
    /// </summary>
    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var context = new ParserContext(command);

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case Operator { Character: ';' }:
                    continue;

                case ReservedKeyword { Keyword: Keyword.Select }:
                    yield return Selection.Parse(context, 0).Results;
                    continue;

                case ReservedKeyword { Keyword: Keyword.Insert }:
                    yield return ParseInsert(context);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Merge }:
                    yield return ParseMerge(context);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Create } when TryParseCreate(context):
                case ReservedKeyword { Keyword: Keyword.Set } when TryParseSet(context):
                case ReservedKeyword { Keyword: Keyword.Alter } when TryParseAlter(context):
                case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseDbcc(context):
                    continue;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
