using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER FULLTEXT INDEX</c> / <c>ALTER FULLTEXT CATALOG</c>, the options
/// <c>CREATE FULLTEXT INDEX</c> records, and the column and transaction rules
/// full-text DDL shares. Every expectation probed 2026-09-26 against SQL
/// Server 2025, on an index whose population had completed.
/// </summary>
[TestClass]
public sealed class FullTextAlterTests
{
    private static Simulation Indexed(string with = "")
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create fulltext catalog c as default;
            create table t (id int not null constraint pk primary key, s nvarchar(100), u nvarchar(100), n int, b varbinary(max), v varbinary(10));
            insert t (id, s, u) values (1, N'the quick fox', N'lazy dog'), (2, N'about a dog', null);
            create fulltext index on t (s) key index pk {with}
            """);
        return simulation;
    }

    private static string State(Simulation simulation)
        => (string)simulation.ExecuteScalar("select concat(is_enabled, change_tracking_state_desc, isnull(stoplist_id, -1)) from sys.fulltext_indexes")!;

    [TestMethod]
    [DataRow("", "1AUTO0")]
    [DataRow("with change_tracking off", "1OFF0")]
    [DataRow("with change_tracking = manual", "1MANUAL0")]
    [DataRow("with change_tracking off, no population", "1OFF0")]
    [DataRow("with stoplist = off", "1AUTO-1")]
    [DataRow("with (change_tracking = off, stoplist off)", "1OFF-1")]
    [DataRow("with (stoplist = system, search property list = off)", "1AUTO0")]
    public void CreateOptions_AreRecorded(string with, string expected)
        => AreEqual(expected, State(Indexed(with)));

    [TestMethod]
    [DataRow("alter fulltext index on t disable", "0AUTO0")]
    [DataRow("alter fulltext index on t set change_tracking manual", "1MANUAL0")]
    [DataRow("alter fulltext index on t set change_tracking = off", "1OFF0")]
    [DataRow("alter fulltext index on t set stoplist off", "1AUTO-1")]
    [DataRow("alter fulltext index on t drop (s)", "0AUTO0")]
    [DataRow("alter fulltext index on t start update population", "1AUTO0")]
    [DataRow("alter fulltext index on t set search property list off", "1AUTO0")]
    public void AlterIndex_ChangesItsState(string alter, string expected)
    {
        var simulation = Indexed();
        _ = simulation.ExecuteNonQuery(alter);
        AreEqual(expected, State(simulation));
    }

    [TestMethod]
    public void AddAndDropColumns_ChangeWhatIsSearched()
    {
        var simulation = Indexed();
        _ = simulation.ExecuteNonQuery("alter fulltext index on t add (u language 1033)");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t where contains(*, 'dog')"));
        _ = simulation.ExecuteNonQuery("alter fulltext index on t drop (s)");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t where contains(*, 'dog')"));
        _ = simulation.ExecuteNonQuery("alter fulltext index on t drop (u)");
        IsFalse((bool)simulation.ExecuteScalar("select is_enabled from sys.fulltext_indexes")!);
        _ = simulation.ExecuteNonQuery("alter fulltext index on t add (s)");
        IsTrue((bool)simulation.ExecuteScalar("select is_enabled from sys.fulltext_indexes")!);
    }

    [TestMethod]
    public void StoplistOff_SearchesStopwords()
    {
        var simulation = Indexed("with stoplist = off");
        AreEqual(1, simulation.ExecuteScalar("select id from t where contains(s, 'the')"));
        AreEqual(2, simulation.ExecuteScalar("select id from t where contains(s, '\"about\"')"));
        _ = simulation.ExecuteNonQuery("alter fulltext index on t set stoplist system");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t where contains(s, '\"about\"')"));
    }

    [TestMethod]
    public void DisabledIndex_StillAnswers()
    {
        var simulation = Indexed();
        _ = simulation.ExecuteNonQuery("alter fulltext index on t disable");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t where contains(s, 'fox')"));
    }

    [TestMethod]
    [DataRow("", "alter fulltext index on t set change_tracking off", 7638)]
    [DataRow("with change_tracking off", "alter fulltext index on t set change_tracking off", 7673)]
    [DataRow("with change_tracking manual", "alter fulltext index on t set change_tracking manual", 7661)]
    [DataRow("", "alter fulltext index on t set change_tracking auto", 7662)]
    [DataRow("", "alter fulltext index on t start full population", 7636)]
    [DataRow("", "alter fulltext index on t stop population", 7676)]
    [DataRow("", "alter fulltext index on t pause population", 9974)]
    [DataRow("with change_tracking manual", "alter fulltext index on t resume population", 9975)]
    [DataRow("", "alter fulltext index on t set stoplist off with no population", 30022)]
    public void AlterIndex_WarnsAsRealDoes(string with, string alter, int number)
    {
        var simulation = Indexed(with);
        using var connection = simulation.CreateOpenConnection();
        var messages = new List<int>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand(alter).ExecuteNonQuery();
        AreEqual(number, messages.Single());
    }

    [TestMethod]
    [DataRow("", "alter fulltext index on t set change_tracking manual")]
    [DataRow("with change_tracking manual", "alter fulltext index on t start full population")]
    [DataRow("", "alter fulltext index on t resume population")]
    [DataRow("", "alter fulltext index on t set stoplist system with no population")]
    public void AlterIndex_OtherwiseSilent(string with, string alter)
    {
        var simulation = Indexed(with);
        using var connection = simulation.CreateOpenConnection();
        var messages = 0;
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages += e.Errors.Count;
        _ = connection.CreateCommand(alter).ExecuteNonQuery();
        AreEqual(0, messages);
    }

    [TestMethod]
    [DataRow("", "alter fulltext index on nope enable", 208, 52)]
    [DataRow("", "alter fulltext index on t add (nope)", 1911, 4)]
    [DataRow("", "alter fulltext index on t drop (nope)", 1911, 5)]
    [DataRow("", "alter fulltext index on t add (n)", 7670, 1)]
    [DataRow("", "alter fulltext index on t add (v)", 7670, 1)]
    [DataRow("", "alter fulltext index on t add (b)", 7655, 1)]
    [DataRow("", "alter fulltext index on t add (s)", 7672, 1)]
    [DataRow("", "alter fulltext index on t add (u, u)", 7672, 1)]
    [DataRow("", "alter fulltext index on t drop (u)", 7677, 1)]
    [DataRow("", "alter fulltext index on t add (u statistical_semantics)", 41209, 3)]
    [DataRow("", "alter fulltext index on t alter column s add statistical_semantics", 41209, 3)]
    [DataRow("", "alter fulltext index on t add (u) with no population", 7663, 2)]
    [DataRow("with change_tracking manual", "alter fulltext index on t drop (s) with no population", 7663, 2)]
    [DataRow("with change_tracking off", "alter fulltext index on t start update population", 7664, 1)]
    [DataRow("", "alter fulltext index on t set stoplist = nope", 30023, 3)]
    [DataRow("", "alter fulltext index on t set search property list = pl", 30025, 3)]
    [DataRow("", "alter fulltext index on t drop (s); alter fulltext index on t enable", 7659, 2)]
    [DataRow("", "begin tran; alter fulltext index on t disable", 574, 0)]
    [DataRow("", "begin tran; alter fulltext catalog c reorganize", 574, 0)]
    [DataRow("", "begin tran; drop fulltext index on t", 574, 0)]
    [DataRow("", "alter fulltext catalog nope reorganize", 7641, 2)]
    public void AlterRefusals_MatchReal(string with, string sql, int number, int state)
        => AreEqual((byte)state, Indexed(with).AssertSqlError(sql, number).State);

    [TestMethod]
    [DataRow("create fulltext index on w (id) key index pkw", 7670, 1)]
    [DataRow("create fulltext index on w (s, s) key index pkw", 7672, 1)]
    [DataRow("create fulltext index on w (s statistical_semantics) key index pkw", 41209, 3)]
    [DataRow("create fulltext index on w (s) key index pkw with stoplist = nope", 30023, 1)]
    [DataRow("create fulltext index on w (s) key index pkw with (search property list = pl)", 30025, 1)]
    [DataRow("begin tran; create fulltext index on w (s) key index pkw", 574, 0)]
    [DataRow("begin tran; create fulltext catalog d", 574, 0)]
    [DataRow("drop fulltext index on w", 7658, 5)]
    [DataRow("alter fulltext index on w enable", 7658, 2)]
    public void CreateAndDropRefusals_MatchReal(string sql, int number, int state)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create fulltext catalog c as default; create table w (id int not null constraint pkw primary key, s nvarchar(100))");
        AreEqual((byte)state, simulation.AssertSqlError(sql, number).State);
    }

    [TestMethod]
    public void AlterCatalog_ChangesDefaultAndAccentSensitivity()
    {
        var simulation = Indexed();
        _ = simulation.ExecuteNonQuery("create fulltext catalog d");
        _ = simulation.ExecuteNonQuery("alter fulltext catalog d as default");
        AreEqual("d", simulation.ExecuteScalar("select name from sys.fulltext_catalogs where is_default = 1"));
        _ = simulation.ExecuteNonQuery("update t set s = N'café' where id = 1");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t where contains(s, 'cafe')"));
        _ = simulation.ExecuteNonQuery("alter fulltext catalog c rebuild with accent_sensitivity = off");
        AreEqual(0, simulation.ExecuteScalar("select fulltextcatalogproperty('c', 'AccentSensitivity')"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t where contains(s, 'cafe')"));
    }
}
