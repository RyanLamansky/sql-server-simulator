using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Concurrent;
using System.Data.Common;

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
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    /// <summary>User tables, keyed by name.</summary>
    internal readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);

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
