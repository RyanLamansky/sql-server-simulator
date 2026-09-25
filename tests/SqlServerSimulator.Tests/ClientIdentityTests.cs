using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The client identity a session reports — workstation and application name.
/// In-process they come from the connection string's <c>Workstation ID</c> /
/// <c>WSID</c> and <c>Application Name</c> / <c>App</c> keywords; over the TDS
/// endpoint from LOGIN7's <c>HostName</c> / <c>AppName</c> fields. Both feed
/// <c>HOST_NAME()</c> / <c>APP_NAME()</c>,
/// <c>sys.dm_exec_sessions.host_name</c> / <c>program_name</c>, and the
/// <c>HostName</c> / <c>ProgramName</c> columns of <c>sp_who</c> /
/// <c>sp_who2</c>. A session that reported neither answers the empty string,
/// which is what real reports for a client that sent none.
/// </summary>
[TestClass]
public sealed class ClientIdentityTests
{
    private static DbConnection Open(Simulation simulation, string connectionString)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = connectionString;
        connection.Open();
        return connection;
    }

    [TestMethod]
    public void NoKeywords_ReportEmptyStrings()
        => AreEqual("|", new Simulation().ExecuteScalar("select host_name() + '|' + app_name()"));

    [TestMethod]
    public void CanonicalKeywords_ReachTheScalars()
    {
        using var connection = Open(new Simulation(), "Workstation ID=ws-7;Application Name=Reporter");
        AreEqual("ws-7|Reporter", connection.CreateCommand("select host_name() + '|' + app_name()").ExecuteScalar());
    }

    [TestMethod]
    public void ShortAliases_ReachTheScalars()
    {
        using var connection = Open(new Simulation(), "WSID=ws-8;App=Loader");
        AreEqual("ws-8|Loader", connection.CreateCommand("select host_name() + '|' + app_name()").ExecuteScalar());
    }

    [TestMethod]
    public void DmExecSessions_ProjectsTheReportedIdentity()
    {
        using var connection = Open(new Simulation(), "Workstation ID=ws-9;Application Name=Monitor");
        using var reader = connection.CreateCommand(
            "select host_name, program_name from sys.dm_exec_sessions where session_id = @@spid").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("ws-9", reader.GetString(0));
        AreEqual("Monitor", reader.GetString(1));
    }

    [TestMethod]
    public void SpWho_ReportsTheHostName()
    {
        using var connection = Open(new Simulation(), "Workstation ID=ws-10");
        using var reader = connection.CreateCommand("exec sp_who").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("ws-10", reader.GetString(4).TrimEnd());
    }

    /// <summary>
    /// <c>sp_who2</c>'s widths are the measured maximum data lengths of the
    /// reported rows, so a populated HostName / ProgramName widens the column
    /// past the placeholder floors.
    /// </summary>
    [TestMethod]
    public void SpWho2_ReportsBothNames()
    {
        using var connection = Open(new Simulation(), "Workstation ID=workstation-11;Application Name=Dashboard");
        using var reader = connection.CreateCommand("exec sp_who2").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("workstation-11", reader.GetString(3).TrimEnd());
        AreEqual("Dashboard", reader.GetString(10).TrimEnd());
    }

    /// <summary>
    /// Reassigning the connection string before Open replaces the identity
    /// rather than merging with the previous one.
    /// </summary>
    [TestMethod]
    public void ReassignedConnectionString_ClearsThePreviousIdentity()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.ConnectionString = "Workstation ID=first;Application Name=first-app";
        connection.ConnectionString = "Application Name=second-app";
        connection.Open();
        using (connection)
            AreEqual("|second-app", connection.CreateCommand("select host_name() + '|' + app_name()").ExecuteScalar());
    }

    // An in-process connection has no network side; sys.dm_exec_connections
    // reports it in real's shared-memory shape.
    [TestMethod]
    public void InProcessConnection_ReportsTheSharedMemoryShape()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using var other = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(
            "select net_transport, encrypt_option, client_net_address, isnull(client_tcp_port, -1), isnull(net_packet_size, -1), isnull(protocol_version, -1), endpoint_id,"
            + " (select count(*) from sys.dm_exec_connections), (select count(distinct connection_id) from sys.dm_exec_connections)"
            + " from sys.dm_exec_connections where session_id = @@spid");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("Shared memory|FALSE|<local machine>|-1|-1|-1|2|2|2", string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
    }

    // The querying session's own request is always listed, running the
    // SELECT that reads it; an idle session has none.
    [TestMethod]
    public void Requests_ListTheRunningQueryOnly()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using var idle = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(
            "set datefirst 3; select concat(count(*), '|', max(status), '|', max(command), '|', max(blocking_session_id), '|', isnull(max(wait_type), '-'), '|', max(date_first), '|', max(nest_level), '|', max(user_id))"
            + " from sys.dm_exec_requests where session_id > 50");
        AreEqual("1|running|SELECT|0|-|3|0|1", command.ExecuteScalar());
    }
}
