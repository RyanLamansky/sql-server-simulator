using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A restricted principal's cached plans answer every security change made
/// between their executions — a grant, a revoke, a deny, a role membership, an
/// ownership change and a rolled-back revoke — as a fresh parse would. The
/// checker answers from an index of the stored permission rows rebuilt after
/// each change, and asks the objects a plan holds for their owners rather
/// than finding them again by id, so each test runs one text in a session
/// that keeps it cached while a <c>dbo</c> session changes what it may do,
/// and asserts both the answers and that the plan was replayed.
/// </summary>
[TestClass]
public sealed class PermissionChangeReplayTests
{
    private const string Setup = """
        create table t (id int primary key, a int not null, b int not null);
        insert t values (1, 10, 100), (2, 20, 200);
        create login reader with password = 'S3cret!PassR';
        create user reader for login reader;
        create user other without login;
        create role readers;
        """;

    private const string Select = "SELECT [t].[a], [t].[b]\nFROM [t] AS [t]\nWHERE [t].[id] = @id";

    private const string Update = "SET IMPLICIT_TRANSACTIONS OFF;\nSET NOCOUNT ON;\nUPDATE [t] SET [a] = @a\nOUTPUT 1\nWHERE [id] = @id;";

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        using var dbo = Dbo(simulation);
        Execute(dbo, Setup);
        Execute(dbo, "create schema app");
        Execute(dbo, "create table app.w (id int primary key)");
        return simulation;
    }

    private static DbConnection Dbo(Simulation simulation)
    {
        var connection = simulation.CreateDbConnection();
        connection.Open();
        return connection;
    }

    private static DbConnection Reader(Simulation simulation)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=reader;Password=S3cret!PassR";
        connection.Open();
        return connection;
    }

    private static DbCommand Command(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            _ = command.Parameters.Add(parameter);
        }
        return command;
    }

    private static void Execute(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, text, parameters);
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    /// <summary>The error number <paramref name="text"/> raises, or 0 when it runs.</summary>
    private static int ErrorOf(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        try
        {
            Execute(connection, text, parameters);
            return 0;
        }
        catch (SimulatedSqlException error)
        {
            return error.Number;
        }
    }

    private static int SelectError(DbConnection reader) => ErrorOf(reader, Select, ("@id", 1));

    [TestMethod]
    public void GrantThenRevoke_CachedSelectFollowsEachChange()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        Execute(dbo, "grant select on t to reader");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "revoke select on t to reader");
        AreEqual(229, SelectError(reader));
        Execute(dbo, "grant select on t to reader");
        AreEqual(0, SelectError(reader));
        IsGreaterThanOrEqualTo(2L, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void ColumnDenyAfterObjectGrant_CachedSelectRefusesTheColumnThenReadsAgain()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        Execute(dbo, "grant select on t to reader");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "deny select (b) on t to reader");
        AreEqual(230, SelectError(reader));
        Execute(dbo, "grant select (b) on t to reader");
        AreEqual(0, SelectError(reader));
        IsGreaterThanOrEqualTo(2L, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void DenyThenRevoke_CachedUpdateFollowsEachChange()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        Execute(dbo, "grant select, update on t to reader");
        AreEqual(0, ErrorOf(reader, Update, ("@a", 11), ("@id", 1)));
        Execute(dbo, "deny update on t to reader");
        AreEqual(229, ErrorOf(reader, Update, ("@a", 12), ("@id", 1)));
        Execute(dbo, "grant update on t to reader");
        AreEqual(0, ErrorOf(reader, Update, ("@a", 13), ("@id", 1)));
        Execute(dbo, "deny update (a) on t to reader");
        AreEqual(230, ErrorOf(reader, Update, ("@a", 14), ("@id", 1)));

        using var check = Command(dbo, "select a from t where id = 1");
        AreEqual(13, check.ExecuteScalar());
        IsGreaterThanOrEqualTo(3L, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void RoleMembershipChange_CachedSelectFollowsIt()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        Execute(dbo, "grant select on t to readers");
        AreEqual(229, SelectError(reader));
        Execute(dbo, "alter role readers add member reader");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "alter role readers drop member reader");
        AreEqual(229, SelectError(reader));

        // The procedure forms change membership without a schema change, so
        // the plan cached by the first read after them replays across the rest.
        Execute(dbo, "exec sp_addrolemember 'readers', 'reader'");
        AreEqual(0, SelectError(reader));
        var hits = simulation.PlanCacheHits;
        Execute(dbo, "exec sp_droprolemember 'readers', 'reader'");
        AreEqual(229, SelectError(reader));
        Execute(dbo, "exec sp_addrolemember 'db_datareader', 'reader'");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "exec sp_droprolemember 'db_datareader', 'reader'");
        AreEqual(229, SelectError(reader));
        AreEqual(hits + 3, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void AlterAuthorization_CachedSelectFollowsTheOwner()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        AreEqual(229, SelectError(reader));
        Execute(dbo, "alter authorization on object::t to reader");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "alter authorization on object::t to schema owner");
        AreEqual(229, SelectError(reader));

        const string selectApp = "SELECT [w].[id]\nFROM [app].[w] AS [w]";
        AreEqual(229, ErrorOf(reader, selectApp));
        Execute(dbo, "alter authorization on schema::app to reader");
        AreEqual(0, ErrorOf(reader, selectApp));
        Execute(dbo, "alter authorization on schema::app to dbo");
        AreEqual(229, ErrorOf(reader, selectApp));
    }

    [TestMethod]
    public void RolledBackRevoke_CachedSelectStillReads()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        Execute(dbo, "grant select on t to reader");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "begin tran; revoke select on t to reader; rollback;");
        AreEqual(0, SelectError(reader));
        Execute(dbo, "begin tran; deny select on t to reader; commit;");
        AreEqual(229, SelectError(reader));
        IsGreaterThanOrEqualTo(1L, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void OwnershipChainBrokenByAlterAuthorization_CachedProcedureCallFollowsIt()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        using var reader = Reader(simulation);
        Execute(dbo, "create procedure dbo.readt @id int as select a from dbo.t where id = @id");
        Execute(dbo, "grant execute on dbo.readt to reader");
        const string call = "exec dbo.readt @id = 1";
        AreEqual(0, ErrorOf(reader, call));
        Execute(dbo, "alter authorization on object::t to other");
        AreEqual(229, ErrorOf(reader, call));
        Execute(dbo, "grant select on t to reader");
        AreEqual(0, ErrorOf(reader, call));
        Execute(dbo, "revoke select on t to reader; alter authorization on object::t to schema owner");
        AreEqual(0, ErrorOf(reader, call));
    }

    [TestMethod]
    public void ExecuteAsAndRevert_OneSessionAlternatingAroundAGrant()
    {
        var simulation = Seeded();
        using var dbo = Dbo(simulation);
        for (var round = 0; round < 2; round++)
        {
            Execute(dbo, "execute as user = 'reader'");
            AreEqual(round == 0 ? 229 : 0, SelectError(dbo));
            Execute(dbo, "revert");
            AreEqual(0, SelectError(dbo));
            Execute(dbo, "grant select on t to reader");
        }
        IsGreaterThanOrEqualTo(2L, simulation.PlanCacheHits);
    }
}
