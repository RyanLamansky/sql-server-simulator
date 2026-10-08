using System.Data.Common;
using System.Globalization;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A single-table <c>UPDATE</c> or <c>DELETE</c> whose WHERE holds a small
/// uncorrelated <c>col IN (SELECT …)</c>, or an <c>EXISTS</c> correlating on
/// one of the target's columns, seeks the rows the subquery names rather than
/// judging every row of the target. The seek has to change nothing a statement
/// reports, so each test runs one statement against a table keyed on the
/// column — which seeks — and against an unindexed copy of it — which scans —
/// and compares what each sends and leaves behind.
/// </summary>
[TestClass]
public sealed class SubqueryDrivenWriteTests
{
    private static string Setup(string table, bool keyed) => $"""
        create table {table} (id int {(keyed ? "primary key" : "not null")}, v int not null, name varchar(10) null);
        insert {table} select value, value % 7, concat('n', value) from generate_series(1, 400);
        """;

    private const string Source = """
        create table src (g int not null, r int null, s varchar(10) null);
        insert src values (1, 5, '5'), (1, 7, '7'), (1, 7, '7'), (1, 999, '999'), (1, null, null),
            (2, null, null), (3, 9, 'x'), (4, 40, '40');
        insert src select 5, value, cast(value as varchar(10)) from generate_series(1, 100);
        """;

    private static string Transcript(string table, string statement, int group)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup(table, keyed: table == "k") + Source);
        using var connection = simulation.CreateOpenConnection();
        var transcript = new StringBuilder();
        Run(connection, statement.Replace("{T}", table, StringComparison.Ordinal), group, transcript);
        Run(connection, $"select @@rowcount, @@error; select count(*), sum(v), checksum_agg(checksum(id, v, name)) from {table}", group, transcript);
        return transcript.ToString();
    }

    private static void Run(DbConnection connection, string text, int group, StringBuilder transcript)
    {
        using var command = connection.CreateCommand(text, ("@g", group));
        try
        {
            using var reader = command.ExecuteReader();
            do
            {
                var rows = new List<string>();
                while (reader.Read())
                    rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
                // The order a write's OUTPUT returns its rows in is its plan's.
                rows.Sort(StringComparer.Ordinal);
                _ = transcript.AppendLine(string.Join(",", rows));
            }
            while (reader.NextResult());
            _ = transcript.Append("affected ").Append(reader.RecordsAffected).AppendLine();
        }
        catch (SimulatedSqlException error)
        {
            _ = transcript.Append("error ").Append(error.Number).Append(" line ").Append(error.LineNumber).Append(": ").AppendLine(error.Message);
        }
    }

    private static void AssertSeekMatchesScan(string statement, params int[] groups)
    {
        foreach (var group in groups)
            AreEqual(Transcript("h", statement, group), Transcript("k", statement, group), $"group {group}");
    }

    [TestMethod]
    public void UpdateWhereInSubquery_NullsDuplicatesAndMisses()
        => AssertSeekMatchesScan("update {T} set v = v + 1 output inserted.id, inserted.v, deleted.v where id in (select r from src where g = @g)", 1, 2, 3, 4, 5, 6);

    [TestMethod]
    public void DeleteWhereInSubquery()
        => AssertSeekMatchesScan("delete {T} output deleted.id where id in (select r from src where g = @g) and v > 0", 1, 4, 5, 6);

    [TestMethod]
    public void NotInSubquery_ThreeValued()
        => AssertSeekMatchesScan("update {T} set v = 0 where id not in (select r from src where g = @g) and id < 12", 1, 3, 6);

    [TestMethod]
    public void InSubquery_ConversionErrorInTheBody()
        => AssertSeekMatchesScan("update {T} set v = v + 1 where id in (select cast(s as int) from src where g = @g)", 1, 3, 6);

    [TestMethod]
    public void InSubquery_ScalarMismatchedTypes()
        => AssertSeekMatchesScan("update {T} set name = 'z' where name in (select s from src where g = @g) or id in (select r from src where g = @g)", 1, 3);

    [TestMethod]
    public void ExistsCorrelatedOnTheTarget()
        => AssertSeekMatchesScan("update {T} set v = v * 2 output inserted.id where exists (select 1 from src where src.r = {T}.id and src.g = @g)", 1, 2, 3, 5, 6);

    [TestMethod]
    public void NotExists_StillReadsEveryRow()
        => AssertSeekMatchesScan("delete {T} where not exists (select 1 from src where src.r = {T}.id and src.g = @g) and id > 395", 1, 2);

    [TestMethod]
    public void ExistsOverAnErroringBody()
        => AssertSeekMatchesScan("update {T} set v = v + 1 where exists (select 1 from src where cast(src.s as int) = {T}.id and src.g = @g)", 1, 3);

    [TestMethod]
    public void TriggerSeesTheRowsTheSeekWrote()
    {
        const string trigger = "create trigger {T}_tr on {T} after update as select count(*), sum(i.v) from inserted i";
        foreach (var group in new[] { 1, 4, 6 })
        {
            AreEqual(
                TranscriptWithTrigger("h", trigger, group),
                TranscriptWithTrigger("k", trigger, group));
        }

        static string TranscriptWithTrigger(string table, string trigger, int group)
        {
            var simulation = new Simulation();
            _ = simulation.ExecuteNonQuery(Setup(table, keyed: table == "k") + Source);
            _ = simulation.ExecuteNonQuery(trigger.Replace("{T}", table, StringComparison.Ordinal));
            using var connection = simulation.CreateOpenConnection();
            var transcript = new StringBuilder();
            Run(connection, $"update {table} set v = v + 1 where id in (select r from src where g = @g)", group, transcript);
            return transcript.ToString();
        }
    }

    [TestMethod]
    public void TheSeekReadsOnlyTheNamedRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup("k", keyed: true) + Source);
        using var connection = simulation.CreateOpenConnection();
        var sent = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => sent.Add(e.Message);
        _ = connection.CreateCommand("set statistics io on").ExecuteNonQuery();
        _ = connection.CreateCommand("update k set v = v + 1 where id in (select r from src where g = 1)").ExecuteNonQuery();
        _ = connection.CreateCommand("update k set v = v + 1 where exists (select 1 from src where src.r = k.id and src.g = 4)").ExecuteNonQuery();
        _ = connection.CreateCommand("update k set v = v + 1 where v = -1").ExecuteNonQuery();
        var reads = sent.Where(message => message.StartsWith("Table 'k'.", StringComparison.Ordinal))
            .Select(message => int.Parse(message.Split("logical reads ")[1].Split(',')[0], CultureInfo.InvariantCulture))
            .ToArray();
        HasCount(3, reads);
        IsLessThan(reads[2], reads[0]);
        IsLessThan(reads[2], reads[1]);
    }
}
