using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The index keyword TVFs — <c>sys.dm_fts_index_keywords</c> and its
/// <c>_by_document</c>, <c>_position_by_document</c> and <c>_by_property</c>
/// siblings — and the keyword encoding they share with
/// <c>sys.dm_fts_parser</c>. Every expectation is SQL Server 2025's own answer
/// over the same rows (probed 2026-10-17 against the reference).
/// </summary>
[TestClass]
public sealed class FullTextKeywordViewTests
{
    private const string Seed = """
        create fulltext catalog cat as default;
        create table t (id int not null constraint pk primary key, a nvarchar(200), b nvarchar(200));
        insert t values (1, N'café über naïve résumé', N'The quick 42 3.5'), (2, N'Cafe CAFÉ cafè', null), (3, null, null), (5, N'élan Ångström ça', N'x');
        create fulltext index on t (a, b) key index pk;
        """;

    /// <summary>Each row of <paramref name="query"/>, its columns joined by <c>:</c>, binaries in hex.</summary>
    private static string Rows(Simulation simulation, string query)
    {
        using var reader = simulation.ExecuteReader(query);
        List<string> rows = [];
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.GetValue(i) switch
                {
                    byte[] bytes => "0x" + Convert.ToHexString(bytes),
                    var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                };
            }
            rows.Add(string.Join(':', values));
        }
        return string.Join(' ', rows);
    }

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Seed);
        return simulation;
    }

    [TestMethod]
    public void Keywords_CountDocumentsPerColumn_AccentsAppendedAfterTheBaseLetters() =>
        AreEqual(
            "0x0033002E0035:3.5:3:1 0x00340032:42:3:1 0x0061006E0067007300740072006F006D00001A020202020213:ångström:2:1 "
            + "0x0063006100001C:ça:2:1 0x0063006100660065:cafe:2:1 0x006300610066006500000202020E:café:2:2 0x006300610066006500000202020F:cafè:2:1 "
            + "0x0065006C0061006E00000E:élan:2:1 0x006E00610069007600650000020213:naïve:2:1 0x006E006E003300640035:nn3d5:3:1 0x006E006E00340032:nn42:3:1 "
            + "0x0071007500690063006B:quick:3:1 0x0072006500730075006D00650000020E0202020E:résumé:2:1 0x0075006200650072000013:über:2:1 "
            + "0xFF:END OF FILE:2:3 0xFF:END OF FILE:3:3",
            Rows(Seeded(), "select * from sys.dm_fts_index_keywords(db_id(), object_id('t'))"));

    [TestMethod]
    public void PositionByDocument_ListsEachOccurrence_NoiseKeepingItsPosition() =>
        AreEqual(
            "0x006300610066006500000202020E:café:2:1:1 0x0075006200650072000013:über:2:1:2 0x006E00610069007600650000020213:naïve:2:1:3 "
            + "0x0072006500730075006D00650000020E0202020E:résumé:2:1:4 0xFF:END OF FILE:2:1:5 0x0071007500690063006B:quick:3:1:2 "
            + "0x00340032:42:3:1:3 0x006E006E00340032:nn42:3:1:3 0x0033002E0035:3.5:3:1:4 0x006E006E003300640035:nn3d5:3:1:4 0xFF:END OF FILE:3:1:5",
            Rows(Seeded(), "select * from sys.dm_fts_index_keywords_position_by_document(db_id(), object_id('t')) where document_id = 1 order by column_id, position, keyword"));

    [TestMethod]
    public void ByDocument_CountsOccurrences_InKeywordColumnDocumentOrder() =>
        AreEqual(
            "café:2:1:1 café:2:2:1 END OF FILE:2:1:1 END OF FILE:2:2:1 END OF FILE:2:5:1 END OF FILE:3:1:1 END OF FILE:3:3:1 END OF FILE:3:5:1",
            Rows(Seeded(), "select display_term, column_id, document_id, occurrence_count from sys.dm_fts_index_keywords_by_document(db_id(), object_id('t')) where display_term in (N'café', N'END OF FILE')"));

    /// <summary>
    /// A non-empty value ends one position past its last word, noise words
    /// counted, at 1 when it holds none; an empty one ends nowhere, and a row
    /// with nothing ends at position 1 of its highest-numbered column.
    /// </summary>
    [TestMethod]
    public void EndOfFile_PerNonEmptyValue_OrOnTheLastColumn() =>
        AreEqual(
            "neg:2:-5:1 END OF FILE:2:-5:2 END OF FILE:2:1:3 END OF FILE:4:1:1 END OF FILE:3:2:3 END OF FILE:4:3:1 word:2:4:1 END OF FILE:2:4:2 END OF FILE:4:6:1",
            Rows(new Simulation(), """
                create fulltext catalog cat as default;
                create table t (id int not null constraint pk primary key, a nvarchar(100), b nvarchar(100), c nvarchar(100));
                insert t values (1, N'the of', N'', N'!!'), (2, null, N'x y', null), (3, N'', N'', N''), (4, N'word', null, N''), (-5, N'neg', null, null), (6, null, null, null);
                create fulltext index on t (c, a, b) key index pk;
                select display_term, column_id, document_id, position from sys.dm_fts_index_keywords_position_by_document(db_id(), object_id('t')) order by document_id, column_id, position
                """));

    /// <summary>
    /// A key that isn't an integer is numbered in key order; an
    /// accent-insensitive catalog stores the bases without codes.
    /// </summary>
    [TestMethod]
    public void NonIntegerKey_NumbersInKeyOrder_AccentInsensitiveDropsTheCodes() =>
        AreEqual(
            "hund:3:1:1 END OF FILE:3:1:2 quick:2:2:4 quick:2:2:5 brown:2:2:6 END OF FILE:2:2:7 strasse:3:2:1 grosser:3:2:2 END OF FILE:3:2:3 END OF FILE:3:3:1",
            Rows(new Simulation(), """
                create table t (k varchar(10) not null constraint pk primary key, a nvarchar(max), g nvarchar(200));
                insert t values ('b', N'The and of quick quick brown', N'Straße größer'), ('a', null, N'Hund'), ('zz', N'', N'');
                create fulltext catalog cat with accent_sensitivity = off;
                create fulltext index on t (a language 1033, g language 1031) key index pk on cat;
                select display_term, column_id, document_id, position from sys.dm_fts_index_keywords_position_by_document(db_id(), object_id('t')) order by document_id, column_id, position
                """));

    /// <summary>
    /// The display term reads each accented letter back from its base and
    /// code, so an expanding letter comes back as its letters and some come
    /// back in upper case.
    /// </summary>
    [TestMethod]
    [DataRow("xǣ", "0x0078006100650000021717", "xāē")]
    [DataRow("xÿ", "0x0078007900000213", "xŸ")]
    [DataRow("xά", "0x007803B100000205", "xΆ")]
    [DataRow("xṹ", "0x0078007500000225", "xǚ")]
    [DataRow("xþ", "0x007800740068", "xth")]
    [DataRow("xð", "0x0078006400000268", "xð")]
    public void Parser_Keyword_EncodesTheAccents(string term, string keyword, string display) =>
        AreEqual($"{keyword}:{display}", Rows(new Simulation(), $"select keyword, display_term from sys.dm_fts_parser(N'\"{term}\"', 1033, NULL, 1)"));

    /// <summary>
    /// A key that isn't an integer keeps the id the initial crawl gave it
    /// through an update and a delete-and-reinsert, and a row written after
    /// the crawl starts real's next block of ids, its later ones following in
    /// write order rather than key order.
    /// </summary>
    [TestMethod]
    public void NonIntegerKey_RowsWrittenAfterTheCrawl_StartTheNextBlockInWriteOrder()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create fulltext catalog cat as default;
            create table t (k nvarchar(10) not null constraint pk primary key, s nvarchar(100));
            insert t values (N'b', N'alpha'), (N'd', N'beta'), (N'f', N'gamma');
            create fulltext index on t (s) key index pk;
            insert t values (N'z', N'zeta');
            insert t values (N'a', N'delta');
            update t set s = N'eta' where k = N'd';
            delete t where k = N'f';
            insert t values (N'f', N'theta');
            create table e (k nvarchar(10) not null constraint pk_e primary key, s nvarchar(100));
            create fulltext index on e (s) key index pk_e;
            insert e values (N'q', N'omega');
            """);
        AreEqual(
            "alpha:1 eta:2 theta:3 zeta:1002 delta:1003",
            Rows(simulation, "select display_term, document_id from sys.dm_fts_index_keywords_by_document(db_id(), object_id('t')) where display_term <> 'END OF FILE' order by document_id"));
        AreEqual(
            "omega:1002",
            Rows(simulation, "select display_term, document_id from sys.dm_fts_index_keywords_by_document(db_id(), object_id('e')) where display_term <> 'END OF FILE'"));
    }

    /// <summary>A crawl past a thousand rows skips the id between two blocks.</summary>
    [TestMethod]
    public void NonIntegerKey_CrawlPastAThousandRows_SkipsTheIdBetweenBlocks() =>
        AreEqual(
            "word1000:1000 word1001:1002 word1500:1501 omega:2003",
            Rows(new Simulation(), """
                create fulltext catalog cat as default;
                create table u (k nvarchar(10) not null constraint pk primary key, s nvarchar(100));
                insert u select right('0000' + cast(value as nvarchar(10)), 5), N'word' + cast(value as nvarchar(10)) from generate_series(1, 1500);
                create fulltext index on u (s) key index pk;
                insert u values (N'zzz', N'omega');
                select display_term, document_id from sys.dm_fts_index_keywords_by_document(db_id(), object_id('u')) where display_term in (N'word1000', N'word1001', N'word1500', N'omega') order by document_id
                """));

    [TestMethod]
    public void ByProperty_IsEmptyWithoutAPropertyList() =>
        AreEqual("", Rows(Seeded(), "select * from sys.dm_fts_index_keywords_by_property(db_id(), object_id('t'))"));

    [TestMethod]
    [DataRow("select * from sys.dm_fts_index_keywords(null, object_id('t'))", 30007)]
    [DataRow("select * from sys.dm_fts_index_keywords_by_document(db_id(), null)", 30007)]
    [DataRow("select * from sys.dm_fts_index_keywords(9999, object_id('t'))", 30003)]
    [DataRow("select * from sys.dm_fts_index_keywords_position_by_document(db_id(), 12345)", 30004)]
    [DataRow("select * from sys.dm_fts_index_keywords(db_id(), object_id('sys.objects'))", 30004)]
    [DataRow("create table u (id int not null primary key); select * from sys.dm_fts_index_keywords_by_property(db_id(), object_id('u'))", 30006)]
    [DataRow("select * from sys.dm_fts_index_keywords(db_id())", 313)]
    [DataRow("select * from sys.dm_fts_index_keywords('db', 'name')", 245)]
    public void Arguments_AreCheckedAsRealChecksThem(string query, int number) =>
        _ = Seeded().AssertSqlError(query, number);
}
