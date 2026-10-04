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
        string? location = null;
        string? providerString = null;
        string? catalog = null;
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
                    location = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.NVarchar).AsString;
                    break;
                case var n when BuiltInToken.Equals(n, "provstr"):
                    providerString = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.NVarchar).AsString;
                    break;
                case var n when BuiltInToken.Equals(n, "catalog"):
                    catalog = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                    break;
                default:
                    throw SimulatedSqlException.NotAParameterForProcedure(name, "sp_addlinkedserver");
            }
        }

        if (!serverSupplied)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_addlinkedserver", "server");
        if (string.IsNullOrEmpty(server))
            throw SimulatedSqlException.NameCannotBeNull();
        RequireAlterAnyLinkedServer(batch);

        var simulation = batch.Connection.Simulation;
        if (!simulation.AvailableRemotes.TryGetValue(server, out var target))
            throw new NotSupportedException($"sp_addlinkedserver '{server}' has no corresponding registered target Simulation; call Simulation.AddRemoteSimulation(\"{server}\", target) from the host code before activating the linked server.");

        // A SQL Server product names the server itself as its data source.
        if (string.Equals(srvProduct, "SQL Server", StringComparison.OrdinalIgnoreCase))
            dataSource = server;
        simulation.ActiveLinkedServers[server] = new LinkedServer(
            server, target, srvProduct, provider, dataSource, location, providerString, catalog, batch.CurrentStatement.UtcNow);
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
                throw SimulatedSqlException.InvalidSystemProcedureOption("sp_dropserver");
            }
        }

        if (!serverSupplied)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_dropserver", "server");
        server ??= "(null)";
        RequireAlterAnyLinkedServer(batch);

        if (!batch.Connection.Simulation.ActiveLinkedServers.TryRemove(server, out _))
            throw SimulatedSqlException.LinkedServerDoesNotExist(server);
        // Dropping a linked server removes a four-part-name binding;
        // cached plans referencing it would re-resolve incorrectly.
        batch.Connection.Simulation.BumpSchemaVersion();
    }

    /// <summary>
    /// Body for <c>sp_serveroption (@server, @optname, @optvalue)</c>. The three
    /// options a linked server's behavior reads — <c>rpc out</c>,
    /// <c>data access</c> and <c>remote proc transaction promotion</c> — are
    /// kept on the <see cref="LinkedServer"/>; real's other options are
    /// accepted and discarded. A value is <c>true</c> / <c>on</c> or
    /// <c>false</c> / <c>off</c>, any other (a number or NULL included) Msg
    /// 15600, as is an option name real doesn't know; an unknown server is Msg
    /// 15015 (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpServerOption(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string[] positional = ["server", "optname", "optvalue"];
        if (arguments.Count(arg => arg.Name is null) > positional.Length)
            throw SimulatedSqlException.TooManyArgumentsToFunction("sp_serveroption");
        SqlValue? server = null, optionName = null, optionValue = null;
        var positionalIndex = 0;
        foreach (var arg in arguments)
        {
            var name = arg.Name ?? positional[positionalIndex];
            positionalIndex++;
            switch (name)
            {
                case var n when BuiltInToken.Equals(n, "server"):
                    server = arg.Value;
                    break;
                case var n when BuiltInToken.Equals(n, "optname"):
                    optionName = arg.Value;
                    break;
                case var n when BuiltInToken.Equals(n, "optvalue"):
                    optionValue = arg.Value;
                    break;
                default:
                    throw SimulatedSqlException.NotAParameterForProcedure(name, "sp_serveroption");
            }
        }
        if (server is null)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_serveroption", "server");
        if (optionName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_serveroption", "optname");
        if (optionValue is null)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_serveroption", "optvalue");
        RequireAlterAnyLinkedServer(batch);

        var serverName = server.Value.IsNull ? "(null)" : server.Value.CoerceTo(SqlType.SystemName).AsString;
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var linkedServer))
            throw SimulatedSqlException.LinkedServerDoesNotExist(serverName);

        var option = optionName.Value.IsNull ? "" : optionName.Value.CoerceTo(SqlType.NVarchar).AsString.Trim();
        var text = optionValue.Value.IsNull || optionValue.Value.Type.Category != SqlTypeCategory.String
            ? ""
            : optionValue.Value.AsString.Trim();
        bool enabled;
        if (BuiltInToken.Equals(text, "true") || BuiltInToken.Equals(text, "on"))
            enabled = true;
        else if (BuiltInToken.Equals(text, "false") || BuiltInToken.Equals(text, "off"))
            enabled = false;
        else
            throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");

        if (BuiltInToken.Equals(option, "rpc out"))
            linkedServer.RpcOut = enabled;
        else if (BuiltInToken.Equals(option, "data access"))
            linkedServer.DataAccess = enabled;
        else if (BuiltInToken.Equals(option, "remote proc transaction promotion"))
            linkedServer.RemoteProcTransactionPromotion = enabled;
        else if (!IsDiscardedServerOption(option))
            throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");
    }

    /// <summary>
    /// The <c>sp_serveroption</c> names real accepts that change nothing the
    /// simulator models.
    /// </summary>
    private static bool IsDiscardedServerOption(string option)
    {
        foreach (var known in (string[])["collation compatible", "collation name", "connect timeout", "dist", "lazy schema validation", "pub", "query timeout", "rpc", "sub", "system", "use remote collation"])
        {
            if (BuiltInToken.Equals(option, known))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parse-and-discard body for <c>sp_addlinkedsrvlogin</c> /
    /// <c>sp_droplinkedsrvlogin</c>. The simulator has no principal-mapping
    /// model, but real BACPACs and migration scripts often emit these alongside
    /// <c>sp_addlinkedserver</c>; silently accepting them keeps those scripts
    /// running. Argument grammar is consumed (so a malformed call still
    /// raises through the standard EXEC arg parser) but the values are
    /// dropped.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpLinkedServerNoOp(BatchContext batch)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        if (!batch.IsSkipping)
            RequireAlterAnyLinkedServer(batch);
        yield break;
    }

    /// <summary>
    /// The linked-server procedures' gate: <c>ALTER ANY LINKED SERVER</c>,
    /// which <c>setupadmin</c> carries, else Msg 15247 once the arguments have
    /// bound (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static void RequireAlterAnyLinkedServer(BatchContext batch)
    {
        if (!batch.Connection.Simulation.SessionHoldsServerPermission(batch.Connection, Permission.AlterAnyLinkedServer))
            throw SimulatedSqlException.UserDoesNotHavePermission();
    }
}
