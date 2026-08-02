using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the parse-and-store-but-no-search full-text surface
/// (<c>CREATE/DROP FULLTEXT CATALOG</c> + <c>CREATE/DROP FULLTEXT INDEX</c>
/// + <c>sys.fulltext_catalogs</c> / <c>sys.fulltext_indexes</c> /
/// <c>sys.fulltext_index_columns</c>). The simulator stores full-text
/// metadata for AW model.xml round-trip but does not execute text search;
/// CONTAINS / FREETEXT / CONTAINSTABLE / FREETEXTTABLE raise
/// <see cref="NotSupportedException"/>.
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
    public void CreateFullTextCatalog_DuplicateName_Raises2714()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create fulltext catalog mycat");
        _ = sim.AssertSqlError("create fulltext catalog mycat", 2714);
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
    public void DropFullTextCatalog_Missing_Raises208()
        => new Simulation().AssertSqlError("drop fulltext catalog missing_cat", 208);

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
    public void CreateFullTextIndex_OnTableTwice_Raises2714()
    {
        var sim = BuildSimWithDoc();
        _ = sim.ExecuteNonQuery("create fulltext index on dbo.doc (body language 1033) key index pk_doc");
        _ = sim.AssertSqlError(
            "create fulltext index on dbo.doc (body language 1033) key index pk_doc",
            2714);
    }

    [TestMethod]
    public void CreateFullTextIndex_UnknownColumn_Raises207()
    {
        var sim = BuildSimWithDoc();
        _ = sim.AssertSqlError(
            "create fulltext index on dbo.doc (no_such_col language 1033) key index pk_doc",
            207);
    }

    [TestMethod]
    public void CreateFullTextIndex_UnknownKeyIndex_Raises208()
    {
        var sim = BuildSimWithDoc();
        _ = sim.AssertSqlError(
            "create fulltext index on dbo.doc (body language 1033) key index no_such_index",
            208);
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
        AreEqual("FULL", sim.ExecuteScalar("select crawl_type_desc from sys.fulltext_indexes"));
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
}
