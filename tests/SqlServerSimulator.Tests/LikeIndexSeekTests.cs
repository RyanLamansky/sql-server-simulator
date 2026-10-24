using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>LIKE</c> over an indexed column seeks the range its literal prefix
/// confines its matches to, as real's plan seeks one; whichever access path
/// the read takes, it answers exactly as the scan of an unindexed copy of the
/// table does — the strings here are the ones whose place in a collation's
/// order sits furthest from where their first characters put them.
/// </summary>
[TestClass]
public sealed class LikeIndexSeekTests
{
    private static readonly string[] Values =
    [
        "", " ", "S", "s", "Sm", "SM", "sm", "Smith", "smith", "SMITH", "Sm ith", "S m", "S-mith", "Sm-ith", "Sm'ith",
        "Sm\0x", "S\0mith", "Sm\u0001", "Sm\u001Fz", "Sm\u001F", "Sm\t", "Sm   ", "Sl", "Slz", "Sn", "Snow", "Śmith", "Smíth",
        "Sœ", "Sæ", "Smæ", "Sß", "Smß", "ss", "ß", "Sz", "Szz", "T", "Ta", "Z", "z", "zz", "Zz", "9", "99", "a9", "b", "A",
        "@", "`", "[", "Sm%x", "Sm_x", "Sm[x", "Sm\\x", "S\u0301m", "Sm\u0301", "Ｓｍ", "S\u200Bm", "Sm\u00ADx", "中文", "Sm中",
        "Az", "B", "Ba", "a", "Aa",
    ];

    private static readonly (string Pattern, string? Escape)[] Patterns =
    [
        ("Sm%", null), ("sm%", null), ("SM%", null), ("S%", null), ("s%", null), ("Sm", null), ("Smith", null), ("Sm_%", null),
        ("Sm[a-z]%", null), ("Sz%", null), ("Z%", null), ("z%", null), ("zz%", null), ("9%", null), ("a9%", null),
        ("Sm\\%%", "\\"), ("Sm!_x", "!"), ("S%m", null), ("%m%", null), ("", null), ("%", null), ("_m%", null),
        ("Ś%", null), ("Sl%", null), ("Smi%", null), ("ß%", null), ("ss%", null), ("Sæ%", null), ("Az%", null),
        ("A%", null), ("Sm!", "!"), ("xS%", "x"), ("Sm[", null), ("S\u0301%", null),
    ];

    [TestMethod]
    [DataRow("nvarchar(30)", "SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("varchar(30)", "SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("varchar(30)", "SQL_Latin1_General_CP1_CS_AS")]
    [DataRow("nvarchar(30)", "Latin1_General_100_CS_AS")]
    [DataRow("nvarchar(30)", "Latin1_General_CI_AI")]
    [DataRow("nchar(30)", "Latin1_General_CI_AS")]
    [DataRow("nvarchar(30)", "Latin1_General_BIN2")]
    [DataRow("varchar(30)", "Latin1_General_BIN2")]
    [DataRow("nvarchar(30)", "Latin1_General_BIN")]
    [DataRow("varchar(30)", "Latin1_General_100_CI_AS_SC_UTF8")]
    [DataRow("nvarchar(30)", "Danish_Norwegian_CI_AS")]
    [DataRow("nvarchar(30)", "Traditional_Spanish_CI_AS")]
    public void ASeekedLike_AnswersAsTheScanDoes(string type, string collation)
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand($"""
            create table seeked (id int primary key, name {type} collate {collation} not null);
            create index ix on seeked (name);
            create table scanned (id int primary key, name {type} collate {collation} not null);
            """).ExecuteNonQuery();
        for (var i = 0; i < Values.Length; i++)
        {
            _ = connection.CreateCommand("insert seeked values (@id, @name); insert scanned values (@id, @name);", ("@id", i), ("@name", Values[i])).ExecuteNonQuery();
        }

        foreach (var (pattern, escape) in Patterns)
        {
            var escapeClause = escape is null ? "" : " escape @escape";
            (string, object)[] parameters = escape is null ? [("@p", pattern)] : [("@p", pattern), ("@escape", escape)];
            var expected = Ids(connection, $"select id from scanned where name like @p{escapeClause} order by id", parameters);
            CollectionAssert.AreEqual(expected, Ids(connection, $"select id from seeked where name like @p{escapeClause} order by id", parameters), $"{pattern} under {collation}");
            // An ordered read walks the index between the same bounds. Which
            // of the values the collation calls equal comes first isn't
            // settled across its repertoire boundary (see collations.md), so
            // the walk is held to the rows it reads.
            CollectionAssert.AreEquivalent(expected, Ids(connection, $"select id from seeked where name like @p{escapeClause} order by name, id", parameters), $"{pattern} ordered under {collation}");
        }
    }

    [TestMethod]
    public void ANullPattern_MatchesNothing_AndAnEscapeOfTwoCharactersIsRefused()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int primary key, name nvarchar(20) not null); create index ix on t (name); insert t values (1, N'Smith')").ExecuteNonQuery();
        IsEmpty(Ids(connection, "select id from t where name like cast(null as nvarchar(10))", []));
        var refused = ThrowsExactly<SimulatedSqlException>(() => Ids(connection, "select id from t where name like N'Sm%' escape @e", [("@e", "ab")]));
        AreEqual(506, refused.Number);
    }

    private static List<int> Ids(DbConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand(sql, parameters);
        using var reader = command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }
}
