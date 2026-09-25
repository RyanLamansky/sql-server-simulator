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

        // Real's own signature (probed 2026-09-25 against SQL Server 2025):
        // an unknown name is Msg 8145, a ninth argument Msg 8144, no @server
        // Msg 201 and a NULL one Msg 15004.
        string? server = null;
        var serverSupplied = false;
        var srvProduct = string.Empty;
        var provider = "SQLNCLI";
        string? dataSource = null;
        string[] positional = ["server", "srvproduct", "provider", "datasrc", "location", "provstr", "catalog", "linkedstyle"];
        var positionalIndex = 0;
        foreach (var arg in arguments)
        {
            var name = arg.Name;
            if (name is null)
            {
                if (positionalIndex >= positional.Length)
                    throw SimulatedSqlException.TooManyArgumentsToFunction("sp_addlinkedserver");
                name = positional[positionalIndex];
            }
            positionalIndex++;
            switch (name)
            {
                case var n when BuiltInToken.Equals(n, "server"):
                    serverSupplied = true;
                    server = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when BuiltInToken.Equals(n, "srvproduct"):
                    srvProduct = arg.Value.IsNull ? string.Empty : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when BuiltInToken.Equals(n, "provider"):
                    provider = arg.Value.IsNull ? "SQLNCLI" : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when BuiltInToken.Equals(n, "datasrc"):
                    dataSource = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                case var n when BuiltInToken.Equals(n, "linkedstyle"):
                    // A system procedure's parameter conversion reports state 1.
                    try
                    {
                        _ = arg.Value.CoerceTo(SqlType.Bit);
                    }
                    catch (SimulatedSqlException error) when (Parser.Expressions.Cast.IsConversionFailure(error.Number))
                    {
                        throw SimulatedSqlException.ConvertingDataTypeError(arg.Value.Type, "bit", state: 1);
                    }
                    break;
                case var n when BuiltInToken.Equals(n, "location"):
                case var n2 when BuiltInToken.Equals(n2, "provstr"):
                case var n3 when BuiltInToken.Equals(n3, "catalog"):
                    break;
                default:
                    throw SimulatedSqlException.NotAParameterForProcedure(name, "sp_addlinkedserver");
            }
        }

        if (!serverSupplied)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_addlinkedserver", "server");
        if (string.IsNullOrEmpty(server))
            throw SimulatedSqlException.NameCannotBeNull();

        var simulation = batch.Connection.Simulation;
        if (!simulation.AvailableRemotes.TryGetValue(server, out var target))
            throw new NotSupportedException($"sp_addlinkedserver '{server}' has no corresponding registered target Simulation; call Simulation.AddRemoteSimulation(\"{server}\", target) from the host code before activating the linked server.");

        simulation.ActiveLinkedServers[server] = new LinkedServer(server, target, srvProduct, provider, dataSource);
        // Activating (or re-activating) a linked server changes how
        // four-part-name FROM clauses bind at parse time; cached plans
        // parsed before this call must be invalidated.
        simulation.BumpSchemaVersion();
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

        // Real's own signature (probed 2026-09-25 against SQL Server 2025):
        // an unknown name leaves @server unsupplied (Msg 201), a third
        // argument is Msg 8144, and @droplogins takes only 'droplogins'.
        string? server = null;
        var serverSupplied = false;
        string[] positional = ["server", "droplogins"];
        if (arguments.Count(arg => arg.Name is null) > positional.Length)
            throw SimulatedSqlException.TooManyArgumentsToFunction("sp_dropserver");
        var positionalIndex = 0;
        foreach (var arg in arguments)
        {
            var name = arg.Name;
            name ??= positional[positionalIndex];
            positionalIndex++;
            if (BuiltInToken.Equals(name, "server"))
            {
                serverSupplied = true;
                server = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
            }
            else if (BuiltInToken.Equals(name, "droplogins")
                && !arg.Value.IsNull && !BuiltInToken.Equals(arg.Value.CoerceTo(SqlType.SystemName).AsString.TrimEnd(), "droplogins"))
            {
                throw SimulatedSqlException.InvalidLinkedServerParameter("sys.sp_dropserver");
            }
        }

        if (!serverSupplied)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_dropserver", "server");
        server ??= "(null)";

        if (!batch.Connection.Simulation.ActiveLinkedServers.TryRemove(server, out _))
            throw SimulatedSqlException.LinkedServerDoesNotExist(server);
        // Dropping a linked server removes a four-part-name binding;
        // cached plans referencing it would re-resolve incorrectly.
        batch.Connection.Simulation.BumpSchemaVersion();
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
