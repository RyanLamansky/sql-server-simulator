namespace SqlServerSimulator;

/// <summary>
/// The full-text query pipeline: <c>CONTAINS</c> / <c>FREETEXT</c> and the
/// <c>CONTAINSTABLE</c> / <c>FREETEXTTABLE</c> rowsets. Every expected row set
/// here was read off a live SQL Server 2025 (17.0.4065.4) instance with
/// Full-Text Search installed, running the same statements against the same
/// seed data; the divergences the simulator's smaller word breaker and stemmer
/// carry are catalogued in <c>docs/claude/full-text.md</c> and marked here.
/// </summary>
[TestClass]
public sealed class FullTextQueryTests
{
    /// <summary>
    /// The probe corpus, matching the reference database row for row so the
    /// expectations below can be compared against real by eye.
    /// </summary>
    private static Simulation Seeded(string catalogOptions = "")
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"create fulltext catalog ftcat {catalogOptions} as default",
            "create table dbo.docs (id int not null constraint pk_docs primary key, title nvarchar(200) null, body nvarchar(max) null, plain varchar(200) null)",
            """
            insert into dbo.docs (id, title, body, plain) values
             (1, N'red-hot chili', N'The quick brown fox jumps over the lazy dog', 'plain one'),
             (2, N'O''Brien and C', N'She runs and he ran while they are running', 'plain two'),
             (3, N'dotnet 123abc under_score', N'42 apples and 7 oranges', 'plain three'),
             (4, N'café résumé', N'cafe resume naive', 'plain four'),
             (5, N'mouse and geese', N'The mice ate the goose', 'plain five'),
             (6, N'alpha beta gamma delta epsilon', N'one two three four five six seven eight nine ten', 'plain six')
            """,
            "create fulltext index on dbo.docs (title language 1033, body language 1033, plain language 1033) key index pk_docs on ftcat with change_tracking auto");
        return sim;
    }

    /// <summary>Comma-joined ids the predicate matched, or <c>"-"</c>.</summary>
    private static string Hits(Simulation sim, string predicate)
    {
        using var reader = sim.ExecuteReader($"select id from dbo.docs where {predicate} order by id");
        List<string> ids = [];
        while (reader.Read())
            ids.Add(reader.GetInt32(0).ToString());
        return ids.Count == 0 ? "-" : string.Join(',', ids);
    }

    private static string Hits(string predicate) => Hits(Seeded(), predicate);

    // ---- word breaking ----------------------------------------------------

    [TestMethod]
    // A hyphen compounds: the whole run is indexed and so is each part.
    [DataRow("contains(title, 'red')", "1")]
    [DataRow("contains(title, 'hot')", "1")]
    [DataRow("contains(title, '\"red hot\"')", "1")]
    [DataRow("contains(title, '\"red-hot\"')", "1")]
    [DataRow("contains(title, 'redhot')", "-")]
    // An interior apostrophe joins, so the token is `o'brien` whole.
    [DataRow("contains(title, '\"O''Brien\"')", "2")]
    [DataRow("contains(title, 'O''Brien')", "2")]
    [DataRow("contains(title, 'obrien')", "-")]
    [DataRow("contains(title, 'brien')", "-")]
    // Single letters and digits are system stopwords.
    [DataRow("contains(title, 'o')", "-")]
    [DataRow("contains(title, 'c')", "-")]
    [DataRow("contains(body, '7')", "-")]
    [DataRow("contains(body, '42')", "3")]
    // A digit-carrying run stays one token; an underscore compounds like a hyphen.
    [DataRow("contains(title, '123abc')", "3")]
    [DataRow("contains(title, '123')", "-")]
    [DataRow("contains(title, 'abc')", "-")]
    [DataRow("contains(title, 'under_score')", "3")]
    [DataRow("contains(title, 'under')", "-")]
    [DataRow("contains(title, 'score')", "3")]
    // Case folds; accents don't, because the catalog defaults to accent-sensitive.
    [DataRow("contains(title, 'CHILI')", "1")]
    [DataRow("contains(title, 'cafe')", "-")]
    [DataRow("contains(title, N'café')", "4")]
    [DataRow("contains(body, N'café')", "-")]
    [DataRow("contains(title, 'RESUME')", "-")]
    [DataRow("contains(body, 'naive')", "4")]
    public void WordBreaking_Matches_Reference(string predicate, string expected) =>
        Assert.AreEqual(expected, Hits(predicate));

    [TestMethod]
    public void AccentInsensitive_Catalog_Folds_Diacritics()
    {
        var sim = Seeded("with accent_sensitivity = off");
        Assert.AreEqual("4", Hits(sim, "contains(title, 'cafe')"));
        Assert.AreEqual("4", Hits(sim, "contains(title, 'resume')"));
    }

    [TestMethod]
    public void Matching_Is_Case_Insensitive_Whatever_The_Column_Collation()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.cs (id int not null constraint pk_cs primary key, t nvarchar(100) collate Latin1_General_CS_AS)",
            "insert into dbo.cs values (1, N'Apple Banana'), (2, N'apple cherry')",
            "create fulltext index on dbo.cs (t language 1033) key index pk_cs on ftcat");
        foreach (var written in new[] { "apple", "APPLE", "Apple" })
        {
            using var reader = sim.ExecuteReader($"select id from dbo.cs where contains(t, '{written}') order by id");
            List<int> ids = [];
            while (reader.Read())
                ids.Add(reader.GetInt32(0));
            CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
        }
    }

    // ---- CONTAINS forms ---------------------------------------------------

    [TestMethod]
    // Prefix has meaning only as the last character inside the quotes.
    [DataRow("contains(title, '\"ch*\"')", "1")]
    [DataRow("contains(title, 'ch*')", "-")]
    [DataRow("contains(title, '\"re*\"')", "1")]
    [DataRow("contains(body, '\"qu*\"')", "1")]
    [DataRow("contains(title, '\"c*i\"')", "-")]
    [DataRow("contains(body, '\"*quick\"')", "1")]
    [DataRow("contains(title, '\"al* be*\"')", "6")]
    [DataRow("contains(title, '\"red-h*\"')", "1")]
    // Boolean composition, keyword and symbol spellings alike.
    [DataRow("contains(title, 'red AND chili')", "1")]
    [DataRow("contains(title, 'red and chili')", "1")]
    [DataRow("contains(title, 'red OR gamma')", "1,6")]
    [DataRow("contains(title, 'red AND NOT chili')", "-")]
    [DataRow("contains(title, 'red & chili')", "1")]
    [DataRow("contains(title, 'red | gamma')", "1,6")]
    [DataRow("contains(title, 'red &! chili')", "-")]
    [DataRow("contains(title, '(red OR gamma) AND NOT chili')", "6")]
    // Phrases run over consecutive positions, stopwords included.
    [DataRow("contains(body, '\"quick brown\"')", "1")]
    [DataRow("contains(body, '\"quick brown fox\"')", "1")]
    [DataRow("contains(body, '\"over the lazy dog\"')", "1")]
    [DataRow("contains(body, '\"jumps over lazy\"')", "-")]
    // An ignored term collapses the AND / AND NOT holding it but not an OR.
    [DataRow("contains(body, 'the')", "-")]
    [DataRow("contains(body, 'the AND quick')", "-")]
    [DataRow("contains(body, 'the OR quick')", "1")]
    [DataRow("contains(body, 'quick AND NOT the')", "-")]
    // FORMSOF and ISABOUT.
    [DataRow("contains(body, 'FORMSOF(INFLECTIONAL, run)')", "2")]
    [DataRow("contains(body, 'FORMSOF(INFLECTIONAL, mouse)')", "5")]
    [DataRow("contains(body, 'FORMSOF(THESAURUS, run)')", "-")]
    [DataRow("contains(body, 'ISABOUT(quick weight(.8), fox weight(.2))')", "1")]
    // Column specifications.
    [DataRow("contains(*, 'quick')", "1")]
    [DataRow("contains(*, 'plain')", "1,2,3,4,5,6")]
    [DataRow("contains((title, body), 'quick')", "1")]
    [DataRow("contains(plain, 'one')", "1")]
    public void ContainsForms_Match_Reference(string predicate, string expected) =>
        Assert.AreEqual(expected, Hits(predicate));

    [TestMethod]
    public void Alias_Qualified_Column_And_Star_Bind()
    {
        var sim = Seeded();
        using (var reader = sim.ExecuteReader("select d.id from dbo.docs d where contains(d.body, 'quick')"))
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1, reader.GetInt32(0));
        }
        using (var reader = sim.ExecuteReader("select d.id from dbo.docs d where contains(d.*, 'quick')"))
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1, reader.GetInt32(0));
        }
    }

    [TestMethod]
    public void Language_Argument_Parses_On_All_Four_Members()
    {
        var sim = Seeded();
        Assert.AreEqual("1", Hits(sim, "contains(body, 'quick', language 1033)"));
        Assert.AreEqual("1", Hits(sim, "freetext(body, 'quick', language 1033)"));
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select count(*) from containstable(dbo.docs, body, 'quick', language 1033)"));
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select count(*) from freetexttable(dbo.docs, body, 'quick', language 1033)"));
    }

    [TestMethod]
    public void Contains_Reads_As_A_Predicate_In_A_Case_Expression()
    {
        var sim = Seeded();
        Assert.AreEqual(1, sim.ExecuteScalar<int>(
            "select case when contains(body, 'quick') then 1 else 0 end from dbo.docs where id = 1"));
    }

    // ---- NEAR --------------------------------------------------------------

    private static Simulation SeededProximity()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.nr (id int not null constraint pk_nr primary key, t nvarchar(400))",
            """
            insert into dbo.nr values
             (1, N'aaa bbb'), (2, N'aaa xx bbb'), (3, N'aaa xx yy bbb'),
             (4, N'aaa xx yy zz bbb'), (5, N'aaa xx yy zz ww bbb'),
             (6, N'aaa xx yy zz ww vv bbb'), (7, N'bbb aaa'),
             (8, N'aaa qq qq qq qq qq qq qq qq qq qq qq qq bbb')
            """,
            "create fulltext index on dbo.nr (t language 1033) key index pk_nr on ftcat");
        return sim;
    }

    private static string ProximityHits(Simulation sim, string condition)
    {
        using var reader = sim.ExecuteReader($"select id from dbo.nr where contains(t, '{condition}') order by id");
        List<string> ids = [];
        while (reader.Read())
            ids.Add(reader.GetInt32(0).ToString());
        return ids.Count == 0 ? "-" : string.Join(',', ids);
    }

    [TestMethod]
    // The infix and no-distance generic forms are row-scope: both terms present.
    [DataRow("aaa NEAR bbb", "1,2,3,4,5,6,7,8")]
    [DataRow("aaa ~ bbb", "1,2,3,4,5,6,7,8")]
    [DataRow("NEAR(aaa, bbb)", "1,2,3,4,5,6,7,8")]
    [DataRow("NEAR((aaa,bbb),MAX)", "1,2,3,4,5,6,7,8")]
    // A distance counts the terms lying between the two, so 0 means adjacent.
    [DataRow("NEAR((aaa,bbb),0)", "1,7")]
    [DataRow("NEAR((aaa,bbb),1)", "1,2,7")]
    [DataRow("NEAR((aaa,bbb),2)", "1,2,3,7")]
    [DataRow("NEAR((aaa,bbb),3)", "1,2,3,4,7")]
    [DataRow("NEAR((aaa,bbb),4)", "1,2,3,4,5,7")]
    [DataRow("NEAR((aaa,bbb),12)", "1,2,3,4,5,6,7,8")]
    // TRUE requires the written order, MAX included.
    [DataRow("NEAR((aaa,bbb),2,TRUE)", "1,2,3")]
    [DataRow("NEAR((bbb,aaa),2,TRUE)", "7")]
    [DataRow("NEAR((bbb,aaa),MAX,TRUE)", "7")]
    [DataRow("NEAR((aaa,bbb),2,FALSE)", "1,2,3,7")]
    public void Near_Distances_Match_Reference(string condition, string expected) =>
        Assert.AreEqual(expected, ProximityHits(SeededProximity(), condition));

    [TestMethod]
    public void Near_Distance_Counts_Stopwords_As_Occupying_Positions()
    {
        // `The quick brown fox jumps over the lazy dog` — six terms lie between
        // `quick` and `dog` once the two stopwords are counted, so 5 misses and
        // 6 matches. Probe-confirmed on the reference.
        var sim = Seeded();
        Assert.AreEqual("-", Hits(sim, "contains(body, 'NEAR((quick,dog),5)')"));
        Assert.AreEqual("1", Hits(sim, "contains(body, 'NEAR((quick,dog),6)')"));
    }

    // ---- FREETEXT ----------------------------------------------------------

    [TestMethod]
    // Multiple words are an OR, not an AND.
    [DataRow("freetext(body, 'quick')", "1")]
    [DataRow("freetext(body, 'quick geese')", "1,5")]
    [DataRow("freetext(body, 'the quick')", "1")]
    [DataRow("freetext(body, 'the')", "-")]
    // Words match through their inflectional forms.
    [DataRow("freetext(body, 'running')", "2")]
    [DataRow("freetext(body, 'mouse')", "5")]
    [DataRow("freetext(body, 'apple')", "3")]
    [DataRow("freetext(body, 'goose')", "5")]
    // Quotes carry no operator meaning here.
    [DataRow("freetext(body, '\"quick brown\"')", "1")]
    [DataRow("freetext(*, 'geese')", "5")]
    [DataRow("freetext((title, body), 'geese')", "5")]
    public void FreeTextForms_Match_Reference(string predicate, string expected) =>
        Assert.AreEqual(expected, Hits(predicate));

    [TestMethod]
    // One word per row, so a stem that fails to relate two forms shows up as a
    // missing row. Each expectation is the reference's own answer.
    [DataRow("walking", "walking,walked,walks,walk")]
    [DataRow("studied", "studies,studied,study,studying")]
    [DataRow("boxes", "boxes,box,boxed,boxing")]
    [DataRow("children", "children,child")]
    [DataRow("women", "woman,women")]
    [DataRow("teeth", "teeth,tooth")]
    [DataRow("written", "wrote,written,writing,writes,write")]
    [DataRow("countries", "countries,country")]
    [DataRow("cities", "cities,city")]
    [DataRow("analyses", "analyses,analysis")]
    [DataRow("crises", "crises,crisis")]
    [DataRow("happier", "happier")]
    [DataRow("buses", "buses,bus")]
    [DataRow("classes", "classes,class")]
    [DataRow("moving", "moving,moved,move,moves")]
    [DataRow("knives", "knives,knife")]
    [DataRow("oxen", "oxen,ox")]
    [DataRow("data", "data,datum")]
    [DataRow("indices", "indices,index")]
    [DataRow("mice", "mice,mouse")]
    [DataRow("ran", "running,ran,runs,run")]
    public void Inflectional_Equivalence_Classes_Match_Reference(string search, string expectedWords)
    {
        string[] corpus =
        [
            "walking", "walked", "walks", "walk", "studies", "studied", "study", "studying",
            "boxes", "box", "boxed", "boxing", "children", "child", "man", "men", "woman", "women",
            "feet", "foot", "teeth", "tooth", "wrote", "written", "writing", "writes", "write",
            "better", "best", "good", "cities", "city", "countries", "country",
            "analyses", "analysis", "crises", "crisis", "happier", "happiest", "happy",
            "buses", "bus", "classes", "class", "moving", "moved", "move", "moves",
            "leaf", "knives", "knife", "running", "ran", "runs", "run", "mice", "mouse",
            "oxen", "ox", "data", "datum", "indices", "index",
        ];
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.w (id int not null constraint pk_w primary key, t nvarchar(100))",
            $"insert into dbo.w values {string.Join(", ", corpus.Select(static (word, i) => $"({i + 1}, N'{word}')"))}",
            "create fulltext index on dbo.w (t language 1033) key index pk_w on ftcat");
        using var reader = sim.ExecuteReader($"select t from dbo.w where freetext(t, '{search}') order by id");
        List<string> matched = [];
        while (reader.Read())
            matched.Add(reader.GetString(0));
        List<string> expected = [.. expectedWords.Split(',')];
        expected.Sort((left, right) => Array.IndexOf(corpus, left).CompareTo(Array.IndexOf(corpus, right)));
        CollectionAssert.AreEqual(expected, matched);
    }

    // ---- CONTAINSTABLE / FREETEXTTABLE ------------------------------------

    private static List<(object Key, int Rank)> TableRows(Simulation sim, string source)
    {
        using var reader = sim.ExecuteReader($"select [KEY], [RANK] from {source}");
        List<(object, int)> rows = [];
        while (reader.Read())
            rows.Add((reader.GetValue(0), reader.GetInt32(1)));
        return rows;
    }

    [TestMethod]
    public void ContainsTable_Projects_Key_And_Rank()
    {
        var sim = Seeded();
        var rows = TableRows(sim, "containstable(dbo.docs, body, 'quick')");
        Assert.HasCount(1, rows);
        Assert.AreEqual(1, rows[0].Key);
        Assert.IsGreaterThan(0, rows[0].Rank);
        Assert.IsLessThanOrEqualTo(1000, rows[0].Rank);
    }

    [TestMethod]
    public void ContainsTable_Column_Metadata_Is_Key_And_Rank()
    {
        var sim = Seeded();
        using var reader = sim.ExecuteReader("select * from containstable(dbo.docs, body, 'quick')");
        Assert.AreEqual(2, reader.FieldCount);
        Assert.AreEqual("KEY", reader.GetName(0));
        Assert.AreEqual("RANK", reader.GetName(1));
        Assert.AreEqual(typeof(int), reader.GetFieldType(0));
        Assert.AreEqual(typeof(int), reader.GetFieldType(1));
    }

    [TestMethod]
    public void ContainsTable_Key_Column_Follows_The_Key_Index_Type()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.sk (code varchar(20) not null constraint pk_sk primary key, t nvarchar(200))",
            "insert into dbo.sk values ('AA', N'alpha text'), ('BB', N'beta text')",
            "create fulltext index on dbo.sk (t language 1033) key index pk_sk on ftcat");
        using var reader = sim.ExecuteReader("select * from containstable(dbo.sk, t, 'alpha')");
        Assert.AreEqual(typeof(string), reader.GetFieldType(0));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("AA", reader.GetString(0));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public void ContainsTable_Honors_Boolean_Composition_And_Star()
    {
        var sim = Seeded();
        Assert.HasCount(1, TableRows(sim, "containstable(dbo.docs, body, 'quick AND fox')"));
        Assert.IsEmpty(TableRows(sim, "containstable(dbo.docs, body, 'quick AND NOT fox')"));
        Assert.HasCount(6, TableRows(sim, "containstable(dbo.docs, *, 'plain')"));
        Assert.HasCount(1, TableRows(sim, "containstable(dbo.docs, (title, body), 'quick')"));
    }

    [TestMethod]
    public void ContainsTable_Rank_Is_Deterministic_And_Ordered()
    {
        var sim = Seeded();
        var first = TableRows(sim, "containstable(dbo.docs, body, 'quick OR apples OR geese')");
        var second = TableRows(sim, "containstable(dbo.docs, body, 'quick OR apples OR geese')");
        CollectionAssert.AreEqual(first, second);
        for (var i = 1; i < first.Count; i++)
            Assert.IsLessThanOrEqualTo(first[i - 1].Rank, first[i].Rank);
    }

    [TestMethod]
    public void ContainsTable_Rank_Rises_With_Term_Frequency()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.rk (id int not null constraint pk_rk primary key, t nvarchar(400))",
            "insert into dbo.rk values (1, N'delta filler'), (2, N'delta delta filler'), (3, N'delta delta delta delta filler')",
            "create fulltext index on dbo.rk (t language 1033) key index pk_rk on ftcat");
        var rows = TableRows(sim, "containstable(dbo.rk, t, 'delta')");
        Assert.HasCount(3, rows);
        // Ordered by rank descending, so the four-occurrence row leads.
        Assert.AreEqual(3, rows[0].Key);
        Assert.AreEqual(1, rows[^1].Key);
    }

    [TestMethod]
    public void ContainsTable_TopNByRank_Limits_The_Rowset()
    {
        var sim = Seeded();
        Assert.HasCount(2, TableRows(sim, "containstable(dbo.docs, *, 'plain', 2)"));
        Assert.IsEmpty(TableRows(sim, "containstable(dbo.docs, *, 'plain', 0)"));
        Assert.HasCount(3, TableRows(sim, "containstable(dbo.docs, *, 'plain', language 1033, 3)"));
    }

    [TestMethod]
    public void FreeTextTable_Projects_The_Same_Shape_With_Or_Semantics()
    {
        var sim = Seeded();
        var rows = TableRows(sim, "freetexttable(dbo.docs, body, 'quick mice apples')");
        Assert.HasCount(3, rows);
        List<object> keys = [.. rows.Select(static row => row.Key)];
        keys.Sort();
        CollectionAssert.AreEqual(new object[] { 1, 3, 5 }, keys);
    }

    [TestMethod]
    public void FullTextTable_Joins_Back_To_The_Base_Table()
    {
        var sim = Seeded();
        Assert.AreEqual(1, sim.ExecuteScalar<int>(
            "select d.id from dbo.docs d join containstable(dbo.docs, body, 'quick') k on d.id = k.[KEY]"));
    }

    // ---- maintenance on DML -----------------------------------------------

    [TestMethod]
    public void Search_Follows_Insert_Update_And_Delete()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("insert into dbo.docs (id, title, body, plain) values (7, N'new', N'zeppelin flying machine', 'plain seven')");
        Assert.AreEqual("7", Hits(sim, "contains(body, 'zeppelin')"));

        _ = sim.ExecuteNonQuery("update dbo.docs set body = N'balloon flying machine' where id = 7");
        Assert.AreEqual("-", Hits(sim, "contains(body, 'zeppelin')"));
        Assert.AreEqual("7", Hits(sim, "contains(body, 'balloon')"));

        _ = sim.ExecuteNonQuery("delete from dbo.docs where id = 7");
        Assert.AreEqual("-", Hits(sim, "contains(body, 'balloon')"));
    }

    [TestMethod]
    public void Rolled_Back_Write_Leaves_Nothing_Searchable()
    {
        // One connection throughout: the write is only visible to the session
        // holding the transaction, and the rollback has to reach the same one.
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        string Search()
        {
            using var reader = connection.CreateCommand("select id from dbo.docs where contains(body, 'zeppelin')").ExecuteReader();
            return reader.Read() ? reader.GetInt32(0).ToString() : "-";
        }
        _ = connection.CreateCommand("begin transaction").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into dbo.docs (id, title, body, plain) values (8, N'x', N'zeppelin', 'p')").ExecuteNonQuery();
        Assert.AreEqual("8", Search());
        _ = connection.CreateCommand("rollback transaction").ExecuteNonQuery();
        Assert.AreEqual("-", Search());
    }

    // ---- errors ------------------------------------------------------------

    [TestMethod]
    public void Table_Without_A_FullText_Index_Is_Msg_7601_State_2()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table dbo.noft (id int primary key, t nvarchar(100))");
        var ex = sim.AssertSqlError("select 1 from dbo.noft where contains(t, 'x')", 7601);
        Assert.AreEqual("Cannot use a CONTAINS or FREETEXT predicate on table or indexed view 'noft' because it is not full-text indexed.", ex.Message);
        Assert.AreEqual(2, ex.State);
        Assert.AreEqual(16, ex.Class);
    }

    [TestMethod]
    public void Column_Outside_The_Index_Is_Msg_7601_State_3()
    {
        var ex = Seeded().AssertSqlError("select 1 from dbo.docs where contains(id, 'x')", 7601);
        Assert.AreEqual("Cannot use a CONTAINS or FREETEXT predicate on column 'id' because it is not full-text indexed.", ex.Message);
        Assert.AreEqual(3, ex.State);
    }

    [TestMethod]
    public void Unknown_Column_Is_Msg_207()
    {
        Seeded().AssertSqlError("select 1 from dbo.docs where contains(nosuch, 'x')", 207,
            "Invalid column name 'nosuch'.");
    }

    [TestMethod]
    [DataRow("select 1 from dbo.docs where contains(body, '')")]
    [DataRow("select 1 from dbo.docs where contains(body, '   ')")]
    [DataRow("select 1 from dbo.docs where freetext(body, '')")]
    [DataRow("select 1 from dbo.docs where contains(body, cast(null as nvarchar(10)))")]
    public void Null_Or_Empty_Predicate_Is_Msg_7645(string sql)
    {
        var ex = Seeded().AssertSqlError(sql, 7645);
        Assert.AreEqual("Null or empty full-text predicate.", ex.Message);
        Assert.AreEqual(1, ex.State);
        Assert.AreEqual(15, ex.Class);
    }

    [TestMethod]
    // State 1 — the condition ran out.
    [DataRow("(quick", "<end of input>", 1)]
    [DataRow("\"quick\" NEAR", "<end of input>", 1)]
    // State 2 — punctuation where a term belonged.
    [DataRow("\"quick", "\"", 2)]
    [DataRow("ISABOUT()", ")", 2)]
    // State 3 — a word where an operator or the end belonged.
    [DataRow("NOT x", "x", 3)]
    [DataRow("quick AND AND fox", "fox", 3)]
    [DataRow("FORMSOF(BOGUS, run)", "BOGUS", 3)]
    public void Condition_Syntax_Errors_Are_Msg_7630(string condition, string token, int state)
    {
        var ex = Seeded().AssertSqlError($"select 1 from dbo.docs where contains(body, '{condition}')", 7630);
        Assert.AreEqual($"Syntax error near '{token}' in the full-text search condition '{condition}'.", ex.Message);
        Assert.AreEqual(state, ex.State);
        Assert.AreEqual(15, ex.Class);
    }

    [TestMethod]
    public void Condition_Syntax_Is_Checked_At_Compile_Even_In_An_Untaken_Branch()
    {
        var ex = Seeded().AssertSqlError("if 1 = 0 select 1 from dbo.docs where contains(body, '(bad')", 7630);
        Assert.AreEqual(1, ex.State);
    }

    [TestMethod]
    public void Procedure_Body_Defers_Condition_Syntax_To_Execution()
    {
        // Real creates the procedure and raises at EXEC — the column and table
        // gates bind at CREATE, the condition string does not.
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create procedure dbo.p_bad as select 1 from dbo.docs where contains(body, '(bad')");
        _ = sim.AssertSqlError("exec dbo.p_bad", 7630);
    }

    [TestMethod]
    public void Procedure_Body_Binds_The_Table_And_Column_At_Create()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table dbo.noft (id int primary key, t nvarchar(100))");
        _ = sim.AssertSqlError("create procedure dbo.p_noft as select 1 from dbo.noft where contains(t, 'x')", 7601);
        _ = sim.AssertSqlError("create procedure dbo.p_badcol as select 1 from dbo.docs where contains(nosuch, 'x')", 207);
    }

    [TestMethod]
    public void Variable_Condition_Parses_Per_Execution()
    {
        var sim = Seeded();
        Assert.AreEqual(1, sim.ExecuteScalar<int>(
            "declare @v nvarchar(100) = N'quick'; select count(*) from dbo.docs where contains(body, @v)"));
        _ = sim.AssertSqlError(
            "declare @v nvarchar(100) = N'(bad'; select count(*) from dbo.docs where contains(body, @v)", 7630);
        _ = sim.AssertSqlError(
            "declare @v nvarchar(100) = null; select count(*) from dbo.docs where contains(body, @v)", 7645);
    }

    [TestMethod]
    public void Predicate_Outside_A_Query_Scope_Is_Msg_1046()
    {
        var sim = Seeded();
        sim.AssertSqlError(
            "alter table dbo.docs add constraint ck_x check (contains(title, 'x'))", 1046,
            "Subqueries are not allowed in this context. Only scalar expressions are allowed.");
    }

    [TestMethod]
    public void Ignored_Words_Raise_The_Severity_10_Info_Message()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        List<(int Number, string Message)> messages = [];
        ((SimulatedDbConnection)connection).InfoMessage += (_, args) =>
        {
            foreach (var error in args.Errors.Cast<SimulatedError>())
                messages.Add((error.Number, error.Message));
        };
        using var command = connection.CreateCommand("select id from dbo.docs where contains(body, 'the')");
        using (var reader = command.ExecuteReader())
        {
            Assert.IsFalse(reader.Read());
        }
        Assert.HasCount(1, messages);
        Assert.AreEqual(9927, messages[0].Number);
        Assert.AreEqual("Informational: The full-text search condition contained noise word(s).", messages[0].Message);
    }

    [TestMethod]
    public void TypeColumn_Binary_Document_Contributes_No_Terms()
    {
        // Real filters the varbinary document into text before indexing it; the
        // simulator has no filter, so the column is searchable but empty rather
        // than word-breaking its bytes.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.f (id int not null constraint pk_f primary key, doc varbinary(max), ext nvarchar(10), note nvarchar(100))",
            "insert into dbo.f values (1, 0x68656C6C6F, N'.txt', N'searchable note')",
            "create fulltext index on dbo.f (doc type column ext language 1033, note language 1033) key index pk_f on ftcat");
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select count(*) from dbo.f where contains(note, 'searchable')"));
        Assert.AreEqual(0, sim.ExecuteScalar<int>("select count(*) from dbo.f where contains(doc, 'hello')"));
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select count(*) from dbo.f where contains(*, 'note')"));
    }

    [TestMethod]
    public void Xml_Column_Indexes_Content_Not_Markup()
    {
        // Real indexes an xml column's text nodes and attribute values; the
        // element and attribute names are markup and never enter the index.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.jc (id int not null constraint pk_jc primary key, resume xml null)",
            "insert into dbo.jc values (1, N'<r kind=\"cv\"><skill>Engineer</skill></r>')",
            "create fulltext index on dbo.jc (resume language 1033) key index pk_jc on ftcat");
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select count(*) from dbo.jc where contains(resume, 'Engineer')"));
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select count(*) from dbo.jc where contains(resume, 'cv')"));
        Assert.AreEqual(0, sim.ExecuteScalar<int>("select count(*) from dbo.jc where contains(resume, 'skill')"));
        Assert.AreEqual(0, sim.ExecuteScalar<int>("select count(*) from dbo.jc where contains(resume, 'kind')"));
    }

    [TestMethod]
    public void CatalogProperty_Counts_Indexed_Rows_And_Distinct_Terms()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftcat as default",
            "create table dbo.w (id int not null constraint pk_w primary key, t nvarchar(100))",
            "insert into dbo.w values (1, N'alpha beta'), (2, N'beta gamma'), (3, N'the and of')",
            "create fulltext index on dbo.w (t language 1033) key index pk_w on ftcat");
        Assert.AreEqual(3, sim.ExecuteScalar<int>("select fulltextcatalogproperty('ftcat', 'ItemCount')"));
        // alpha / beta / gamma; row 3 holds only stopwords, which never enter the index.
        Assert.AreEqual(3, sim.ExecuteScalar<int>("select fulltextcatalogproperty('ftcat', 'UniqueKeyCount')"));
        Assert.AreEqual(1, sim.ExecuteScalar<int>("select fulltextcatalogproperty('ftcat', 'AccentSensitivity')"));
        Assert.AreEqual(0, sim.ExecuteScalar<int>("select fulltextcatalogproperty('ftcat', 'PopulateStatus')"));
        _ = sim.ExecuteNonQuery("insert into dbo.w values (4, N'delta')");
        Assert.AreEqual(4, sim.ExecuteScalar<int>("select fulltextcatalogproperty('ftcat', 'ItemCount')"));
        Assert.AreEqual(4, sim.ExecuteScalar<int>("select fulltextcatalogproperty('ftcat', 'UniqueKeyCount')"));
    }

    [TestMethod]
    public void Semantic_Rowsets_Still_Report_Themselves_Unmodeled()
    {
        var ex = Assert.ThrowsExactly<NotSupportedException>(() =>
            Seeded().ExecuteScalar("select count(*) from semantickeyphrasetable(dbo.docs, body) as t"));
        Assert.Contains("SEMANTICKEYPHRASETABLE", ex.Message);
    }

    [TestMethod]
    public void ContainsTable_On_An_Unindexed_Table_Is_Msg_7601()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table dbo.noft (id int primary key, t nvarchar(100))");
        _ = sim.AssertSqlError("select * from containstable(dbo.noft, t, 'x')", 7601);
    }
}
