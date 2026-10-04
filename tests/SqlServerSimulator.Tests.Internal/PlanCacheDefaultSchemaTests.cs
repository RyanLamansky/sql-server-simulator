using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The one binding a cached plan takes from its principal is the default
/// schema an unqualified name searches, so the plan cache keys on it: two
/// principals with different default schemas sending one text alternately
/// each resolve their own object from a plan of their own, while principals
/// sharing a default schema share one plan. Real keys the same way (its
/// <c>user_id</c> plan attribute holds the default schema's id; probed
/// 2026-10-04 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class PlanCacheDefaultSchemaTests
{
    private const string Setup = """
        create table dbo.t (id int primary key, x varchar(20));
        insert dbo.t values (1, 'dbo.t');
        create table s.t (id int primary key, x varchar(20));
        insert s.t values (1, 's.t');
        create login ls with password = 'S3cret!PassA';
        create login ls2 with password = 'S3cret!PassB';
        create login ld with password = 'S3cret!PassC';
        create user ls for login ls with default_schema = s;
        create user ls2 for login ls2 with default_schema = s;
        create user ld for login ld;
        create user us without login with default_schema = s;
        create user ud without login;
        grant select, insert, update, delete to ls, ls2, ld, us, ud;
        """;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        Execute(dbo, "create schema s");
        Execute(dbo, Setup);
        return simulation;
    }

    private static DbConnection Login(Simulation simulation, string login, string password)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = $"User ID={login};Password={password}";
        connection.Open();
        return connection;
    }

    private static DbConnection Impersonating(Simulation simulation, string user)
    {
        var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, $"execute as user = '{user}'");
        return connection;
    }

    private static DbCommand Command(DbConnection connection, string text, int? id = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        if (id is { } value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = value;
            _ = command.Parameters.Add(parameter);
        }
        return command;
    }

    private static void Execute(DbConnection connection, string text, int? id = null)
    {
        using var command = Command(connection, text, id);
        _ = command.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection connection, string text, int? id = null)
    {
        using var command = Command(connection, text, id);
        return command.ExecuteScalar();
    }

    [TestMethod]
    public void Select_DifferentDefaultSchemas_AlternatingLogins_NeverSharePlans()
    {
        var simulation = Seeded();
        using var s = Login(simulation, "ls", "S3cret!PassA");
        using var dbo = Login(simulation, "ld", "S3cret!PassC");
        const string text = "select x from t where id = @id";
        var answers = new List<object?>();
        for (var i = 0; i < 6; i++)
            answers.Add(Scalar(i % 2 == 0 ? s : dbo, text, 1));

        CollectionAssert.AreEqual(new object?[] { "s.t", "dbo.t", "s.t", "dbo.t", "s.t", "dbo.t" }, answers);
        AreEqual(2, simulation.PlanCacheCount);
        AreEqual(4, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void Select_SharedDefaultSchema_SharesOnePlan()
    {
        var simulation = Seeded();
        using var first = Login(simulation, "ls", "S3cret!PassA");
        using var second = Login(simulation, "ls2", "S3cret!PassB");
        const string text = "select x from t where id = @id";
        for (var i = 0; i < 4; i++)
            AreEqual("s.t", Scalar(i % 2 == 0 ? first : second, text, 1));

        AreEqual(1, simulation.PlanCacheCount);
        AreEqual(3, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void Select_ExecuteAsFrames_AlternatingConnections_EachResolveTheirOwn()
    {
        var simulation = Seeded();
        using var s = Impersonating(simulation, "us");
        using var dbo = Impersonating(simulation, "ud");
        const string text = "select x from t where id = @id";
        for (var i = 0; i < 4; i++)
            AreEqual(i % 2 == 0 ? "s.t" : "dbo.t", Scalar(i % 2 == 0 ? s : dbo, text, 1));

        AreEqual(2, simulation.PlanCacheCount);
        AreEqual(2, simulation.PlanCacheHits);
    }

    [TestMethod]
    public void Update_DifferentDefaultSchemas_AlternatingLogins_EachWriteTheirOwnTable()
    {
        var simulation = Seeded();
        var recordedBySetup = simulation.DmlPlanRecordings;
        using var s = Login(simulation, "ls", "S3cret!PassA");
        using var dbo = Login(simulation, "ld", "S3cret!PassC");
        const string text = "set nocount on; update t set x = x + '+' where id = @id";
        for (var i = 0; i < 6; i++)
            Execute(i % 2 == 0 ? s : dbo, text, 1);

        AreEqual("s.t+++", Scalar(s, "select x from s.t"));
        AreEqual("dbo.t+++", Scalar(dbo, "select x from dbo.t"));
        AreEqual(2, simulation.DmlPlanRecordings - recordedBySetup);
        AreEqual(4, simulation.DmlPlanHits);
    }

    /// <summary>
    /// An EXECUTE AS earlier in the batch changes the default schema the
    /// batch's key was taken under, so the statements after it neither run
    /// from nor record a plan under that key.
    /// </summary>
    [TestMethod]
    public void Update_AfterExecuteAsInTheBatch_ResolvesAsTheNewPrincipal()
    {
        var simulation = Seeded();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        const string text = "update t set x = x + '+' where id = @id; execute as user = 'us'; update t set x = x + '+' where id = @id; revert;";
        for (var i = 0; i < 3; i++)
            Execute(connection, text, 1);

        AreEqual("s.t+++", Scalar(connection, "select x from s.t"));
        AreEqual("dbo.t+++", Scalar(connection, "select x from dbo.t"));
    }
}
