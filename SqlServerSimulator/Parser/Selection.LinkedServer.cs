using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Wraps a four-part-name reference (<c>server.db.schema.t</c>) as a
    /// <see cref="Selection"/> suitable for use as a
    /// <see cref="FromSource.LateralPlan"/>. Each <see cref="Execute"/> call
    /// opens a fresh <see cref="SimulatedDbConnection"/> on
    /// <paramref name="server"/>'s <see cref="LinkedServer.Target"/>,
    /// issues <c>SELECT * FROM [<paramref name="databaseName"/>].[<paramref
    /// name="schemaName"/>].[<paramref name="leafName"/>]</c> through the
    /// remote's full parser / planner / lock-manager pipeline, and streams
    /// the encoded row bytes back. Re-executes on every outer-row
    /// invocation (matching the catalog-view / correlated-derived-table
    /// pattern), so the remote sees a fresh snapshot per row when the
    /// query is correlated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SELECT-projection output bytes from the remote use the type-only
    /// <see cref="RowEncoder.EncodeRow(ReadOnlySpan{SqlType}, ReadOnlySpan{SqlValue})"/>
    /// overload (no LOB store), so the bytes are self-contained — no LOB
    /// pointers reference the remote heap. The local plan reads them via
    /// the same <see cref="RowDecoder"/> path it uses for any other
    /// <see cref="FromSource"/>.
    /// </para>
    /// <para>
    /// No predicate / projection push-down: the simulator always asks the
    /// remote for the full table and applies the local <c>WHERE</c> /
    /// projection / join on the returned rowset. Correct but slow for
    /// large remote tables; matches the agreed initial scope.
    /// </para>
    /// </remarks>
    internal static Selection ForLinkedServer(LinkedServer server, string databaseName, string schemaName, string leafName, HeapColumn[] columns)
    {
        var schemaArr = new SqlType[columns.Length];
        var columnNames = new string[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            schemaArr[i] = columns[i].Type;
            columnNames[i] = columns[i].Name;
        }
        return new Selection(
            schemaArr,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (_, _) => StreamRemoteRows(server, databaseName, schemaName, leafName));
    }

    /// <summary>
    /// Opens a fresh remote connection, issues
    /// <c>SELECT * FROM [db].[schema].[leaf]</c>, materializes the row
    /// bytes into a list (so the connection / command / reader chain
    /// disposes before iteration is consumed by the local plan), and
    /// returns the buffered rows. Disposing the connection drops any
    /// remote locks acquired by the query under the remote's
    /// session-isolation defaults — matching the "fresh remote session
    /// per remote query" semantic of real SQL Server's linked-server
    /// pipeline.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query string is built from identifiers that already passed parser validation as Name tokens (so they're well-formed SQL identifiers, not user-typed text); the leaf / schema / db segments are bracket-quoted with embedded ] escaping. The remote command runs against a sibling Simulation in the same process — there's no external SQL surface to inject against.")]
    private static List<byte[]> StreamRemoteRows(LinkedServer server, string databaseName, string schemaName, string leafName)
    {
        using var conn = server.Target.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT * FROM [{EscapeIdent(databaseName)}].[{EscapeIdent(schemaName)}].[{EscapeIdent(leafName)}]");
        var rows = new List<byte[]>();
        foreach (var outcome in server.Target.CreateResultSetsForCommand(cmd))
        {
            if (outcome is SimulatedSqlResultSet rs)
                rows.AddRange(rs.RowBytes);
        }
        return rows;
    }

    private static string EscapeIdent(string ident) => ident.Replace("]", "]]", StringComparison.Ordinal);
}
