using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The word breaker, read through <c>sys.dm_fts_parser</c>, and the matching
/// rules its output drives. Every expectation is SQL Server 2025's own answer
/// to the same input (probed 2026-09-29 against the reference), and
/// <c>docs/claude/full-text.md</c> records the corpora the rules were fitted
/// and measured on.
/// </summary>
[TestClass]
public sealed class FullTextWordBreakerTests
{
    /// <summary>
    /// The parser's rows for <paramref name="condition"/> as
    /// <c>occurrence:display</c>, a noise word marked <c>~</c> and a sentence
    /// or paragraph marker written <c>EOS</c> / <c>EOP</c>.
    /// </summary>
    private static string Parsed(string condition, int lcid = 1033, string stoplist = "0", int accentSensitive = 1)
    {
        using var reader = new Simulation().ExecuteReader(
            $"select occurrence, special_term, display_term from sys.dm_fts_parser(N'{condition.Replace("'", "''")}', {lcid}, {stoplist}, {accentSensitive})");
        List<string> rows = [];
        while (reader.Read())
        {
            var special = reader.GetString(1);
            rows.Add(special switch
            {
                "End Of Sentence" => $"{reader.GetInt32(0)}:EOS",
                "End of Paragraph" => $"{reader.GetInt32(0)}:EOP",
                "Noise Word" => $"{reader.GetInt32(0)}:{reader.GetString(2)}~",
                _ => $"{reader.GetInt32(0)}:{reader.GetString(2)}",
            });
        }
        return string.Join(' ', rows);
    }

    [TestMethod]
    [DataRow("hello world", "1:hello 2:world")]
    [DataRow("The quick brown fox", "1:the~ 2:quick 3:brown 4:fox")]
    [DataRow("don't", "1:don't")]
    [DataRow("O'Brien", "1:o'brien")]
    [DataRow("rock'n'roll", "1:rock'n'roll")]
    [DataRow("dogs'", "1:dogs")]
    [DataRow("red-hot", "1:red-hot 1:red 2:hot")]
    [DataRow("well-known-fact", "1:well-known-fact 1:well~ 2:known 3:fact")]
    [DataRow("snake_case_name", "1:snake_case_name 1:snake 2:case 3:name")]
    [DataRow("e-mail", "1:e-mail")]
    [DataRow("pre--post", "1:pre 2:post")]
    [DataRow("u.s.a.", "1:u.s.a. 1:usa")]
    [DataRow("U.S.", "1:u.s. 1:us")]
    [DataRow("e.g.", "1:e.g")]
    [DataRow("a.b.c", "1:a~ 2:b~ 3:c~")]
    [DataRow("file.txt", "1:file.txt 1:file 2:txt")]
    [DataRow("www.example.com", "1:www.example.com 1:www 2:example 3:com")]
    [DataRow("sub.example.co.uk", "1:sub.example.co.uk 1:sub 2:example 3:co 4:uk")]
    [DataRow("node.js", "1:node.js 1:node 2:js")]
    [DataRow("ab.cd", "1:ab 2:cd")]
    [DataRow(".net", "1:.net")]
    [DataRow("c#", "1:c#")]
    [DataRow("c++", "1:c++")]
    [DataRow("f#", "1:f~")]
    [DataRow("j++", "1:j++")]
    [DataRow("at&t", "1:at&t")]
    [DataRow("a&b", "1:a&b")]
    [DataRow("abc&def", "1:abc 2:def")]
    [DataRow("42", "1:42 1:nn42")]
    [DataRow("007", "1:007 1:nn7~")]
    [DataRow("000", "1:000 1:nn000")]
    [DataRow("1,000", "1:1,000 1:nn1000")]
    [DataRow("1,000,000", "1:1,000,000 1:nn1000000")]
    [DataRow("1,00", "1:1~ 1:nn1~ 2:00 2:nn0~")]
    [DataRow("12,3456", "1:12 1:nn12 2:3456 2:nn3456")]
    [DataRow("1.50", "1:1.50 1:nn1d5")]
    [DataRow("0.05", "1:0.05 1:nn0d05")]
    [DataRow("3.14159", "1:3.14159 1:nn3d14159")]
    [DataRow("1,234.56", "1:1,234.56 1:nn1234d56")]
    [DataRow("1.234,56", "1:1.234 1:nn1d234 2:56 2:nn56")]
    [DataRow("1.2.3.4", "1:1~ 1:nn1~ 2:2~ 2:nn2~ 3:3~ 3:nn3~ 4:4~ 4:nn4~")]
    [DataRow("-5", "1:-5 1:nn5-")]
    [DataRow("+5", "1:+5 1:nn5~")]
    [DataRow("-3.5", "1:-3.5 1:nn3d5-")]
    [DataRow("$5", "1:$5 1:nn5$")]
    [DataRow("5€", "1:5€ 1:nn5€")]
    [DataRow("$1,000.50", "1:$1,000.50 1:nn1000d5$")]
    [DataRow("USD5", "1:usd5 1:nn5usd")]
    [DataRow("usd5", "1:usd5")]
    [DataRow("kr5", "1:kr5 1:nn5kr")]
    [DataRow("Q3", "1:q3 1:nn3q")]
    [DataRow("$ 5", "1:$ 5 1:nn5$")]
    [DataRow("5 USD", "1:5 usd 1:nn5usd")]
    [DataRow("5 Ft", "1:5 ft 1:nn5ft")]
    [DataRow("1 000", "1:1 000 1:nn1000 1:1~ 1:nn1~ 2:000 2:nn000")]
    [DataRow("5%", "1:5~ 1:nn5~")]
    [DataRow("3.5kg", "1:3~ 1:nn3~ 2:5kg")]
    [DataRow("5am", "1:5am 1:tt24050000")]
    [DataRow("10 am", "1:10 am 1:tt24100000")]
    [DataRow("10:30", "1:tt24103000 1:10:30 1:tt24223000")]
    [DataRow("23:59", "1:23:59 1:tt24235900")]
    [DataRow("12:00", "1:tt24000000 1:12:00 1:tt24120000")]
    [DataRow("10:30:45", "1:tt24103045 1:10:30:45 1:tt24223045")]
    [DataRow("10:30am", "1:10:30am 1:tt24103000")]
    [DataRow("3 o'clock", "1:tt24030000 1:3 o'clock 1:tt24150000")]
    [DataRow("2026-08-02", "1:2026-08-02 1:dd20260802 1:2026 2:08 3:02")]
    [DataRow("08/02/2026", "1:08/02/2026 1:dd20260208 1:08 2:02 3:2026")]
    [DataRow("02.08.2026", "1:02.08.2026 1:dd20260802 1:02 2:08 3:2026")]
    [DataRow("2026.08.02", "1:2026.08.02 1:dd20260208 1:2026 2:08 3:02")]
    [DataRow("26-08-02", "1:dd19020826 1:26-08-02 1:dd20020826 1:26 2:08 3:02")]
    [DataRow("12/31/2026", "1:12/31/2026 1:dd20261231 1:12 2:31 3:2026")]
    [DataRow("2026-13-45", "1:2026-13-45 1:2026 1:nn2026 2:13 2:nn13 3:45 3:nn45")]
    [DataRow("9999-12-31", "1:9999-12-31 1:9999 1:nn9999 2:12 2:nn12 3:31 3:nn31")]
    [DataRow("Aug 2, 2026", "1:aug 2, 2026 1:dd20260802 1:aug 2:2~ 3:2026")]
    [DataRow("2 August 2026", "1:2 august 2026 1:dd20260802 1:2~ 2:august 3:2026")]
    [DataRow("2-Aug-2026", "1:2-aug-2026 1:dd20260802 1:2~ 2:aug 3:2026")]
    [DataRow("Dec. 25, 2026", "1:dec. 25, 2026 1:dd20261225 1:dec. 2:25 3:2026")]
    [DataRow("aug 2, 2026", "1:aug 2:2~ 2:nn2~ 3:2026 3:nn2026")]
    [DataRow("foo@bar.com", "1:foo@bar.com 1:foo 2:bar 3:com")]
    [DataRow("user@localhost", "1:user 2:localhost")]
    [DataRow("http://x.com", "1:http://x.com 1:http 2:x~ 3:com")]
    [DataRow("https://www.example.com/path/to/page.html", "1:https://www.example.com/path/to/page.html 1:https 2:www 3:example 4:com 5:path 6:to~ 7:page 8:html")]
    [DataRow("C:\\Windows\\System32", "1:c:\\windows\\system32 1:c~ 2:windows 3:system32")]
    [DataRow("\\\\server\\share", "1:\\\\server\\share 1:server 2:share")]
    [DataRow("end. next", "1:end 9:EOS 10:next")]
    [DataRow("end.  Next. Another. last", "1:end 9:EOS 10:next 18:EOS 19:another~ 27:EOS 28:last")]
    [DataRow("Mr. Smith", "1:mr 2:smith")]
    [DataRow("mr. smith", "1:mr 9:EOS 10:smith")]
    [DataRow("etc. and", "1:etc 2:and~")]
    [DataRow("A. next", "1:a~ 2:next")]
    [DataRow("one... two", "1:one 2:two")]
    [DataRow("one.. two", "1:one 9:EOS 10:two")]
    [DataRow("end! next", "1:end 9:EOS 10:next")]
    [DataRow("end? next", "1:end 9:EOS 10:next")]
    [DataRow("end; next", "1:end 2:next")]
    [DataRow("end\nnext", "1:end 129:EOP 130:next")]
    [DataRow("one. two\n\nthree", "1:one 9:EOS 10:two 138:EOP 139:three")]
    [DataRow("end.\nnext", "1:end 129:EOP 130:next")]
    [DataRow("x.  y", "1:x~ 9:EOS 10:y~")]
    [DataRow("word. 42", "1:word 9:EOS 10:42 10:nn42")]
    [DataRow(":)", "1::)")]
    [DataRow("straße", "1:strasse")]
    [DataRow("café", "1:café")]
    [DataRow("Ａｂｃ", "1:abc")]
    [DataRow("ﬁne", "1:fine")]
    [DataRow("😀", "1:😀")]
    [DataRow("❤️", "")]
    [DataRow("€", "1:€")]
    [DataRow("abc$", "1:abc 2:$~")]
    [DataRow("$405 USD 814", "1:$405 usd 1:nn405usd 2:814 2:nn814")]
    [DataRow("UTF-8", "1:utf-8 1:utf 2:8~ 2:nn8~")]
    [DataRow("4:30 p.m.", "1:4:30 p.m. 1:tt24163000")]
    [DataRow("It!", "1:it!")]
    public void Breaks_As_Reference(string text, string expected) =>
        AreEqual(expected, Parsed('"' + text + '"'));

    [TestMethod]
    public void Accent_Insensitive_Folds_European_Marks_Only()
    {
        AreEqual("1:cafe 2:istanbul 3:ii", Parsed("\"café İstanbul ıi\"", accentSensitive: 0));
        // A Devanagari virama and a kana voicing mark are not accents.
        AreEqual("1:हिन्दी 2:がな", Parsed("\"हिन्दी がな\"", accentSensitive: 0));
    }

    [TestMethod]
    public void Neutral_And_British_Break_As_English() =>
        AreEqual(Parsed("\"The .NET 42 2026-08-02\""), Parsed("\"The .NET 42 2026-08-02\"", lcid: 2057));

    // ---- the condition as dm_fts_parser reports it -------------------------

    /// <summary>
    /// Every column but <c>keyword</c>, one row per line — the group numbers
    /// the leaves, the source is what the leaf was written as.
    /// </summary>
    private static string Report(string condition)
    {
        using var reader = new Simulation().ExecuteReader(
            $"select group_id, phrase_id, occurrence, special_term, display_term, expansion_type, source_term from sys.dm_fts_parser(N'{condition.Replace("'", "''")}', 1033, 0, 1)");
        List<string> rows = [];
        while (reader.Read())
            rows.Add($"{reader.GetInt32(0)}.{reader.GetInt32(1)}.{reader.GetInt32(2)}:{reader.GetString(3)}:{reader.GetString(4)}:{reader.GetInt32(5)}<{reader.GetString(6)}>");
        return string.Join(" | ", rows);
    }

    [TestMethod]
    [DataRow("quick AND (\"brown fox\" OR fox)", "1.0.1:Exact Match:quick:0<quick> | 2.0.1:Exact Match:brown:0<brown fox> | 2.0.2:Exact Match:fox:0<brown fox> | 3.0.1:Exact Match:fox:0<fox>")]
    [DataRow("FORMSOF(INFLECTIONAL, run)", "1.0.1:Exact Match:ran:2<run> | 1.0.1:Exact Match:run's:2<run> | 1.0.1:Exact Match:running:2<run> | 1.0.1:Exact Match:runs:2<run> | 1.0.1:Exact Match:runs':2<run> | 1.0.1:Exact Match:run:0<run>")]
    [DataRow("FORMSOF(INFLECTIONAL, walk)", "1.0.1:Exact Match:walk's:2<walk> | 1.0.1:Exact Match:walked:2<walk> | 1.0.1:Exact Match:walking:2<walk> | 1.0.1:Exact Match:walks:2<walk> | 1.0.1:Exact Match:walks':2<walk> | 1.0.1:Exact Match:walk:0<walk>")]
    [DataRow("FORMSOF(INFLECTIONAL, stop)", "1.0.1:Exact Match:stop's:2<stop> | 1.0.1:Exact Match:stopped:2<stop> | 1.0.1:Exact Match:stopping:2<stop> | 1.0.1:Exact Match:stops:2<stop> | 1.0.1:Exact Match:stops':2<stop> | 1.0.1:Exact Match:stop:0<stop>")]
    [DataRow("FORMSOF(INFLECTIONAL, carry)", "1.0.1:Exact Match:carried:2<carry> | 1.0.1:Exact Match:carries:2<carry> | 1.0.1:Exact Match:carries':2<carry> | 1.0.1:Exact Match:carry's:2<carry> | 1.0.1:Exact Match:carrying:2<carry> | 1.0.1:Exact Match:carry:0<carry>")]
    [DataRow("FORMSOF(INFLECTIONAL, see)", "1.0.1:Exact Match:saw:2<see> | 1.0.1:Exact Match:see's:2<see> | 1.0.1:Exact Match:seeing:2<see> | 1.0.1:Exact Match:seen:2<see> | 1.0.1:Exact Match:sees:2<see> | 1.0.1:Exact Match:sees':2<see> | 1.0.1:Noise Word:see:0<see>")]
    [DataRow("FORMSOF(THESAURUS, run)", "1.0.1:Exact Match:run:0<run>")]
    [DataRow("\"quick bro*\"", "1.0.1:Exact Match:quick:0<quick bro> | 1.0.2:Exact Match:bro:0<quick bro>")]
    [DataRow("qui*", "1.0.1:Exact Match:qui:0<qui*>")]
    [DataRow("quick AND the", "1.0.1:Exact Match:quick:0<quick> | 2.0.1:Noise Word:the:0<the>")]
    [DataRow("\"end. next\"", "1.0.1:Exact Match:end:0<end. next> | 1.0.9:End Of Sentence:END OF FILE:0<end. next> | 1.0.10:Exact Match:next:0<end. next>")]
    [DataRow("\"U.S.A.\"", "1.0.1:Exact Match:u.s.a.:0<U.S.A.> | 1.0.1:Exact Match:usa:0<U.S.A.>")]
    public void Condition_Report_Matches_Reference(string condition, string expected) =>
        AreEqual(expected, Report(condition));

    [TestMethod]
    public void Keyword_Is_The_Term_In_Utf16_And_Markers_Are_FF()
    {
        using var reader = new Simulation().ExecuteReader("select keyword from sys.dm_fts_parser(N'\"c#. x\"', 1033, 0, 1) order by occurrence");
        IsTrue(reader.Read());
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x63, 0x00, 0x23 }, (byte[])reader.GetValue(0));
        IsTrue(reader.Read());
        CollectionAssert.AreEqual(new byte[] { 0xFF }, (byte[])reader.GetValue(0));
    }

    [TestMethod]
    public void Null_Stoplist_Keeps_Noise_Words() =>
        AreEqual("1:the 2:quick", Parsed("\"the quick\"", stoplist: "null"));

    [TestMethod]
    public void Each_Language_Reads_Its_Own_Stoplist()
    {
        AreEqual("1:und~", Parsed("und", lcid: 1031));
        AreEqual("1:und", Parsed("und"));
        AreEqual("1:the", Parsed("the", lcid: 1031));
    }

    [TestMethod]
    [DataRow("select * from sys.dm_fts_parser(N'a', 9999, 0, 0)", 7696)]
    [DataRow("select * from sys.dm_fts_parser(N'a', 1033, 5, 0)", 30092)]
    [DataRow("select * from sys.dm_fts_parser(null, 1033, 0, 0)", 7645)]
    [DataRow("select * from sys.dm_fts_parser(N'a', null, 0, 0)", 7645)]
    [DataRow("select * from sys.dm_fts_parser(N'a', 1033, 0, null)", 7645)]
    [DataRow("select * from sys.dm_fts_parser(N'', 1033, 0, 0)", 7645)]
    [DataRow("select * from sys.dm_fts_parser(N'(a', 1033, 0, 0)", 7630)]
    [DataRow("select * from sys.dm_fts_parser(N'quick brown', 1033, 0, 0)", 7630)]
    [DataRow("select * from sys.dm_fts_parser(N'lightweight!', 1033, 0, 0)", 7630)]
    [DataRow("select * from sys.dm_fts_parser(N'a', 1033, 0)", 313)]
    public void Parser_Arguments_Are_Checked(string statement, int error) =>
        _ = new Simulation().AssertSqlError(statement, error);

    [TestMethod]
    public void Null_Argument_States_Name_The_Argument()
    {
        AreEqual(201, new Simulation().AssertSqlError("select * from sys.dm_fts_parser(null, 1033, 0, 0)", 7645).State);
        AreEqual(202, new Simulation().AssertSqlError("select * from sys.dm_fts_parser(N'a', null, 0, 0)", 7645).State);
        AreEqual(203, new Simulation().AssertSqlError("select * from sys.dm_fts_parser(N'a', 1033, 0, null)", 7645).State);
    }

    [TestMethod]
    public void Parser_Reads_Column_Arguments() =>
        AreEqual(3, new Simulation().ExecuteScalar("""
            select count(*) from (values (N'a b'), (N'x')) t(s)
            cross apply sys.dm_fts_parser('"' + t.s + '"', 1033, 0, 0) p
            """));

    [TestMethod]
    public void System_Stopwords_Carry_Every_Language()
    {
        var sim = new Simulation();
        AreEqual(15829, sim.ExecuteScalar("select count(*) from sys.fulltext_system_stopwords"));
        AreEqual(154, sim.ExecuteScalar("select count(*) from sys.fulltext_system_stopwords where language_id = 1033"));
        AreEqual(1729, sim.ExecuteScalar("select count(*) from sys.fulltext_system_stopwords where language_id = 1031"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fulltext_system_stopwords where language_id = 0 and stopword = '_'"));
    }

    // ---- what the breaker's output does to a search ------------------------

    private static Simulation Seeded(string rows, string language = "1033")
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.t (id int not null constraint pk_t primary key, s nvarchar(max) null)",
            $"insert dbo.t values {rows}",
            $"create fulltext index on dbo.t (s language {language}) key index pk_t");
        return sim;
    }

    private static string Hits(Simulation sim, string predicate)
    {
        using var reader = sim.ExecuteReader($"select id from dbo.t where {predicate} order by id");
        List<string> ids = [];
        while (reader.Read())
            ids.Add(reader.GetInt32(0).ToString());
        return ids.Count == 0 ? "-" : string.Join(',', ids);
    }

    private static readonly Lazy<Simulation> Corpus = new(() => Seeded("""
        (1, N'price 42.0 total'), (2, N'on 2 August 2026 we met'), (3, N'the .NET way'),
        (4, N'mail foo@bar.com now'), (5, N'end. Next word'), (6, N'at 10:30 sharp'),
        (7, N'cost $5 only'), (8, N'red-hot chili'), (9, N'the U.S.A. rocks'), (10, N'price 42 total'),
        (11, N'ships first'), (12, N'requires x requires'), (13, N'carbon-fiber requires'),
        (14, N'reds hotter'), (15, N'on 2026-08-02 we met'), (16, N'an x-ray image'),
        (17, N'word of mouth'), (18, N'a state-of-the-art bike'), (19, N'theory')
        """));

    [TestMethod]
    // A number matches through its normalized companion: 42 finds 42.0.
    [DataRow("contains(s, '42')", "1,10")]
    [DataRow("contains(s, 'nn42')", "1,10")]
    // .NET is one term; net alone finds nothing.
    [DataRow("contains(s, 'net')", "-")]
    [DataRow("contains(s, '\".net\"')", "3")]
    // An address is one term and its parts.
    [DataRow("contains(s, 'bar')", "4")]
    [DataRow("contains(s, '\"foo@bar.com\"')", "4")]
    // A sentence break pushes the next word eight positions on.
    [DataRow("contains(s, '\"end next\"')", "-")]
    [DataRow("contains(s, 'NEAR((end, next), 7)')", "-")]
    [DataRow("contains(s, 'NEAR((end, next), 8)')", "5")]
    // A time is found by its written form or its 24-hour companion, not its hour.
    [DataRow("contains(s, '\"22:30\"')", "6")]
    [DataRow("contains(s, '10')", "-")]
    [DataRow("contains(s, '\"$5\"')", "7")]
    // A compound is its composite and its parts.
    [DataRow("contains(s, '\"red hot\"')", "8")]
    [DataRow("contains(s, 'usa')", "9")]
    [DataRow("contains(s, '\"usa rocks\"')", "9")]
    // A date's composite stands for its first position only: another
    // spelling of the same date doesn't match, though both carry dd20260802.
    [DataRow("contains(s, '\"2026-08-02\"')", "15")]
    [DataRow("contains(s, '\"August 2, 2026\"')", "2")]
    // A noise word leading or trailing a phrase drops out entirely.
    [DataRow("contains(s, '\"with ships\"')", "11")]
    // The same term named twice in NEAR needs two occurrences.
    [DataRow("contains(s, 'NEAR((requires, requires), 0)')", "-")]
    [DataRow("contains(s, 'NEAR((requires, requires), 1)')", "12")]
    // A compound spans its parts, so nothing lies between it and the next word.
    [DataRow("contains(s, 'NEAR((\"carbon-fiber\", requires), 0)')", "13")]
    // A starred compound prefixes each part.
    [DataRow("contains(s, '\"red-hot*\"')", "8,14")]
    [DataRow("contains(s, '\"the*\"')", "19")]
    // Inflection expands a single-letter noise word, which then has to match;
    // a longer one drops out as in a phrase.
    [DataRow("contains(s, 'FORMSOF(INFLECTIONAL, x-ray)')", "16")]
    [DataRow("contains(s, 'FORMSOF(INFLECTIONAL, \"word x mouth\")')", "-")]
    [DataRow("contains(s, 'FORMSOF(INFLECTIONAL, \"word of mouth\")')", "17")]
    [DataRow("contains(s, 'FORMSOF(INFLECTIONAL, \"state-of-the-art\")')", "18")]
    // A starred phrase holding a noise word matches nothing.
    [DataRow("contains(s, '\"word of mou*\"')", "-")]
    public void Search_Reads_The_Breaker_Output(string predicate, string expected) =>
        AreEqual(expected, Hits(Corpus.Value, predicate));

    [TestMethod]
    public void Language_Argument_Picks_The_Stoplist()
    {
        var sim = Seeded("(1, N'the und cat'), (2, N'running dogs')");
        AreEqual("1", Hits(sim, "contains(s, 'und')"));
        AreEqual("-", Hits(sim, "contains(s, 'und', language 1031)"));
        AreEqual("-", Hits(sim, "contains(s, 'und', language 'German')"));
        AreEqual("-", Hits(sim, "contains(s, 'und', language 0x407)"));
        // A noise word is never indexed, whatever language the search reads it in.
        AreEqual("-", Hits(sim, "contains(s, 'the', language 1031)"));
        // Only English morphology is modeled; German reads the written form.
        AreEqual("2", Hits(sim, "freetext(s, 'run')"));
        AreEqual("-", Hits(sim, "freetext(s, 'run', language 1031)"));
    }

    [TestMethod]
    public void Language_Argument_Is_Validated()
    {
        var sim = Seeded("(1, N'x')");
        _ = sim.AssertSqlError("select id from dbo.t where contains(s, 'und', language 9999)", 7696);
        sim.AssertSqlError("select id from dbo.t where contains(s, 'und', language 'Klingon')", 7678,
            "The following string is not defined as a language alias in syslanguages: Klingon.");
    }

    [TestMethod]
    public void Column_Language_Defaults_To_English()
    {
        var sim = Seeded("(1, N'x')");
        sim.ExecuteBatches(
            "create table dbo.u (id int not null constraint pk_u primary key, s nvarchar(100) null)",
            "create fulltext index on dbo.u (s) key index pk_u");
        AreEqual(1033, sim.ExecuteScalar("select language_id from sys.fulltext_index_columns where object_id = object_id('dbo.u')"));
    }

    [TestMethod]
    public void German_Column_Indexes_Under_German_Stoplist()
    {
        var sim = Seeded("(1, N'der Hund und die Katze'), (2, N'the dog')", "1031");
        AreEqual("-", Hits(sim, "contains(s, 'und')"));
        AreEqual("1", Hits(sim, "contains(s, 'hund')"));
        AreEqual("2", Hits(sim, "contains(s, 'the')"));
    }
}
