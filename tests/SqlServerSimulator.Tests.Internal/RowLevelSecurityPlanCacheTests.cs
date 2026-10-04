using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A security predicate is applied as each execution runs, never decided while
/// a plan parses, so one cached plan answers every principal with its own rows:
/// principals sending one text in alternation each read and write what their
/// predicate admits, while <see cref="Simulation.PlanCacheHits"/> /
/// <see cref="Simulation.DmlPlanHits"/> show the plan was replayed rather than
/// parsed again per principal. The Debug build's principal-read watch refuses
/// any plan whose parse read the principal, so these also hold the predicate
/// to running at execution only.
/// </summary>
[TestClass]
public sealed class RowLevelSecurityPlanCacheTests
{
    private const string Setup = """
        create table t (id int primary key, owner sysname not null, a int not null);
        insert t values (1, 'ann', 10), (2, 'bob', 20), (3, 'ann', 30), (4, 'dbo', 40);
        create login ann with password = 'S3cret!PassA';
        create login bob with password = 'S3cret!PassB';
        create user ann for login ann;
        create user bob for login bob;
        grant select, insert, update, delete on t to ann, bob;
        """;

    private const string Predicate = """
        create function dbo.owned(@owner sysname) returns table with schemabinding
        as return select 1 as ok where @owner = user_name() or cast(session_context(N'all') as int) = 1;
        """;

    private static Simulation Seeded(string policy)
    {
        var simulation = new Simulation();
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        Execute(dbo, Setup);
        Execute(dbo, Predicate);
        Execute(dbo, policy);
        return simulation;
    }

    private static DbConnection Login(Simulation simulation, string login, string password)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = $"User ID={login};Password={password}";
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

    private static string Ids(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, text, parameters);
        using var reader = command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return string.Join(",", ids);
    }

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

    [TestMethod]
    public void FilteredSelect_AlternatingLogins_EachReadsItsOwnRows()
    {
        var simulation = Seeded("create security policy p add filter predicate dbo.owned(owner) on dbo.t;");
        using var ann = Login(simulation, "ann", "S3cret!PassA");
        using var bob = Login(simulation, "bob", "S3cret!PassB");
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        const string text = "SELECT [t].[id] FROM [t] WHERE [t].[a] > @p0 ORDER BY [t].[id]";
        var hitsBefore = simulation.PlanCacheHits;

        AreEqual("1,3", Ids(ann, text, ("@p0", 0)));
        AreEqual("2", Ids(bob, text, ("@p0", 0)));
        AreEqual("4", Ids(dbo, text, ("@p0", 0)));
        AreEqual("3", Ids(ann, text, ("@p0", 10)));
        AreEqual(3L, simulation.PlanCacheHits - hitsBefore);
    }

    [TestMethod]
    public void FilteredSelect_SessionContextSetPerConnection_ReadsThroughOnePlan()
    {
        var simulation = Seeded("create security policy p add filter predicate dbo.owned(owner) on dbo.t;");
        using var ann = Login(simulation, "ann", "S3cret!PassA");
        using var bob = Login(simulation, "bob", "S3cret!PassB");
        Execute(bob, "exec sp_set_session_context N'all', 1");
        const string text = "select id from t order by id";

        AreEqual("1,3", Ids(ann, text));
        AreEqual("1,2,3,4", Ids(bob, text));
        Execute(bob, "exec sp_set_session_context N'all', 0");
        AreEqual("2", Ids(bob, text));
        AreEqual("1,3", Ids(ann, text));
    }

    [TestMethod]
    public void FilteredSelect_OneSessionSwitchingPrincipals_ReplaysForEach()
    {
        var simulation = Seeded("create security policy p add filter predicate dbo.owned(owner) on dbo.t;");
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        const string text = "select id from t order by id";
        var answers = new List<string>();
        foreach (var user in new[] { "ann", "bob", "ann", "bob" })
        {
            Execute(connection, $"execute as user = '{user}'");
            answers.Add(Ids(connection, text));
            Execute(connection, "revert");
        }

        CollectionAssert.AreEqual(new[] { "1,3", "2", "1,3", "2" }, answers);
    }

    [TestMethod]
    public void FilteredUpdate_AlternatingLogins_EachWritesOnlyItsOwnRows()
    {
        var simulation = Seeded("create security policy p add filter predicate dbo.owned(owner) on dbo.t;");
        using var ann = Login(simulation, "ann", "S3cret!PassA");
        using var bob = Login(simulation, "bob", "S3cret!PassB");
        const string text = "SET NOCOUNT ON;\nUPDATE [t] SET [a] = @p0\nOUTPUT 1\nWHERE [id] = @p1;";
        var hitsBefore = simulation.DmlPlanHits;
        for (var id = 1; id <= 4; id++)
        {
            Execute(ann, text, ("@p0", 100 + id), ("@p1", id));
            Execute(bob, text, ("@p0", 200 + id), ("@p1", id));
        }

        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        Execute(dbo, "exec sp_set_session_context N'all', 1");
        AreEqual("101,202,103,40", string.Join(",", Values(dbo, "select a from t order by id")));
        AreEqual(7L, simulation.DmlPlanHits - hitsBefore);
    }

    [TestMethod]
    public void BlockedInsert_AlternatingLogins_RefusesEachForeignRow()
    {
        var simulation = Seeded("create security policy p add block predicate dbo.owned(owner) on dbo.t after insert;");
        using var ann = Login(simulation, "ann", "S3cret!PassA");
        using var bob = Login(simulation, "bob", "S3cret!PassB");
        const string text = "insert t (id, owner, a) values (@id, @owner, 0)";

        AreEqual(0, ErrorOf(ann, text, ("@id", 10), ("@owner", "ann")));
        AreEqual(33504, ErrorOf(bob, text, ("@id", 11), ("@owner", "ann")));
        AreEqual(0, ErrorOf(bob, text, ("@id", 12), ("@owner", "bob")));
        AreEqual(33504, ErrorOf(ann, text, ("@id", 13), ("@owner", "bob")));
    }

    [TestMethod]
    public void PolicyStateChange_StalesTheCachedPlan()
    {
        var simulation = Seeded("create security policy p add filter predicate dbo.owned(owner) on dbo.t;");
        using var ann = Login(simulation, "ann", "S3cret!PassA");
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        const string text = "select id from t order by id";

        AreEqual("1,3", Ids(ann, text));
        var version = simulation.SchemaVersion;
        Execute(dbo, "alter security policy p with (state = off)");
        IsGreaterThan(version, simulation.SchemaVersion);
        AreEqual("1,2,3,4", Ids(ann, text));
        Execute(dbo, "alter security policy p with (state = on)");
        AreEqual("1,3", Ids(ann, text));
        Execute(dbo, "drop security policy p");
        AreEqual("1,2,3,4", Ids(ann, text));
    }

    private static List<int> Values(DbConnection connection, string text)
    {
        using var command = Command(connection, text);
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        return values;
    }
}
