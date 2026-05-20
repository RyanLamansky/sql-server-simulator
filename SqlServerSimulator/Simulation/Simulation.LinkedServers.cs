using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Body for <c>sp_addlinkedserver</c>. Activates a linked server on the
    /// caller's <see cref="Simulation"/> by name — the target
    /// <see cref="Simulation"/> must already be bound via
    /// <see cref="AddRemoteSimulation(string, Simulation)"/>. Real SQL
    /// Server's positional signature is
    /// <c>(@server, @srvproduct, @provider, @datasrc, @location,
    /// @provider_string, @catalog)</c>; only <c>@server</c> is meaningful
    /// here, while <c>@srvproduct</c> / <c>@provider</c> / <c>@datasrc</c>
    /// surface unchanged in <c>sys.servers</c> projections and the rest are
    /// accepted-but-discarded. Re-activating an existing linked-server name
    /// silently replaces the prior entry.
    /// </summary>
    /// <remarks>
    /// Cursor on entry: first token after the procedure name (or the trailing
    /// statement boundary when there are no args). Cursor on exit: the
    /// trailing statement boundary.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAddLinkedServer(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string? server = null;
        var srvProduct = string.Empty;
        var provider = "SQLNCLI";
        string? dataSource = null;
        string[] positional = ["server", "srvproduct", "provider", "datasrc", "location", "provider_string", "catalog"];
        var positionalIndex = 0;
        foreach (var arg in arguments)
        {
            var name = arg.Name;
            if (name is null)
            {
                if (positionalIndex >= positional.Length)
                    throw SimulatedSqlException.InvalidLinkedServerParameter("sp_addlinkedserver");
                name = positional[positionalIndex];
            }
            positionalIndex++;
            switch (name)
            {
                case var n when Collation.Default.Equals(n, "server"):
                    server = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when Collation.Default.Equals(n, "srvproduct"):
                    srvProduct = arg.Value.IsNull ? string.Empty : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when Collation.Default.Equals(n, "provider"):
                    provider = arg.Value.IsNull ? "SQLNCLI" : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when Collation.Default.Equals(n, "datasrc"):
                    dataSource = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when Collation.Default.Equals(n, "location"):
                case var n2 when Collation.Default.Equals(n2, "provider_string"):
                case var n3 when Collation.Default.Equals(n3, "catalog"):
                    break;
                default:
                    throw SimulatedSqlException.InvalidLinkedServerParameter("sp_addlinkedserver");
            }
        }

        if (string.IsNullOrEmpty(server))
            throw SimulatedSqlException.InvalidLinkedServerParameter("sp_addlinkedserver");

        var simulation = batch.Connection.Simulation;
        if (!simulation.AvailableRemotes.TryGetValue(server, out var target))
            throw new NotSupportedException($"sp_addlinkedserver '{server}' has no corresponding registered target Simulation; call Simulation.AddRemoteSimulation(\"{server}\", target) from the host code before activating the linked server.");

        simulation.ActiveLinkedServers[server] = new LinkedServer(server, target, srvProduct, provider, dataSource);
    }

    /// <summary>
    /// Body for <c>sp_dropserver</c>. Deactivates a linked server by name.
    /// Real SQL Server's signature is <c>(@server, @droplogins)</c>; the
    /// second arg is accepted and discarded (the simulator doesn't model
    /// linked-server login mappings). Raises Msg 15015 when the server
    /// isn't currently active — mirrors real SQL Server's verbatim wording.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDropServer(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string? server = null;
        string[] positional = ["server", "droplogins"];
        var positionalIndex = 0;
        foreach (var arg in arguments)
        {
            var name = arg.Name;
            if (name is null)
            {
                if (positionalIndex >= positional.Length)
                    throw SimulatedSqlException.InvalidLinkedServerParameter("sp_dropserver");
                name = positional[positionalIndex];
            }
            positionalIndex++;
            switch (name)
            {
                case var n when Collation.Default.Equals(n, "server"):
                    server = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when Collation.Default.Equals(n, "droplogins"):
                    break;
                default:
                    throw SimulatedSqlException.InvalidLinkedServerParameter("sp_dropserver");
            }
        }

        if (string.IsNullOrEmpty(server))
            throw SimulatedSqlException.InvalidLinkedServerParameter("sp_dropserver");

        if (!batch.Connection.Simulation.ActiveLinkedServers.TryRemove(server, out _))
            throw SimulatedSqlException.LinkedServerDoesNotExist(server);
    }

    /// <summary>
    /// Parse-and-discard body for <c>sp_addlinkedsrvlogin</c> /
    /// <c>sp_droplinkedsrvlogin</c> / <c>sp_serveroption</c>. The simulator
    /// has no principal-mapping model and no per-server option semantics,
    /// but real BACPACs and migration scripts often emit these alongside
    /// <c>sp_addlinkedserver</c>; silently accepting them keeps those scripts
    /// running. Argument grammar is consumed (so a malformed call still
    /// raises through the standard EXEC arg parser) but the values are
    /// dropped.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpLinkedServerNoOp(BatchContext batch)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        yield break;
    }
}
