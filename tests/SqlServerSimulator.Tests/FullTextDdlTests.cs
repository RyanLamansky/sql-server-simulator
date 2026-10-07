using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the full-text DDL surface (<c>CREATE/DROP FULLTEXT
/// CATALOG</c> + <c>CREATE/DROP FULLTEXT INDEX</c>) and the catalog views
/// that report it (<c>sys.fulltext_catalogs</c> / <c>sys.fulltext_indexes</c>
/// / <c>sys.fulltext_index_columns</c>).
/// </summary>
[TestClass]
public sealed class FullTextDdlTests
{
    private const string AwCatalog =
        "create fulltext catalog [AW2025FullTextCatalog] as default";

    private const string DocTable = """
        create table dbo.doc (
            id int identity(1,1) not null constraint pk_doc primary key,
            body nvarchar(max) null
        )
        """;

    private static Simulation BuildSimWithDoc()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AwCatalog);
        _ = sim.ExecuteNonQuery(DocTable);
        return sim;
    }

    [TestMethod]
    public void CreateFullTextCatalog_AsDefault_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AwCatalog);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fulltext_catalogs where name = 'AW2025FullTextCatalog'"));
        IsTrue((bool)sim.ExecuteScalar("select is_default from sys.fulltext_catalogs where name = 'AW2025FullTextCatalog'")!);
    }

    [TestMethod]
    public void CreateFullTextCatalog_DefaultsAccentSensitiveTrue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        IsTrue((bool)sim.ExecuteScalar("select is_accent_sensitivity_on from sys.fulltext_catalogs where name = 'mycat'")!);
    }

    [TestMethod]
    public void CreateFullTextCatalog_WithAccentSensitivityOff_Stores()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat with accent_sensitivity = off");
        IsFalse((bool)sim.ExecuteScalar("select is_accent_sensitivity_on from sys.fulltext_catalogs where name = 'mycat'")!);
    }

    [TestMethod]
    public void CreateFullTextCatalog_DuplicateName_Raises7642()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        sim.AssertSqlError("create fulltext catalog mycat", 7642, "A full-text catalog named 'mycat' already exists in this database. Use a different name.");
    }

    [TestMethod]
    public void CreateFullTextCatalog_DemotesPriorDefault()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog cat1 as default");
        _ = sim.ExecuteNonQuery("create fulltext catalog cat2 as default");
        IsFalse((bool)sim.ExecuteScalar("select is_default from sys.fulltext_catalogs where name = 'cat1'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_default from sys.fulltext_catalogs where name = 'cat2'")!);
    }

    [TestMethod]
    public void DropFullTextCatalog_Removes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AwCatalog);
        _ = sim.ExecuteNonQuery("drop fulltext catalog [AW2025FullTextCatalog]");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.fulltext_catalogs"));
    }

    [TestMethod]
    public void DropFullTextCatalog_Missing_Raises7641State4()
    {
        var ex = new Simulation().AssertSqlError("drop fulltext catalog missing_cat", 7641);
        AreEqual(4, ex.State);
    }

    [TestMethod]
    public void CreateFullTextIndex_SingleColumn_Succeeds()
    {
        var sim = BuildSimWithDoc();
        _ = sim.ExecuteNonQuery("create fulltext index on dbo.doc (body language 1033) key index pk_doc on [AW2025FullTextCatalog]");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fulltext_indexes"));
        AreEqual(1033, sim.ExecuteScalar("select language_id from sys.fulltext_index_columns"));
    }

    [TestMethod]
    public void CreateFullTextIndex_PicksDefaultCatalog_WhenNoOnClause()
    {
        var sim = BuildSimWithDoc();
        _ = sim.ExecuteNonQuery("create fulltext index on dbo.doc (body language 1033) key index pk_doc");
        var catName = sim.ExecuteScalar(@"
            select c.name
            from sys.fulltext_indexes i
            join sys.fulltext_catalogs c on c.fulltext_catalog_id = i.fulltext_catalog_id");
        AreEqual("AW2025FullTextCatalog", catName);
    }

    [TestMethod]
    public void CreateFullTextIndex_MultiColumn_WithTypeColumn_Succeeds()
    {
        // AW's [Production].[Document] shape — body column + extension column.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AwCatalog);
        _ = sim.ExecuteNonQuery("""
            create table dbo.doc (
                id int identity(1,1) not null constraint pk_doc primary key,
                file_ext nvarchar(8) null,
                body varbinary(max) null,
                summary nvarchar(max) null
            )
            """);
        _ = sim.ExecuteNonQuery("""
            create fulltext index on dbo.doc (
                summary language 1033,
                body type column file_ext language 1033
            ) key index pk_doc on [AW2025FullTextCatalog]
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.fulltext_index_columns"));
        // The body column (storage ordinal 3) has type_column_id pointing to
        // file_ext (storage ordinal 2).
        AreEqual(2, sim.ExecuteScalar("select type_column_id from sys.fulltext_index_columns where column_id = 3"));
    }

    [TestMethod]
    public void CreateFullTextIndex_OnMissingTable_Raises208()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AwCatalog);
        _ = sim.AssertSqlError(
            "create fulltext index on dbo.missing (body language 1033) key index pk_doc on [AW2025FullTextCatalog]",
            208);
    }

    [TestMethod]
    public void CreateFullTextIndex_OnTableTwice_Raises7652()
    {
        var sim = BuildSimWithDoc();
        _ = sim.ExecuteNonQuery("create fulltext index on dbo.doc (body language 1033) key index pk_doc");
        sim.AssertSqlError(
            "create fulltext index on dbo.doc (body language 1033) key index pk_doc",
            7652,
            "A full-text index for table or indexed view 'dbo.doc' has already been created.");
    }

    [TestMethod]
    public void CreateFullTextIndex_UnknownColumn_Raises1911()
    {
        var sim = BuildSimWithDoc();
        AreEqual((byte)4, sim.AssertSqlError(
            "create fulltext index on dbo.doc (no_such_col language 1033) key index pk_doc",
            1911).State);
    }

    [TestMethod]
    public void CreateFullTextIndex_UnknownKeyIndex_Raises7653State1()
    {
        var sim = BuildSimWithDoc();
        var ex = sim.AssertSqlError(
            "create fulltext index on dbo.doc (body language 1033) key index no_such_index",
            7653);
        AreEqual(1, ex.State);
    }

    [TestMethod]
    public void DropFullTextIndex_RemovesFromCatalog()
    {
        var sim = BuildSimWithDoc();
        _ = sim.ExecuteNonQuery("create fulltext index on dbo.doc (body language 1033) key index pk_doc");
        _ = sim.ExecuteNonQuery("drop fulltext index on dbo.doc");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.fulltext_indexes"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.fulltext_index_columns"));
    }

    [TestMethod]
    public void SysFullTextIndexes_RowShape()
    {
        var sim = BuildSimWithDoc();
        _ = sim.ExecuteNonQuery("create fulltext index on dbo.doc (body language 1033) key index pk_doc on [AW2025FullTextCatalog]");
        IsTrue((bool)sim.ExecuteScalar("select is_enabled from sys.fulltext_indexes")!);
        IsTrue((bool)sim.ExecuteScalar("select has_crawl_completed from sys.fulltext_indexes")!);
        AreEqual("AUTO", sim.ExecuteScalar("select change_tracking_state_desc from sys.fulltext_indexes"));
        AreEqual("FULL_CRAWL", sim.ExecuteScalar("select crawl_type_desc from sys.fulltext_indexes"));
    }

    [TestMethod]
    public void SysFullTextCatalogs_HasDboAsOwner()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AwCatalog);
        // dbo is principal_id=1 (probe-confirmed pre-seed).
        AreEqual(1, sim.ExecuteScalar("select principal_id from sys.fulltext_catalogs where name = 'AW2025FullTextCatalog'"));
    }

    // FULLTEXTSERVICEPROPERTY returns a plain int (probe-confirmed — unlike
    // SERVERPROPERTY's sql_variant). The simulator claims Full-Text is
    // installed, so IsFullTextInstalled reports 1 (the reference box returns 0
    // only because Full-Text isn't installed there); the resource-tuning
    // properties report their installed value of 0.
    [TestMethod]
    public void FullTextServiceProperty_IsFullTextInstalled_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select fulltextserviceproperty('IsFullTextInstalled')"));

    [TestMethod]
    public void FullTextServiceProperty_SurfacesAsInt()
    {
        using var reader = new Simulation().ExecuteReader("select fulltextserviceproperty('IsFullTextInstalled')");
        IsTrue(reader.Read());
        AreEqual("int", reader.GetDataTypeName(0));
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(1, reader.GetValue(0));
    }

    [TestMethod]
    public void FullTextServiceProperty_ResourceProperties_MatchInstalledReference()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select fulltextserviceproperty('ResourceUsage')"));
        AreEqual(0, sim.ExecuteScalar("select fulltextserviceproperty('ConnectTimeout')"));
        AreEqual(0, sim.ExecuteScalar("select fulltextserviceproperty('LoadOSResources')"));
        // Real singles this one out with NULL even when Full-Text is installed.
        AreEqual(DBNull.Value, sim.ExecuteScalar("select fulltextserviceproperty('VerifyResourceUsage')"));
    }

    [TestMethod]
    public void FullTextServiceProperty_CaseInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar("select fulltextserviceproperty('ISFULLTEXTINSTALLED')"));

    [TestMethod]
    public void FullTextServiceProperty_UnknownName_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select fulltextserviceproperty('NotAProperty')"));

    [TestMethod]
    public void FullTextServiceProperty_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select fulltextserviceproperty(cast(null as nvarchar(128)))"));

    [TestMethod]
    public void FullTextServiceProperty_NonConstantName_StillInt()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "declare @p nvarchar(40) = 'IsFullTextInstalled'; select fulltextserviceproperty(@p)"));

    [TestMethod]
    public void FullTextCatalogProperty_EmptyCatalog_PopulationPropertiesAreZero()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        foreach (var property in new[]
        {
            "ItemCount", "IndexSize", "PopulateStatus", "PopulateCompletionAge",
            "MergeStatus", "ImportStatus", "UniqueKeyCount", "LogSize",
        })
        {
            AreEqual(0, sim.ExecuteScalar($"select fulltextcatalogproperty('mycat', '{property}')"), property);
        }
    }

    [TestMethod]
    public void FullTextCatalogProperty_AccentSensitivity_DefaultsOne()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        AreEqual(1, sim.ExecuteScalar("select fulltextcatalogproperty('mycat', 'AccentSensitivity')"));
    }

    [TestMethod]
    public void FullTextCatalogProperty_AccentSensitivity_ReflectsDdlOption()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat with accent_sensitivity = off");
        AreEqual(0, sim.ExecuteScalar("select fulltextcatalogproperty('mycat', 'AccentSensitivity')"));
    }

    [TestMethod]
    public void FullTextCatalogProperty_PropertyNameCaseInsensitive()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        AreEqual(0, sim.ExecuteScalar("select fulltextcatalogproperty('mycat', 'itemcount')"));
    }

    [TestMethod]
    public void FullTextCatalogProperty_UnknownCatalog_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select fulltextcatalogproperty('nosuchcat', 'ItemCount')"));

    [TestMethod]
    public void FullTextCatalogProperty_UnknownProperty_ReturnsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select fulltextcatalogproperty('mycat', 'NotAProperty')"));
    }

    /// <summary>Probed 2026-09-26 against SQL Server 2025.</summary>
    [TestMethod]
    [DataRow("VerifySignature", 1)]
    [DataRow("DataTimeout", 0)]
    public void FullTextServiceProperty_AnswersTheServiceSettings(string property, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select fulltextserviceproperty('{property}')"));

    // ---- CREATE / DROP refusals, probed 2026-10-05 against SQL Server 2025 ----

    private const string KeyTable = """
        create table dbo.k (
            id int not null constraint pk_k primary key,
            n int null,
            i int not null,
            a nvarchar(100) null,
            d varbinary(max) null,
            e nvarchar(10) null,
            c int null
        )
        """;

    [TestMethod]
    [DataRow("create index ix on dbo.k (i)", "ix", 2)]
    [DataRow("create unique index ux on dbo.k (n)", "ux", 3)]
    [DataRow("create unique index ux on dbo.k (i, c)", "ux", 2)]
    [DataRow("create unique index ux on dbo.k (i) where i > 0", "ux", 2)]
    [DataRow("create unique index ux on dbo.k (i); alter index ux on dbo.k disable", "ux", 2)]
    [DataRow("select 1", "nosuch", 1)]
    public void Key_Index_Must_Be_Unique_Single_Column_Not_Null(string setup, string keyIndex, int state)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create fulltext catalog c as default", KeyTable, setup);
        var ex = sim.AssertSqlError($"create fulltext index on dbo.k (a) key index {keyIndex}", 7653);
        AreEqual(state, ex.State);
        StartsWith($"'{keyIndex}' is not a valid index to enforce a full-text search key.", ex.Message);
    }

    [TestMethod]
    [DataRow("select 1", "create fulltext index on dbo.k (a) key index pk_k", 9967)]
    [DataRow("create fulltext catalog c as default", "create fulltext index on dbo.k (a) key index pk_k on nosuch", 7641)]
    [DataRow("create fulltext catalog c as default", "create fulltext index on dbo.k (a)", 102)]
    [DataRow("create fulltext catalog c as default", "create fulltext index on dbo.k (a type column e) key index pk_k", 7699)]
    [DataRow("create fulltext catalog c as default", "create fulltext index on dbo.k (d type column c) key index pk_k", 7671)]
    [DataRow("create fulltext catalog c as default", "create fulltext index on dbo.k (a language 9999) key index pk_k", 7696)]
    [DataRow("create fulltext catalog c as default", "create fulltext index on dbo.k (a) key index pk_k with (change_tracking auto, no population)", 7663)]
    [DataRow("create fulltext catalog c as default; exec('create view dbo.v as select id, a from dbo.k')", "create fulltext index on dbo.v (a) key index pk_k", 9960)]
    public void Create_Index_Refusals(string setup, string statement, int number)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(KeyTable, setup);
        _ = sim.AssertSqlError(statement, number);
    }

    [TestMethod]
    public void Create_Index_Takes_A_Hex_Language_And_No_Column_List()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(KeyTable, "create fulltext catalog c as default", "create fulltext index on dbo.k (a language 0x0407) key index pk_k");
        AreEqual(1031, sim.ExecuteScalar("select language_id from sys.fulltext_index_columns"));
        sim.ExecuteBatches("drop fulltext index on dbo.k", "create fulltext index on dbo.k key index pk_k");
        IsFalse((bool)sim.ExecuteScalar("select is_enabled from sys.fulltext_indexes")!);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fulltext_index_catalog_usages u join sys.fulltext_catalogs c on c.fulltext_catalog_id = u.fulltext_catalog_id where c.name = 'c' and u.index_id = 1"));
    }

    [TestMethod]
    [DataRow("create fulltext catalog c1 authorization nosuch", 9938)]
    [DataRow("create fulltext catalog [cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc]", 193)]
    [DataRow("create fulltext catalog c1 as default with accent_sensitivity = off", 319)]
    public void Create_Catalog_Refusals(string statement, int number)
        => _ = new Simulation().AssertSqlError(statement, number);

    [TestMethod]
    public void Create_Catalog_Takes_In_Path()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog c1 in path '/tmp'");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fulltext_catalogs"));
    }

    [TestMethod]
    public void Drop_Catalog_Holding_An_Index_Is_Msg_7668_And_A_Default_Warns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(KeyTable, "create fulltext catalog c as default", "create fulltext index on dbo.k (a) key index pk_k");
        _ = sim.AssertSqlError("drop fulltext catalog c", 7668);
        sim.ExecuteBatches("drop fulltext index on dbo.k");
        using var connection = sim.CreateOpenConnection();
        List<int> messages = [];
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        using var command = connection.CreateCommand("drop fulltext catalog c");
        _ = command.ExecuteNonQuery();
        AreEqual("7674", string.Join(",", messages));
    }

    [TestMethod]
    [DataRow("create unique index ux on dbo.k (i)", "ux", "drop index ux on dbo.k", 7613, 2)]
    [DataRow("select 1", "pk_k", "alter table dbo.k drop constraint pk_k", 7613, 1)]
    [DataRow("select 1", "pk_k", "alter table dbo.k drop column a", 7614, 1)]
    public void Index_Keyed_Or_Indexed_Columns_Refuse_Their_Drop(string setup, string keyIndex, string statement, int number, int state)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(KeyTable, "create fulltext catalog c as default", setup, $"create fulltext index on dbo.k (a) key index {keyIndex}");
        var ex = sim.AssertSqlError(statement, number);
        AreEqual(state, ex.State);
    }

    [TestMethod]
    public void Dropping_A_Type_Column_Is_Msg_5074_Naming_The_Table()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(KeyTable, "create fulltext catalog c as default", "create fulltext index on dbo.k (d type column e) key index pk_k");
        AreEqual("The object 'k' is dependent on column 'e'.", sim.AssertSqlError("alter table dbo.k drop column e", 5074).Errors[0].Message);
    }

    [TestMethod]
    public void Restricted_Principals_Are_Refused_And_See_Only_Visible_Rows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(KeyTable, "create fulltext catalog c as default", "create fulltext index on dbo.k (a) key index pk_k",
            "create table dbo.x (id int not null constraint pk_x primary key, b nvarchar(10))",
            "create user u without login", "grant select on dbo.k to u", "grant alter on dbo.x to u");
        AreEqual(3, sim.AssertSqlError("execute as user = 'u'; drop fulltext index on dbo.k", 7658).State);
        AreEqual(3, sim.AssertSqlError("execute as user = 'u'; alter fulltext catalog c reorganize", 7641).State);
        AreEqual(7, sim.AssertSqlError("execute as user = 'u'; create fulltext index on dbo.x (b) key index pk_x on c", 7666).State);
        AreEqual("0|1", sim.ExecuteScalar("execute as user = 'u'; select concat((select count(*) from sys.fulltext_catalogs), '|', (select count(*) from sys.fulltext_indexes))"));
    }

    [TestMethod]
    [Description("A tracked full-text index over a legacy LOB column warns that WRITETEXT / UPDATETEXT escape tracking, naming the table as written (Msg 7657).")]
    public void TrackedIndexOverLegacyLob_Warns7657()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.lob (id int not null constraint pk_lob primary key, body text)", "create fulltext catalog c as default");
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand("create fulltext index on dbo.lob (body) key index pk_lob").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "7657:Warning: Table or indexed view 'dbo.lob' has full-text indexed columns that are of type image, text, or ntext. Full-text change tracking cannot track WRITETEXT or UPDATETEXT operations performed on these columns." }, messages);
    }
}
