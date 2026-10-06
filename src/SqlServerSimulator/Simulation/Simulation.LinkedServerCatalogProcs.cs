using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The catalog procedures that describe linked servers rather than reach
// through one: sp_linkedservers, sp_testlinkedserver and sp_catalogs (probed
// 2026-10-06 against SQL Server 2025).
partial class Simulation
{
    private static readonly SystemProcedureParameter[] CatalogsParameters =
    [
        new("server_name", SqlType.NVarchar, 128),
    ];

    /// <summary>
    /// <c>sp_linkedservers</c> lists every server <c>sys.servers</c> holds, the
    /// instance's own row included, in its order, as <c>SRV_NAME</c>,
    /// <c>SRV_PROVIDERNAME</c>, <c>SRV_PRODUCT</c>, <c>SRV_DATASOURCE</c>,
    /// <c>SRV_PROVIDERSTRING</c>, <c>SRV_LOCATION</c> and <c>SRV_CAT</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpLinkedServers(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        _ = BindSystemProcedureArguments("sp_linkedservers", calledAs, arguments, []);
        var rows = new List<SqlValue[]>();
        foreach (var server in BuiltInResources.EnumerateSysServers(batch, batch.CurrentDatabase))
            rows.Add([server[1], server[3], server[2], server[4], server[6], server[5], server[7]]);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var text = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);
        yield return new SimulatedSqlResultSet(
            [name, name, name, text, text, text, name],
            ["SRV_NAME", "SRV_PROVIDERNAME", "SRV_PRODUCT", "SRV_DATASOURCE", "SRV_PROVIDERSTRING", "SRV_LOCATION", "SRV_CAT"],
            rows);
    }

    /// <summary>
    /// <c>sp_testlinkedserver @servername</c>, an extended procedure: connects
    /// to the server and returns 0 quietly when it answers. Its one parameter
    /// takes only a Unicode string — a bare word, an <c>N''</c> literal or an
    /// <c>nvarchar</c> variable — and anything else, NULL included, is Msg 214;
    /// a server <c>sys.servers</c> doesn't hold is Msg 7202, which ends the
    /// batch.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTestLinkedServer(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        if (arguments.Count > 1)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.TooManyArgumentsToFunction("sp_testlinkedserver", state: 90), 1);
        if (arguments.Count == 0 || arguments[0].IsDefault)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameter("sp_testlinkedserver", "servername", state: 95), 1);
        var value = arguments[0].Value;
        if (arguments[0].IsUntypedNull || value.Type is not (NVarcharSqlType or SystemNameSqlType) || value.IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.SystemProcedureParameterType("servername", "sysname", 90), 1);
        var serverName = value.AsString;
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var server))
        {
            var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.LinkedServerNotFound(serverName), 1);
            missing.EndedCalledBatch = false;
            throw missing;
        }
        // Opening a session is the test: a server that can't be reached
        // fails here as any use of it would.
        using var session = server.OpenSession(null);
    }

    /// <summary>
    /// <c>sp_catalogs @server_name</c> lists the databases of a linked server
    /// as <c>CATALOG_NAME</c> and a NULL <c>DESCRIPTION</c>, in the remote
    /// server's collation order.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCatalogs(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_catalogs", calledAs, arguments, CatalogsParameters);
        var serverName = values[0].IsNull ? "" : values[0].AsString;
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var server))
        {
            var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.LinkedServerNotFound(serverName), 1);
            missing.EndedCalledBatch = false;
            throw missing;
        }
        var target = server.Target;
        var names = new List<string>();
        foreach (var (name, _) in target.Databases)
            names.Add(name);
        names.Sort(target.ServerCollation);
        var description = SqlValue.Null(NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit));
        var rows = new List<SqlValue[]>(names.Count);
        foreach (var name in names)
            rows.Add([SqlValue.FromNVarchar(name), description]);
        yield return new SimulatedSqlResultSet(
            [NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit)],
            ["CATALOG_NAME", "DESCRIPTION"],
            rows);
    }
}
