using System.Globalization;
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
    /// @provstr, @catalog, @linkedstyle)</c>; the routing key is
    /// <c>@server</c>, and the rest surface in <c>sys.servers</c>.
    /// </summary>
    /// <remarks>
    /// What real checks, in its order (probed 2026-09-25 and 2026-10-05
    /// against SQL Server 2025): the binding (Msg 8145, 8144, 201, and 8114
    /// for a <c>@linkedstyle</c> that isn't a <c>bit</c>), a NULL or empty
    /// name (Msg 15004); then the product — an omitted or NULL
    /// <c>@srvproduct</c> with no provider is <c>SQL Server</c>, which takes
    /// neither a provider (Msg 15428) nor properties without one (Msg 15426),
    /// any other product needs a provider (Msg 15427), and a NULL product with
    /// a provider is Msg 15429; then a transaction (Msg 15002); a provider other
    /// than SQL Server's (Msg 7222) or <c>@linkedstyle = 0</c> (Msg 15663); and
    /// a name already taken (Msg 15028). <c>SQLOLEDB</c> is recorded as
    /// <c>SQLNCLI</c>, a product past 128 characters is cut, and every new
    /// server maps every login to itself (<c>sys.linked_logins</c>).
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
        var serverSupplied = false;
        string? srvProduct = null;
        string? provider = null;
        string? dataSource = null;
        string? location = null;
        string? providerString = null;
        string? catalog = null;
        bool? linkedStyle = null;
        string[] positional = ["server", "srvproduct", "provider", "datasrc", "location", "provstr", "catalog", "linkedstyle"];
        var positionalIndex = 0;
        try
        {
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
                        srvProduct = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.NVarchar).AsString;
                        break;
                    case var n when BuiltInToken.Equals(n, "provider"):
                        provider = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.SystemName).AsString;
                        break;
                    case var n when BuiltInToken.Equals(n, "datasrc"):
                        dataSource = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.NVarchar).AsString;
                        break;
                    case var n when BuiltInToken.Equals(n, "linkedstyle"):
                        try
                        {
                            linkedStyle = arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.Bit).AsBoolean;
                        }
                        catch (SimulatedSqlException error) when (Parser.Expressions.Cast.IsConversionFailure(error.Number))
                        {
                            // Binding a procedure's argument reports the conversion at state 5.
                            throw SimulatedSqlException.ConvertingDataTypeError(arg.Value.Type, "bit", state: 5);
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
        }
        catch (SimulatedSqlException refusal)
        {
            refusal.SystemProcedureBindingError = true;
            throw;
        }

        if (!serverSupplied)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_addlinkedserver", "server");
        if (string.IsNullOrEmpty(server))
            throw SimulatedSqlException.NameCannotBeNull();
        RequireAlterAnyLinkedServer(batch);

        var hasProperties = dataSource is not null || location is not null || providerString is not null || catalog is not null;
        var isSqlServer = srvProduct is null ? provider is null : srvProduct.Equals("SQL Server", StringComparison.OrdinalIgnoreCase);
        if (provider is null && hasProperties)
            throw SimulatedSqlException.LinkedServerNeedsProvider();
        if (isSqlServer && provider is not null)
            throw SimulatedSqlException.LinkedServerSqlServerProductProperties();
        if (provider is null && !isSqlServer)
            throw SimulatedSqlException.LinkedServerUnknownProduct(srvProduct!);
        if (srvProduct is null && provider is not null)
            throw SimulatedSqlException.LinkedServerInvalidProduct("(null)");
        if (batch.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.ProcedureCannotRunInTransaction("sp_addlinkedserver");
        if (provider is not null && !IsSqlServerProvider(provider))
            throw SimulatedSqlException.OnlySqlServerProviderAllowed(state: 1);
        if (linkedStyle == false)
            throw SimulatedSqlException.AddServerNoLongerSupported();
        var simulation = batch.Connection.Simulation;
        if (simulation.ActiveLinkedServers.ContainsKey(server))
            throw SimulatedSqlException.LinkedServerAlreadyExists(server);

        if (!simulation.AvailableRemotes.TryGetValue(server, out var target))
            throw new NotSupportedException($"sp_addlinkedserver '{server}' has no corresponding registered target Simulation; call Simulation.AddRemoteSimulation(\"{server}\", target) from the host code before activating the linked server.");

        // A SQL Server product names the server itself as its data source.
        string product;
        if (isSqlServer)
        {
            product = "SQL Server";
            provider = "SQLNCLI";
            dataSource = server;
        }
        else
        {
            product = srvProduct!.Length > 128 ? srvProduct[..128] : srvProduct;
            if (provider!.Equals("SQLOLEDB", StringComparison.OrdinalIgnoreCase))
                provider = "SQLNCLI";
        }
        // The nvarchar(4000) properties keep what fits.
        static string? Fit(string? text) => text is { Length: > 4000 } ? text[..4000] : text;
        simulation.ActiveLinkedServers[server] = new LinkedServer(
            server, target, product, provider, Fit(dataSource), Fit(location), Fit(providerString), catalog, batch.CurrentStatement.UtcNow);
        // Activating (or re-activating) a linked server changes how
        // four-part-name FROM clauses bind at parse time; cached plans
        // parsed before this call must be invalidated.
        simulation.BumpSchemaVersion();
    }

    /// <summary>Whether a linked server's provider is one of SQL Server's own, the only kind real allows on Linux (probed 2026-10-05).</summary>
    private static bool IsSqlServerProvider(string provider) =>
        provider.StartsWith("SQLNCLI", StringComparison.OrdinalIgnoreCase)
        || provider.StartsWith("MSOLEDBSQL", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("SQLOLEDB", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Body for <c>sp_dropserver (@server, @droplogins)</c>. Deactivates a
    /// linked server by name: Msg 15002 inside a transaction, Msg 15015 when
    /// the server isn't active, and Msg 15190 when it maps a login other than
    /// its default self-mapping and <c>@droplogins</c> isn't
    /// <c>'droplogins'</c> (probed 2026-09-25 and 2026-10-05 against SQL Server
    /// 2025).
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
        var dropLogins = false;
        string[] positional = ["server", "droplogins"];
        if (arguments.Count(arg => arg.Name is null) > positional.Length)
            throw MarkBinding(SimulatedSqlException.TooManyArgumentsToFunction("sp_dropserver"));
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
            else if (BuiltInToken.Equals(name, "droplogins") && !arg.Value.IsNull)
            {
                dropLogins = BuiltInToken.Equals(arg.Value.CoerceTo(SqlType.SystemName).AsString.TrimEnd(), "droplogins")
                    ? true
                    : throw SimulatedSqlException.InvalidSystemProcedureOption("sp_dropserver");
            }
        }

        if (!serverSupplied)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_dropserver", "server");
        server ??= "(null)";
        RequireAlterAnyLinkedServer(batch);
        if (batch.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.ProcedureCannotRunInTransaction("sp_dropserver");

        var servers = batch.Connection.Simulation.ActiveLinkedServers;
        if (!servers.TryGetValue(server, out var linkedServer))
            throw SimulatedSqlException.LinkedServerDoesNotExist(server);
        if (!dropLogins && linkedServer.Logins.Exists(static login => !(login.LocalPrincipalId == 0 && login.UsesSelf)))
            throw SimulatedSqlException.LinkedServerHasLogins(server);
        _ = servers.TryRemove(server, out _);
        // Dropping a linked server removes a four-part-name binding;
        // cached plans referencing it would re-resolve incorrectly.
        batch.Connection.Simulation.BumpSchemaVersion();
    }

    /// <summary>Marks a refusal raised binding a system procedure's arguments, which reports at line 0.</summary>
    private static SimulatedSqlException MarkBinding(SimulatedSqlException refusal)
    {
        refusal.SystemProcedureBindingError = true;
        return refusal;
    }

    /// <summary>
    /// Body for <c>sp_serveroption (@server, @optname, @optvalue)</c>. Every
    /// option real takes is kept on the <see cref="LinkedServer"/> and shown by
    /// <c>sys.servers</c>; <c>rpc out</c>, <c>data access</c> and <c>remote
    /// proc transaction promotion</c> also change behavior. An on/off option
    /// takes <c>true</c> / <c>on</c> / <c>false</c> / <c>off</c> — <c>system</c>
    /// only the first two — a timeout a non-negative number, and
    /// <c>collation name</c> a known collation or NULL; any other value, or an
    /// option name real doesn't know, is Msg 15600, and an unknown server Msg
    /// 15015 (probed 2026-09-28 and 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpServerOption(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string[] positional = ["server", "optname", "optvalue"];
        if (arguments.Count(arg => arg.Name is null) > positional.Length)
            throw MarkBinding(SimulatedSqlException.TooManyArgumentsToFunction("sp_serveroption"));
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
                    throw MarkBinding(SimulatedSqlException.NotAParameterForProcedure(name, "sp_serveroption"));
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
        var value = optionValue.Value;
        var text = value.IsNull ? null : value.CoerceTo(SqlType.NVarchar).AsString.Trim();
        bool? toggle = text is null ? null
            : BuiltInToken.Equals(text, "true") || BuiltInToken.Equals(text, "on") ? true
            : BuiltInToken.Equals(text, "false") || BuiltInToken.Equals(text, "off") ? false
            : null;
        bool Toggle() => toggle ?? throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");
        var upper = option.Length <= 64 ? stackalloc char[option.Length] : new char[option.Length];
        _ = option.ToUpperInvariant(upper);
        switch (upper)
        {
            case "COLLATION COMPATIBLE":
                linkedServer.CollationCompatible = Toggle();
                break;
            case "COLLATION NAME":
                linkedServer.CollationName = text is null ? null
                    : Collation.TryGet(text) is { } collation ? collation.Name
                    : throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");
                break;
            case "CONNECT TIMEOUT":
                linkedServer.ConnectTimeout = ReadTimeout(text);
                break;
            case "DATA ACCESS":
                linkedServer.DataAccess = Toggle();
                break;
            case "DIST":
                linkedServer.Distributor = Toggle();
                break;
            case "LAZY SCHEMA VALIDATION":
                linkedServer.LazySchemaValidation = Toggle();
                break;
            case "PUB":
                linkedServer.Publisher = Toggle();
                break;
            case "QUERY TIMEOUT":
                linkedServer.QueryTimeout = ReadTimeout(text);
                break;
            case "REMOTE PROC TRANSACTION PROMOTION":
                linkedServer.RemoteProcTransactionPromotion = Toggle();
                break;
            case "RPC":
                linkedServer.RemoteLogin = Toggle();
                break;
            case "RPC OUT":
                linkedServer.RpcOut = Toggle();
                break;
            case "SUB":
                linkedServer.Subscriber = Toggle();
                break;
            case "SYSTEM":
                linkedServer.IsSystem = toggle == true ? true : throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");
                break;
            case "USE REMOTE COLLATION":
                linkedServer.UseRemoteCollation = Toggle();
                break;
            default:
                throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");
        }
    }

    /// <summary>A timeout option's value: a whole number of seconds, not negative (Msg 15600 otherwise).</summary>
    private static int ReadTimeout(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : throw SimulatedSqlException.InvalidSystemProcedureOption("sp_serveroption");

    /// <summary>
    /// Body for <c>sp_addlinkedsrvlogin (@rmtsrvname, @useself = 'TRUE',
    /// @locallogin = NULL, @rmtuser = NULL, @rmtpassword = NULL)</c>: maps a
    /// login — every login when <c>@locallogin</c> is NULL — to itself or to a
    /// remote login, replacing its earlier mapping. <c>@useself</c> takes
    /// <c>true</c> or <c>false</c> (Msg 15600), the server must exist (Msg
    /// 15015) and the login too (Msg 15007), as real checks them (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAddLinkedSrvLogin(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_addlinkedsrvlogin", calledAs, arguments, AddLinkedSrvLoginParameters);
        RequireAlterAnyLinkedServer(batch);
        var useSelf = (values[1].IsNull ? null
            : BuiltInToken.Equals(values[1].AsString.Trim(), "true") ? true
            : BuiltInToken.Equals(values[1].AsString.Trim(), "false") ? (bool?)false
            : null)
            ?? throw SimulatedSqlException.InvalidSystemProcedureOption("sp_addlinkedsrvlogin");
        var linkedServer = RequireLinkedServer(batch, values[0]);
        var principal = LinkedLoginPrincipal(batch, values[2]);
        _ = linkedServer.Logins.RemoveAll(login => login.LocalPrincipalId == principal);
        linkedServer.Logins.Add(new LinkedLogin(principal, useSelf, useSelf || values[3].IsNull ? null : values[3].AsString, batch.CurrentStatement.UtcNow));
    }

    /// <summary>
    /// Body for <c>sp_droplinkedsrvlogin (@rmtsrvname, @locallogin)</c>, both
    /// required: removes a login's mapping — every login's when
    /// <c>@locallogin</c> is NULL — doing nothing when there is none; Msg 15015
    /// for an unknown server and 15007 for an unknown login (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDropLinkedSrvLogin(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_droplinkedsrvlogin", calledAs, arguments, DropLinkedSrvLoginParameters);
        RequireAlterAnyLinkedServer(batch);
        var linkedServer = RequireLinkedServer(batch, values[0]);
        var principal = LinkedLoginPrincipal(batch, values[1]);
        _ = linkedServer.Logins.RemoveAll(login => login.LocalPrincipalId == principal);
    }

    /// <summary>
    /// Body for <c>sp_helplinkedsrvlogin [@rmtsrvname [, @locallogin]]</c>: one
    /// row per mapping of each linked server, or of the one named (Msg 15015
    /// when it isn't), the local login NULL for the mapping of every login.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpLinkedSrvLogin(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_helplinkedsrvlogin", calledAs, arguments, HelpLinkedSrvLoginParameters);
        var simulation = batch.Connection.Simulation;
        IEnumerable<LinkedServer> servers;
        if (values[0].IsNull)
            servers = simulation.ActiveLinkedServers.EnumerateValues().OrderBy(static server => server.Name, StringComparer.OrdinalIgnoreCase);
        else
            servers = [RequireLinkedServer(batch, values[0])];
        var rows = new List<SqlValue[]>();
        foreach (var server in servers)
        {
            foreach (var login in server.Logins.OrderBy(static login => login.LocalPrincipalId))
            {
                var localName = login.LocalPrincipalId == 0 ? null : simulation.Logins.EnumerateValues().FirstOrDefault(candidate => candidate.PrincipalId == login.LocalPrincipalId)?.Name;
                rows.Add([
                    SqlValue.FromSystemName(server.Name),
                    localName is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(localName),
                    SqlValue.FromInt16(login.UsesSelf ? (short)1 : (short)0),
                    login.RemoteName is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(login.RemoteName),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(
            [SqlType.SystemName, SqlType.SystemName, SqlType.SmallInt, SqlType.SystemName],
            ["Linked Server", "Local Login", "Is Self Mapping", "Remote Login"], rows);
    }

    private static readonly SystemProcedureParameter[] AddLinkedSrvLoginParameters =
    [
        new("rmtsrvname", SqlType.SystemName, maxLength: 128),
        new("useself", SqlType.NVarchar, maxLength: 8, defaultValue: SqlValue.FromNVarchar("TRUE")),
        new("locallogin", SqlType.SystemName, maxLength: 128, defaultValue: SqlValue.Null(SqlType.NVarchar)),
        new("rmtuser", SqlType.SystemName, maxLength: 128, defaultValue: SqlValue.Null(SqlType.NVarchar)),
        new("rmtpassword", SqlType.SystemName, maxLength: 128, defaultValue: SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] DropLinkedSrvLoginParameters =
    [
        new("rmtsrvname", SqlType.SystemName, maxLength: 128),
        new("locallogin", SqlType.SystemName, maxLength: 128),
    ];

    private static readonly SystemProcedureParameter[] HelpLinkedSrvLoginParameters =
    [
        new("rmtsrvname", SqlType.SystemName, maxLength: 128, defaultValue: SqlValue.Null(SqlType.NVarchar)),
        new("locallogin", SqlType.SystemName, maxLength: 128, defaultValue: SqlValue.Null(SqlType.NVarchar)),
    ];

    /// <summary>The linked server a login procedure names, else Msg 15015.</summary>
    private static LinkedServer RequireLinkedServer(BatchContext batch, SqlValue name)
    {
        var serverName = name.IsNull ? "(null)" : name.AsString;
        return batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var linkedServer)
            ? linkedServer
            : throw SimulatedSqlException.LinkedServerDoesNotExist(serverName);
    }

    /// <summary>The principal a login mapping is for: 0 for every login when <paramref name="name"/> is NULL, else the login's (Msg 15007 when there is none).</summary>
    private static int LinkedLoginPrincipal(BatchContext batch, SqlValue name) =>
        name.IsNull ? 0
        : batch.Connection.Simulation.Logins.TryGetValue(name.AsString, out var login) ? login.PrincipalId
        : throw SimulatedSqlException.NotAValidLogin(name.AsString);

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
