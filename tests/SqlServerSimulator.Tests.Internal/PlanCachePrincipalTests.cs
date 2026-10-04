using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A cached plan is the same object whoever executes it, so two principals
/// sending one text in alternation — one able to unmask or write, one not —
/// must each get their own answer from the one plan: every replay masks and
/// checks permissions as the principal running it, never as the one that
/// compiled the plan. Each test asserts the answers and, through
/// <see cref="Simulation.DmlPlanHits"/> / <see cref="Simulation.PlanCacheHits"/>,
/// that the plan was reused rather than parsed per principal.
/// </summary>
[TestClass]
public sealed class PlanCachePrincipalTests
{
    private const string Setup = """
        create table m (id int primary key, s varchar(20) masked with (function = 'email()'), copy varchar(20) null);
        insert m (id, s) values (1, 'ann@example.com'), (2, 'bob@example.com'), (3, 'cat@example.com'), (4, 'dan@example.com');
        create table dst (id int primary key, c varchar(20) null);
        insert dst (id) values (1), (2), (3), (4);
        create table w (id int primary key, v int not null);
        create login seer with password = 'S3cret!PassA';
        create login blind with password = 'S3cret!PassB';
        create user seer for login seer;
        create user blind for login blind;
        grant select, insert, update, delete on m to seer, blind;
        grant select, insert, update, delete on dst to seer, blind;
        grant unmask to seer;
        grant select, insert, update on w to seer;
        grant select on w to blind;
        """;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        Execute(dbo, Setup);
        return simulation;
    }

    /// <summary>The DML plans recorded since <see cref="Setup"/>, whose own inserts record theirs.</summary>
    private static long RecordingsAfterSetup(Simulation simulation) => simulation.DmlPlanRecordings - 2;

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

    private static string?[] Column(Simulation simulation, string query)
    {
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        using var command = Command(dbo, query);
        using var reader = command.ExecuteReader();
        var values = new List<string?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return [.. values];
    }

    [TestMethod]
    public void MaskedUpdate_AlternatingLogins_EachStoresWhatItReads()
    {
        var simulation = Seeded();
        using var seer = Login(simulation, "seer", "S3cret!PassA");
        using var blind = Login(simulation, "blind", "S3cret!PassB");
        const string text = "SET NOCOUNT ON;\nUPDATE [m] SET [copy] = [s]\nOUTPUT 1\nWHERE [id] = @p0;";
        for (var id = 1; id <= 4; id++)
            Execute(id % 2 == 1 ? seer : blind, text, ("@p0", id));

        CollectionAssert.AreEqual(
            new[] { "ann@example.com", "bXXX@XXXX.com", "cat@example.com", "dXXX@XXXX.com" },
            Column(simulation, "select copy from m order by id"));
        AreEqual(1, RecordingsAfterSetup(simulation));
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void MaskedUpdate_RecordedByTheMaskedPrincipal_UnmasksForTheOther()
    {
        var simulation = Seeded();
        using var seer = Login(simulation, "seer", "S3cret!PassA");
        using var blind = Login(simulation, "blind", "S3cret!PassB");
        const string text = "update m set copy = left(s, 3) where id = @id";
        for (var id = 1; id <= 4; id++)
            Execute(id % 2 == 1 ? blind : seer, text, ("@id", id));

        CollectionAssert.AreEqual(
            new[] { "xxxx", "bob", "xxxx", "dan" },
            Column(simulation, "select copy from m order by id"));
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void MaskedMerge_AlternatingImpersonations_EachStoresWhatItReads()
    {
        var simulation = Seeded();
        using var seer = Impersonating(simulation, "seer");
        using var blind = Impersonating(simulation, "blind");
        const string text = "merge dst using m as src on dst.id = src.id and src.id = @id when matched then update set c = src.s;";
        for (var id = 1; id <= 4; id++)
            Execute(id % 2 == 1 ? blind : seer, text, ("@id", id));

        CollectionAssert.AreEqual(
            new[] { "aXXX@XXXX.com", "bob@example.com", "cXXX@XXXX.com", "dan@example.com" },
            Column(simulation, "select c from dst order by id"));
        AreEqual(1, RecordingsAfterSetup(simulation));
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void MaskedMerge_OneSessionSwitchingPrincipals_ReplaysForEach()
    {
        var simulation = Seeded();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        const string text = "merge dst using m as src on dst.id = src.id and src.id = @id when matched then update set c = src.s;";
        for (var id = 1; id <= 4; id++)
        {
            Execute(connection, $"execute as user = '{(id % 2 == 1 ? "seer" : "blind")}'");
            Execute(connection, text, ("@id", id));
            Execute(connection, "revert");
        }

        CollectionAssert.AreEqual(
            new[] { "ann@example.com", "bXXX@XXXX.com", "cat@example.com", "dXXX@XXXX.com" },
            Column(simulation, "select c from dst order by id"));
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void MaskedOutput_AlternatingLogins_EachReadsWhatItMay()
    {
        var simulation = Seeded();
        using var seer = Login(simulation, "seer", "S3cret!PassA");
        using var blind = Login(simulation, "blind", "S3cret!PassB");
        const string text = "delete m output deleted.s where id = @id";
        var read = new List<string>();
        for (var id = 1; id <= 4; id++)
        {
            using var command = Command(id % 2 == 1 ? blind : seer, text, ("@id", id));
            read.Add((string)command.ExecuteScalar()!);
        }

        CollectionAssert.AreEqual(new[] { "aXXX@XXXX.com", "bob@example.com", "cXXX@XXXX.com", "dan@example.com" }, read);
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void Insert_AlternatingPermittedAndRefusedLogins_ChecksEachReplay()
    {
        var simulation = Seeded();
        using var seer = Login(simulation, "seer", "S3cret!PassA");
        using var blind = Login(simulation, "blind", "S3cret!PassB");
        const string text = "insert w (id, v) values (@id, @v)";
        var errors = new int[4];
        for (var id = 1; id <= 4; id++)
            errors[id - 1] = ErrorOf(id % 2 == 1 ? seer : blind, text, ("@id", id), ("@v", id));

        CollectionAssert.AreEqual(new[] { 0, 229, 0, 229 }, errors);
        CollectionAssert.AreEqual(new[] { "1", "3" }, Column(simulation, "select cast(id as varchar(5)) from w order by id"));
        AreEqual(1, RecordingsAfterSetup(simulation));
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void Insert_RefusedFirst_RecordsNothingThenReplaysForBoth()
    {
        var simulation = Seeded();
        using var seer = Impersonating(simulation, "seer");
        using var blind = Impersonating(simulation, "blind");
        const string text = "insert w (id, v) values (@id, @v)";
        var errors = new int[4];
        for (var id = 1; id <= 4; id++)
            errors[id - 1] = ErrorOf(id % 2 == 1 ? blind : seer, text, ("@id", id), ("@v", id));

        CollectionAssert.AreEqual(new[] { 229, 0, 229, 0 }, errors);
        CollectionAssert.AreEqual(new[] { "2", "4" }, Column(simulation, "select cast(id as varchar(5)) from w order by id"));
        AreEqual(1, RecordingsAfterSetup(simulation));
        AreEqual(2, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void Update_AlternatingPermittedAndRefusedPrincipals_ChecksEachReplay()
    {
        var simulation = Seeded();
        using (var dbo = simulation.CreateDbConnection())
        {
            dbo.Open();
            Execute(dbo, "insert w values (1, 0)");
        }
        using var seer = Login(simulation, "seer", "S3cret!PassA");
        using var blind = Impersonating(simulation, "blind");
        const string text = "update w set v = v + @by where id = 1";
        var errors = new int[4];
        for (var run = 0; run < 4; run++)
            errors[run] = ErrorOf(run % 2 == 0 ? blind : seer, text, ("@by", 10));

        CollectionAssert.AreEqual(new[] { 229, 0, 229, 0 }, errors);
        CollectionAssert.AreEqual(new[] { "20" }, Column(simulation, "select cast(v as varchar(5)) from w"));
        AreEqual(3, simulation.DmlPlanHits);
    }

    [TestMethod]
    public void ChangeTable_ReplayChecksViewChangeTrackingForTheReplayingPrincipal()
    {
        var simulation = new Simulation();
        using var dbo = simulation.CreateDbConnection();
        dbo.Open();
        Execute(dbo, """
            alter database current set change_tracking = on;
            create table ct (id int primary key, v int);
            alter table ct enable change_tracking;
            insert ct values (1, 1);
            create user tracker without login;
            create user reader without login;
            grant select, view change tracking on ct to tracker;
            grant select on ct to reader;
            """);
        using var tracker = Impersonating(simulation, "tracker");
        using var reader = Impersonating(simulation, "reader");
        const string text = "select count(*) from changetable(changes ct, 0) as c";
        var errors = new[] { ErrorOf(dbo, text), ErrorOf(reader, text), ErrorOf(tracker, text), ErrorOf(reader, text) };

        CollectionAssert.AreEqual(new[] { 0, 229, 0, 229 }, errors);
        AreEqual(1, simulation.PlanCacheCount);
        AreEqual(3L, simulation.PlanCacheHits);
    }
}
