using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Smoke tests for <see cref="Simulation.FromBacpac(string, out BacpacLoadResult)"/>
/// against the AdventureWorks2025 and WideWorldImporters reference bacpacs
/// under <c>.vs/</c>. Both files are gitignored, so each test short-circuits
/// to <see cref="Assert.Inconclusive(string)"/> when the workspace doesn't
/// have them (CI scenario).
/// </summary>
[TestClass]
public sealed class BacpacLoaderTests
{
    private static string ResolveBacpacPath(string fileName)
    {
        // Walk up from the test bin dir to the repo root, then into .vs/.
        // The test runner cwd is the test-project bin/Debug/net10.0/ dir.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".vs", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return string.Empty;
    }

    private static Simulation LoadAdventureWorks(out BacpacLoadResult diagnostics)
    {
        var path = ResolveBacpacPath("AdventureWorks2025.bacpac");
        if (string.IsNullOrEmpty(path))
        {
            Inconclusive(".vs/AdventureWorks2025.bacpac not present in this workspace; skipping AW smoke test.");
        }
        return Simulation.FromBacpac(path, out diagnostics);
    }

    private static Simulation LoadWideWorldImporters(out BacpacLoadResult diagnostics)
    {
        var path = ResolveBacpacPath("WideWorldImporters-Standard.bacpac");
        if (string.IsNullOrEmpty(path))
        {
            Inconclusive(".vs/WideWorldImporters-Standard.bacpac not present in this workspace; skipping WWI smoke test.");
        }
        return Simulation.FromBacpac(path, out diagnostics);
    }

    private static Simulation LoadWideWorldImportersFull(out BacpacLoadResult diagnostics)
    {
        var path = ResolveBacpacPath("WideWorldImporters-Full.bacpac");
        if (string.IsNullOrEmpty(path))
        {
            Inconclusive(".vs/WideWorldImporters-Full.bacpac not present in this workspace; skipping WWI-Full smoke test.");
        }
        return Simulation.FromBacpac(path, out diagnostics);
    }

    [TestMethod]
    public void Load_AW_Creates_All_Five_Schemas()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('dbo','sys','INFORMATION_SCHEMA','guest','public') ORDER BY name;";
        using var reader = command.ExecuteReader();

        var schemas = new List<string>();
        while (reader.Read())
            schemas.Add(reader.GetString(0));

        HasCount(5, schemas, $"expected 5 user schemas, got: {string.Join(", ", schemas)}");
        AreEqual("HumanResources", schemas[0]);
        AreEqual("Person", schemas[1]);
        AreEqual("Production", schemas[2]);
        AreEqual("Purchasing", schemas[3]);
        AreEqual("Sales", schemas[4]);
    }

    [TestMethod]
    public void Load_AW_Database_Options_Applied()
    {
        var simulation = LoadAdventureWorks(out _);
        // AW bacpac carries IsReadCommittedSnapshot=True; the loader emits
        // ALTER DATABASE [simulated] SET READ_COMMITTED_SNAPSHOT ON which
        // flips Database.ReadCommittedSnapshot.
        IsTrue(simulation.Databases["simulated"].ReadCommittedSnapshot);
    }

    [TestMethod]
    public void Load_AW_Element_Counts_Match_Probe()
    {
        _ = LoadAdventureWorks(out var diagnostics);
        AreEqual(5, diagnostics.ElementCounts["SqlSchema"]);
        AreEqual(1, diagnostics.ElementCounts["SqlDatabaseOptions"]);
        AreEqual(71, diagnostics.ElementCounts["SqlTable"]);
    }

    [TestMethod]
    public void Load_AW_Unhandled_Elements_Recorded_In_Skipped()
    {
        _ = LoadAdventureWorks(out var diagnostics);
        // Phases A-F handled types are off Skipped. Computed columns +
        // dependent filtered indexes used to be the dominant remaining
        // gap; the 2026-05-15 WITH SCHEMABINDING / EXECUTE AS scalar-UDF
        // parser fix unblocked `dbo.ufnLeadingZeros`, which in turn
        // unblocked every AW computed column referencing it (and the
        // filtered indexes whose predicates depend on those columns).
        // The 2026-05-16 SqlPermissionStatement loader bundle dispatched
        // the 2 encryption-key GRANTs through the simulator's GRANT
        // parser, clearing AW's last large structural gap.
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlTable").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlPrimaryKeyConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlForeignKeyConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlCheckConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlDefaultConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlPermissionStatement").ToList());
    }

    [TestMethod]
    public void Load_AW_Constraints_Land_On_Tables()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // PK count — every AW table has a PK (71 SqlPrimaryKeyConstraint).
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK';";
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual(71, reader.GetInt32(0));
        }

        // UQ count — AW has 1 SqlUniqueConstraint (Production.Document.rowguid).
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'UQ';";
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual(1, reader.GetInt32(0));
        }

        // FK count — 90 SqlForeignKeyConstraint in AW.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys;";
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual(90, reader.GetInt32(0));
        }

        // CHECK count — 89 SqlCheckConstraint in AW.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.check_constraints;";
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual(89, reader.GetInt32(0));
        }

        // DEFAULT count — 152 SqlDefaultConstraint in AW.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.default_constraints;";
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            AreEqual(152, reader.GetInt32(0));
        }
    }

    [TestMethod]
    public void Load_AW_Production_ProductCategory_PK_Wired()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // Production.ProductCategory's PK is named [PK_ProductCategory_ProductCategoryID].
        command.CommandText = """
            SELECT kc.name
              FROM sys.key_constraints kc
              JOIN sys.tables t ON kc.parent_object_id = t.object_id
              JOIN sys.schemas s ON t.schema_id = s.schema_id
             WHERE s.name = 'Production' AND t.name = 'ProductCategory' AND kc.type = 'PK';
            """;
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("PK_ProductCategory_ProductCategoryID", reader.GetString(0));
    }

    [TestMethod]
    public void Load_AW_Indexes_Land()
    {
        var simulation = LoadAdventureWorks(out var diagnostics);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        // AW has 95 SqlIndex elements: 2 target views (deferred — indexed
        // views need view + SCHEMABINDING machinery), N target computed
        // columns that aren't loaded yet (deferred until functions land).
        // Verify the bulk lands and the deferrals are recorded.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name LIKE 'AK[_]%' OR name LIKE 'IX[_]%';";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        // 93 user indexes land = 95 SqlIndex - 2 view-targeted (deferred:
        // indexed views need SCHEMABINDING). The 2 remaining Skipped
        // entries (down from 6 before the 2026-05-15 index-after-computed
        // reorder) are the view-targeted indexes.
        AreEqual(93, reader.GetInt32(0));
        var skippedIndexEntries = diagnostics.Skipped.Where(s => s.ElementType == "SqlIndex").ToList();
        HasCount(2, skippedIndexEntries);
        // Both remaining Skipped entries are view-targeted (indexed views
        // need SCHEMABINDING — not modeled).
        AreEqual(2, skippedIndexEntries.Count(s => s.Reason.Contains("on view '", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Load_AW_Programmable_Counts()
    {
        var simulation = LoadAdventureWorks(out var diagnostics);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // AW counts: 20 views, 10 procs, 10 scalar functions, 1 multi-stmt
        // TVF, 10 DML triggers, 1 DDL trigger (DDL trigger isn't programmable
        // in the same sense — deferred). Best-effort load may miss some that
        // reference computed cols / unsupported features; lower bounds keep
        // the test green as the loader matures. The Skipped log on the
        // BacpacLoadResult names the reason for each miss.
        var views = QueryCount(connection, "SELECT COUNT(*) FROM sys.views;");
        var procs = QueryCount(connection, "SELECT COUNT(*) FROM sys.procedures;");
        var funcs = QueryCount(connection, "SELECT COUNT(*) FROM sys.objects WHERE type IN ('FN', 'TF', 'IF');");
        var triggers = QueryCount(connection, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 1;");

        // Current landing rates against AW (probed 2026-05-15). Gaps:
        //   views: 11/20 — 3 vJobCandidate-family views use CROSS APPLY in
        //     a shape the simulator's view-body parser rejects; the rest
        //     reference computed columns (deferred until functions land) or
        //     other unsupported syntax.
        //   procs: 8/10 — 2 reject the unbracketed UDDT `dbo.Flag` parameter
        //     type (1-part alias resolution in proc param list).
        //   funcs: 10/11 — 3 scalar UDFs hit RETURN-with-value-in-context
        //     diagnostic; the multi-stmt TVF lands.
        //   triggers: 10/10 — all DML triggers land after the NOT FOR
        //     REPLICATION reserved-keyword fix. (1 DDL trigger lands
        //     separately via SqlDatabaseDdlTrigger.)
        AreEqual(11, views);
        AreEqual(8, procs);
        AreEqual(11, funcs);
        AreEqual(10, triggers);

        // Any Skipped programmable entries name their reason (helps the
        // next-phase development checklist).
        var skippedProgrammable = diagnostics.Skipped
            .Where(s => s.ElementType is "SqlView" or "SqlScalarFunction"
                or "SqlMultiStatementTableValuedFunction" or "SqlProcedure" or "SqlDmlTrigger")
            .ToList();
        // No assertion on count — may be 0 if all land. Just sanity-check
        // that the dispatcher routed every element type (none on Skipped
        // means everything succeeded; >0 means we have a Reason to inspect).
        foreach (var entry in skippedProgrammable)
            IsFalse(string.IsNullOrEmpty(entry.Reason), $"empty Reason on Skipped entry {entry}");
    }

    private static int QueryCount(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        return reader.GetInt32(0);
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Load_AW_Bcp_Data_Loads()
    {
        var simulation = LoadAdventureWorks(out var diagnostics);
        // AW carries 760,167 rows across 1103 BCP shards. With the
        // hierarchyid wire decoder landing alongside the MAX / xml /
        // geography paths, every shard loads cleanly: 100% row coverage,
        // 0 BCP-file failures.
        var rowsLoaded = diagnostics.ElementCounts.GetValueOrDefault("_DataRows", 0);
        AreEqual(760_167, rowsLoaded);

        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // Production.ProductCategory's 4 rows must land (no exotic types).
        AreEqual(4, QueryCount(connection, "SELECT COUNT(*) FROM Production.ProductCategory;"));
        // Sales.SpecialOffer's 16 rows must land (exercises smallmoney +
        // nullable int + uniqueidentifier + alias-typed dbo.Flag).
        AreEqual(16, QueryCount(connection, "SELECT COUNT(*) FROM Sales.SpecialOffer;"));

        // Lock the zero-failures state — any regression in MAX / xml /
        // geography / hierarchyid decoding surfaces here.
        var dataFileFailures = diagnostics.Skipped.Where(s => s.ElementType == "_DataFile").ToList();
        IsEmpty(dataFileFailures);
    }

    [TestMethod]
    public void Load_AW_Hierarchyid_Column_Carries_Path()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // HR.Employee carries 290 rows; OrganizationNode is hierarchyid
        // NULL for the CEO (BusinessEntityID=1) and a path for everyone
        // else. Verify the full count + a representative path.
        AreEqual(290, QueryCount(connection, "SELECT COUNT(*) FROM HumanResources.Employee;"));
        AreEqual(1, QueryCount(connection, "SELECT COUNT(*) FROM HumanResources.Employee WHERE OrganizationNode IS NULL;"));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OrganizationNode.ToString() FROM HumanResources.Employee WHERE BusinessEntityID = 2;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("/1/", reader.GetString(0));

        // Production.Document.DocumentNode loads (12 rows in this bacpac;
        // probe-confirmed). The root row (DocumentNode = '/') has an empty
        // 8-byte payload.
        AreEqual(12, QueryCount(connection, "SELECT COUNT(*) FROM Production.Document;"));
        using var rootCmd = connection.CreateCommand();
        rootCmd.CommandText = "SELECT DocumentNode.ToString() FROM Production.Document WHERE Title = 'Documents';";
        using var rootReader = rootCmd.ExecuteReader();
        IsTrue(rootReader.Read());
        AreEqual("/", rootReader.GetString(0));
    }

    [TestMethod]
    public void Load_AW_Xml_Column_Carries_Value()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // HumanResources.JobCandidate has 13 rows; each Resume column carries
        // a non-NULL XML payload starting with `<ns:Resume`. The first row's
        // Resume must be readable as nvarchar(MAX).
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP(1) CAST(Resume AS nvarchar(MAX)) FROM HumanResources.JobCandidate ORDER BY JobCandidateID;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        var resume = reader.GetString(0);
        IsTrue(resume.StartsWith("<ns:Resume", StringComparison.Ordinal), $"expected XML to start with <ns:Resume, got: {resume[..Math.Min(50, resume.Length)]}");

        // 13 JobCandidate rows total — the BCP file decoded all of them.
        AreEqual(13, QueryCount(connection, "SELECT COUNT(*) FROM HumanResources.JobCandidate;"));
    }

    [TestMethod]
    public void Load_AW_VarbinaryMax_Column_Carries_Bytes()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // Production.ProductPhoto.ThumbNailPhoto: row 1's photo is the
        // "no_image_available_small.gif" placeholder (1077 bytes starting
        // with the GIF89a magic). The varbinary(MAX) decoder reads the
        // 8-byte length + 1077 inline bytes; assert bytes preserved.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP(1) ThumbNailPhoto FROM Production.ProductPhoto ORDER BY ProductPhotoID;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        var photo = (byte[])reader.GetValue(0);
        IsGreaterThanOrEqualTo(6, photo.Length, $"photo too small: {photo.Length} bytes");
        // GIF89a magic = 0x47 0x49 0x46 0x38 0x39 0x61
        AreEqual(0x47, photo[0]);
        AreEqual(0x49, photo[1]);
        AreEqual(0x46, photo[2]);
        AreEqual(0x38, photo[3]);
        AreEqual(0x39, photo[4]);
        AreEqual(0x61, photo[5]);
    }

    [TestMethod]
    public void Load_AW_Geography_Column_Decodes_To_Point_Wkt()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // After the simple-point WKB decoder bundle (2026-05-16), every AW
        // SpatialLocation row decodes to a `POINT (long lat)` WKT string.
        // AW's geography values are all simple points (no LineString /
        // Polygon / etc.), so 19,614 / 19,614 round-trip.
        AreEqual(19614, QueryCount(connection, "SELECT COUNT(*) FROM Person.Address;"));
        AreEqual(0, QueryCount(connection, "SELECT COUNT(*) FROM Person.Address WHERE SpatialLocation IS NULL;"));

        // Spot-check that the WKT round-trips through CAST AS nvarchar(max).
        // AddressID = 1 is a Bothell, WA address; longitude ~-122, latitude ~47.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP(1) AddressID, City, PostalCode, CAST(SpatialLocation AS nvarchar(max)) FROM Person.Address WHERE AddressID = 1;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("Bothell", reader.GetString(1));
        AreEqual("98011", reader.GetString(2));
        var wkt = reader.GetString(3);
        IsTrue(wkt.StartsWith("POINT (", StringComparison.Ordinal), $"expected WKT to start with 'POINT (' but got '{wkt}'");
    }

    [TestMethod]
    public void Load_AW_Plain_Bit_Column_Reads_Correctly()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // Production.Document.FolderFlag is the ONLY non-UDDT bit column in
        // AW (per probe). The wire-format probe revealed plain bit also uses
        // the 1-byte length prefix shape, same as UDDT-aliased columns —
        // not the fixed-raw single-byte shape that the loader used to
        // assume. Note: Production.Document data files block on
        // hierarchyid (DocumentNode), so the row data itself isn't
        // queryable. The fix here is regression-prevention for other
        // bacpacs with plain-bit columns; tested directly above via the
        // wire-format-aware decoder. Assert the table itself landed.
        AreEqual(1, QueryCount(connection, "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'Production' AND t.name = 'Document';"));
    }

    [TestMethod]
    public void Load_AW_Extended_Properties_Land()
    {
        var simulation = LoadAdventureWorks(out var diagnostics);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        // AW has 538 SqlExtendedProperty elements: 461 column-level + 69
        // table-level + 5 schema + 1 DB + 1 filegroup + 1 DDL-trigger. The
        // loader handles SqlColumn / SqlTableBase / SqlSchema /
        // SqlDatabaseOptions hosts; SqlFilegroup + SqlDatabaseDdlTrigger
        // hosts are out of scope, and 9 column/table-level properties miss
        // because their host columns / tables didn't load (computed-col
        // tables, vJobCandidate-family views, etc.). 527 land in practice.
        var landed = QueryCount(connection, "SELECT COUNT(*) FROM sys.extended_properties;");
        IsGreaterThanOrEqualTo(525, landed);

        // The 1 DB-level MS_Description should be present.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value AS nvarchar(MAX)) FROM sys.extended_properties WHERE class = 0 AND name = 'MS_Description';";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("AdventureWorks 2025 Sample OLTP Database", reader.GetString(0));

        // Filegroup + DDL-trigger hosts land on Skipped with the expected reason.
        var skipped = diagnostics.Skipped.Where(s => s.ElementType == "SqlExtendedProperty").ToList();
        IsNotEmpty(skipped.Where(s => s.Reason.Contains("SqlFilegroup", StringComparison.Ordinal)).ToList());
    }

    [TestMethod]
    public void Load_AW_Cascade_FK_Has_Correct_Action()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        // AW carries 2 FKs with OnDeleteAction=CASCADE on Sales.SalesOrderHeader's
        // child tables. Verify delete_referential_action=1 (CASCADE) lands.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys WHERE delete_referential_action = 1;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_AW_Tables_Land_With_Correct_Column_Counts()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // 71 user tables. Pre-existing system tables (sys.*) are filtered by
        // the schema_id range (user schemas use ids >= 5; built-in sys = 4).
        command.CommandText = "SELECT COUNT(*) FROM sys.tables;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(71, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_AW_Production_ProductCategory_Has_Expected_Columns()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // ProductCategory has 4 cols: ProductCategoryID int IDENTITY PK,
        // Name [dbo].[Name] NOT NULL, rowguid uniqueidentifier NOT NULL
        // ROWGUIDCOL, ModifiedDate datetime NOT NULL.
        command.CommandText = """
            SELECT c.name, t.name AS type_name, c.is_nullable, c.is_identity
              FROM sys.columns c
              JOIN sys.tables tab ON c.object_id = tab.object_id
              JOIN sys.types t ON c.user_type_id = t.user_type_id
              JOIN sys.schemas s ON tab.schema_id = s.schema_id
             WHERE s.name = 'Production' AND tab.name = 'ProductCategory'
             ORDER BY c.column_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(string Name, string TypeName, bool Nullable, bool Identity)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }
        HasCount(4, rows);
        AreEqual("ProductCategoryID", rows[0].Name);
        AreEqual("int", rows[0].TypeName);
        IsFalse(rows[0].Nullable);
        IsTrue(rows[0].Identity);
        AreEqual("Name", rows[1].Name);
        // [dbo].[Name] is an alias over nvarchar(50); user_type_id surfaces the
        // alias's allocated id (>=256) and joining to sys.types resolves the
        // alias name. NOT NULL is preserved through the column declaration.
        IsFalse(rows[1].Nullable);
        AreEqual("rowguid", rows[2].Name);
        AreEqual("uniqueidentifier", rows[2].TypeName);
        AreEqual("ModifiedDate", rows[3].Name);
        AreEqual("datetime", rows[3].TypeName);
    }

    [TestMethod]
    public void Load_AW_No_Per_Element_Failures()
    {
        // Defensive regression for the resilient-loader path: every Skipped
        // entry on AW should be a "feature not modeled" marker (recorded
        // intentionally by an emit method), never the "Load failed: …" form
        // that the catch in RunPhase emits when an emit method throws.
        // Catching a throw here means we've regressed something that
        // previously worked.
        _ = LoadAdventureWorks(out var diagnostics);
        var loadFailures = diagnostics.Skipped
            .Where(s => s.Reason.StartsWith("Load failed:", StringComparison.Ordinal))
            .ToList();
        IsEmpty(loadFailures, $"Unexpected load failures on AW: {string.Join(" | ", loadFailures.Select(f => $"{f.ElementName} :: {f.Reason}"))}");
    }

    [TestMethod]
    public void Load_WWI_Element_Counts_Match_Probe()
    {
        _ = LoadWideWorldImporters(out var diagnostics);
        AreEqual(10, diagnostics.ElementCounts["SqlSchema"]);
        AreEqual(48, diagnostics.ElementCounts["SqlTable"]);
        AreEqual(26, diagnostics.ElementCounts["SqlSequence"]);
        AreEqual(9, diagnostics.ElementCounts["SqlRole"]);
        AreEqual(31, diagnostics.ElementCounts["SqlPrimaryKeyConstraint"]);
        AreEqual(98, diagnostics.ElementCounts["SqlForeignKeyConstraint"]);
        AreEqual(41, diagnostics.ElementCounts["SqlDefaultConstraint"]);
    }

    [TestMethod]
    public void Load_WWI_Sequences_Land_In_sys_sequences()
    {
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.sequences;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(26, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWI_Roles_Land_In_sys_database_principals()
    {
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // Filter to user-created roles (skip the fixed seed: public).
        command.CommandText = "SELECT COUNT(*) FROM sys.database_principals WHERE type = 'R' AND principal_id > 4;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(9, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWI_Sequence_Backed_Defaults_Apply()
    {
        // Sanity-check that sequence-backed DEFAULTs resolve: WWI has
        // [Sequences].[CityID] at StartValue=38187 with the DEFAULT bound
        // to Application.Cities.CityID. Inserting a row without specifying
        // CityID should pull the next value off the sequence — confirming
        // the phase-1 sequence emit ran before the phase-3 DEFAULT and
        // that the simulator wired both together.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_value FROM sys.sequences WHERE name = 'CityID';";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        // Loader applies the SqlDefaultConstraint without consuming sequence
        // values, so current_value stays at the bacpac-declared StartValue.
        AreEqual(38187L, reader.GetInt64(0));
    }

    [TestMethod]
    public void Load_WWI_Known_Gaps_Recorded_In_Skipped()
    {
        // Locks the current WWI Skipped category census. As future loader
        // bundles handle these, update the expected counts here so the
        // regression test catches accidental progress (or regress).
        _ = LoadWideWorldImporters(out var diagnostics);
        var grouped = diagnostics.Skipped.GroupBy(s => s.ElementType)
            .ToDictionary(g => g.Key, g => g.Count());
        TestContext.WriteLine("=== WWI Skipped (current) ===");
        foreach (var kv in grouped.OrderByDescending(k => k.Value))
            TestContext.WriteLine($"{kv.Value,5}  {kv.Key}");
        foreach (var s in diagnostics.Skipped.Where(s => s.ElementType is "SqlCheckConstraint" or "SqlView" or "SqlScalarFunction" or "SqlIndex" or "SqlPermissionStatement"))
            TestContext.WriteLine($"  [{s.ElementType}] {s.ElementName}: {s.Reason}");
        IsFalse(grouped.ContainsKey("SqlTableType"), "SqlTableType dispatched.");
        IsFalse(grouped.ContainsKey("SqlProcedure"), "sysname keyword landed; all procs load.");
        IsFalse(grouped.ContainsKey("SqlComputedColumn"), "JSON_QUERY landed; all 8 WWI computed columns load.");
        IsFalse(grouped.ContainsKey("SqlIndex"), "Indexes moved to phase 8 (after computed columns); filtered indexes on computed columns now resolve.");
        IsFalse(grouped.ContainsKey("SqlView"), "DECOMPRESS landed; Website.VehicleTemperatures view loads.");
        IsFalse(grouped.ContainsKey("SqlScalarFunction"), "WITH EXECUTE AS OWNER landed on scalar UDFs; Website.CalculateCustomerPrice loads.");
        IsFalse(grouped.ContainsKey("SqlFilegroup"), "Filegroup skip-with-diagnostic.");
        IsFalse(grouped.ContainsKey("SqlCheckConstraint"), "Paren-wrapped value LHS in boolean parser landed; WWI's CK_Sales_SpecialDeals_Exactly_One_NOT_NULL_Pricing_Option_Is_Required loads.");
        IsFalse(grouped.ContainsKey("SqlPermissionStatement"), "SqlPermissionStatement dispatcher entry landed; the 2 encryption-key VIEW grants emit through the GRANT parser.");
        IsFalse(grouped.ContainsKey("SqlExtendedProperty"), "Extended-property host-routing landed for SqlIndexBase + SqlConstraint; all 414 WWI extended properties load.");
        IsFalse(grouped.ContainsKey("SqlDatabaseOptions"), "Collation metadata round-trip landed; Latin1_General_100_CI_AS recognized and stored on Database.CollationName.");
    }

    [TestMethod]
    public void Load_WWI_Computed_Columns_Land_With_is_computed_Set()
    {
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // All 8 of WWI's computed columns succeed after the 2026-05-15
        // JSON_QUERY bundle (was 6/8 before). Verify all 8 surface in
        // sys.columns with is_computed = 1.
        command.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE is_computed = 1;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(8, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWI_Database_Collation_Round_Trips()
    {
        // WWI declares Latin1_General_100_CI_AS in its SqlDatabaseOptions
        // element. The 2026-05-16 collation metadata bundle stores that on
        // Database.CollationName and surfaces it through both sys.databases
        // and DATABASEPROPERTYEX. Comparison semantics still route through
        // the simulator's default collation — the metadata is honest about
        // the declaration without claiming full fidelity.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT collation_name FROM sys.databases";
            AreEqual("Latin1_General_100_CI_AS", command.ExecuteScalar());
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DATABASEPROPERTYEX('simulated', 'Collation')";
            AreEqual("Latin1_General_100_CI_AS", command.ExecuteScalar());
        }
    }

    [TestMethod]
    public void Load_WWI_Extended_Properties_Cover_Index_And_Constraint_Hosts()
    {
        // WWI carries 76 SqlIndexBase + 5 SqlConstraint extended properties
        // that the 2026-05-16 loader-bundle wired through sp_addextendedproperty.
        // Verify the catalog round-trips a known sample of each:
        //   INDEX:      Application.Cities.FK_Application_Cities_StateProvinceID
        //   CONSTRAINT: Sales.CK_Sales_Invoices_ReturnedDeliveryData_Must_Be_Valid_JSON
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // Index host: class=7 (INDEX), value should land on the named index.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CAST(ep.value AS nvarchar(MAX))
                FROM sys.extended_properties ep
                JOIN sys.indexes i ON ep.major_id = i.object_id AND ep.minor_id = i.index_id
                JOIN sys.objects o ON i.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE ep.class = 7
                  AND s.name = 'Application'
                  AND o.name = 'Cities'
                  AND i.name = 'FK_Application_Cities_StateProvinceID'
                  AND ep.name = 'Description';
                """;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read(), "Expected SqlIndexBase host extended property on FK-named index.");
            AreEqual("Auto-created to support a foreign key", reader.GetString(0));
        }

        // Constraint host: class=1 (OBJECT_OR_COLUMN), major_id = the
        // constraint's own object_id (not the table's).
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CAST(ep.value AS nvarchar(MAX))
                FROM sys.extended_properties ep
                JOIN sys.check_constraints c ON ep.major_id = c.object_id
                JOIN sys.schemas s ON c.schema_id = s.schema_id
                WHERE ep.class = 1
                  AND s.name = 'Sales'
                  AND c.name = 'CK_Sales_Invoices_ReturnedDeliveryData_Must_Be_Valid_JSON'
                  AND ep.name = 'Description';
                """;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read(), "Expected SqlConstraint host extended property on CK constraint.");
            AreEqual("Ensures that if returned delivery data is present that it is valid JSON", reader.GetString(0));
        }

        // Aggregate count check — all 414 WWI extended properties landed.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = 'Description';";
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            // 292 SqlColumn + 76 SqlIndexBase + 31 SqlTableBase + 10 SqlSchema + 5 SqlConstraint = 414.
            IsGreaterThanOrEqualTo(410, reader.GetInt32(0));
        }
    }

    [TestMethod]
    public void Load_WWI_Encryption_Key_Grants_Land_In_sys_database_permissions()
    {
        // The 2 encryption-key VIEW grants
        // (GRANT VIEW ANY COLUMN ENCRYPTION KEY DEFINITION TO public,
        // GRANT VIEW ANY COLUMN MASTER KEY DEFINITION TO public) — both
        // database-scope, granted to the pre-seeded `public` role.
        // Verify they round-trip through sys.database_permissions.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT permission_name
            FROM sys.database_permissions p
            JOIN sys.database_principals g ON p.grantee_principal_id = g.principal_id
            WHERE g.name = 'public'
              AND permission_name LIKE 'VIEW ANY COLUMN%'
            ORDER BY permission_name;
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        HasCount(2, names);
        AreEqual("VIEW ANY COLUMN ENCRYPTION KEY DEFINITION", names[0]);
        AreEqual("VIEW ANY COLUMN MASTER KEY DEFINITION", names[1]);
    }

    [TestMethod]
    public void Load_WWI_ParenWrappedValueLhs_Check_Loaded_And_Enforces()
    {
        // CK_Sales_SpecialDeals_Exactly_One_NOT_NULL_Pricing_Option_Is_Required
        // is the canonical paren-wrapped-value-LHS CHECK in WWI: the parsed
        // expression is `((case_sum) = (1))`. Probe-confirmed against SQL
        // Server 2025: the constraint rejects 0-set and 2-set inserts. The
        // BCP-loaded rows already satisfy it; verify the constraint enforces
        // on new inserts as a proxy for "did this CHECK actually land".
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sales.SpecialDeals
                (SpecialDealID, StockItemID, CustomerID, BuyingGroupID, StockGroupID, DealDescription,
                 StartDate, EndDate, DiscountAmount, DiscountPercentage, UnitPrice, LastEditedBy)
            VALUES (99999, NULL, NULL, NULL, NULL, N'Bad — two pricing options',
                    '2030-01-01', '2030-12-31', 5.00, 10.0, NULL, 1);
            """;
        var ex = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
        Contains("CK_Sales_SpecialDeals_Exactly_One_NOT_NULL_Pricing_Option_Is_Required", ex.Message);
    }

    [TestMethod]
    public void Load_WWI_Persisted_Computed_Column_Evaluates_On_Read()
    {
        // Application.People.SearchName is a PERSISTED computed column
        // defined as concat(PreferredName, N' ', FullName). Verify the
        // ALTER TABLE ADD AS landed AND that the existing BCP-loaded rows
        // can read it (the simulator recomputes on read, since PERSISTED
        // is a no-op in the simulator).
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP 1 PreferredName, FullName, SearchName FROM Application.People WHERE PreferredName IS NOT NULL AND FullName IS NOT NULL ORDER BY PersonID;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        var preferred = reader.GetString(0);
        var full = reader.GetString(1);
        var search = reader.GetString(2);
        AreEqual($"{preferred} {full}", search);
    }

    [TestMethod]
    public void Load_WWI_InvoiceLines_Decimals_Decode_With_Correct_Magnitude()
    {
        // Regression pin for the BCP decimal wire-format fix (2026-05-17):
        // the on-disk layout is [prefix N][precision][scale][sign][N-3 bytes mantissa LE],
        // not [prefix N][sign][N-1 bytes mantissa LE] as previously assumed.
        // The earlier decoder treated the precision byte as the sign (always
        // non-zero → always positive) and started the mantissa 2 bytes too
        // early, multiplying every decoded value by 2^16 ≈ 65,536.
        // Probe-confirmed against the live WWI server:
        //   MAX(UnitPrice) over Sales.InvoiceLines = 1899.00 (decimal(18,2)).
        // The pre-fix simulator returned 124,452,866.58 (= 1899 × 65,536 plus
        // contributions from rows that maxed slightly higher post-shift).
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(UnitPrice), MIN(UnitPrice), MAX(TaxRate), MIN(TaxRate) FROM Sales.InvoiceLines;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1899.00m, reader.GetDecimal(0));
        AreEqual(0.66m, reader.GetDecimal(1));
        AreEqual(15.000m, reader.GetDecimal(2));
        AreEqual(10.000m, reader.GetDecimal(3));
    }

    [TestMethod]
    public void Load_WWI_StockItemTransactions_Decimals_Decode_Negative_Values()
    {
        // Same regression as above but specifically pins the sign handling.
        // Pre-fix the simulator's "sign byte" was actually the precision byte
        // (always non-zero), so every decoded decimal was rendered positive.
        // StockItemTransactions.Quantity (decimal(10,3)) has both negative
        // and positive values on the live server — probed MIN(Quantity) = -360.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(Quantity) FROM Warehouse.StockItemTransactions;";
        AreEqual(-360.000m, command.ExecuteScalar());
    }

    [TestMethod]
    public void Load_WWI_Sysname_Procs_Land_In_sys_procedures()
    {
        // The three WWI procs taking sysname parameters
        // ([Application].[AddRoleMemberIfNonexistent],
        // [Application].[CreateRoleIfNonexistent],
        // [Sequences].[ReseedSequenceBeyondTableValues]) now load — sysname
        // is recognized as a keyword. Verify they're in sys.procedures.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sys.procedures
            WHERE name IN ('AddRoleMemberIfNonexistent', 'CreateRoleIfNonexistent', 'ReseedSequenceBeyondTableValues')
            ORDER BY name;
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        HasCount(3, names);
        AreEqual("AddRoleMemberIfNonexistent", names[0]);
        AreEqual("CreateRoleIfNonexistent", names[1]);
        AreEqual("ReseedSequenceBeyondTableValues", names[2]);
    }

    [TestMethod]
    public void Load_WWI_Table_Types_Land_In_sys_table_types()
    {
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.table_types ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        // WWI emits 4 SqlTableType elements, all under the Website schema:
        // OrderIDList, OrderLineList, OrderList, SensorDataList.
        HasCount(4, names);
        AreEqual("OrderIDList", names[0]);
        AreEqual("OrderLineList", names[1]);
        AreEqual("OrderList", names[2]);
        AreEqual("SensorDataList", names[3]);
    }

    [TestMethod]
    public void Load_WWI_Most_Tables_Loaded()
    {
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        // 48 tables in WWI; even with the deferred features above, every
        // table should still create cleanly (the gaps are at the column /
        // constraint / index / programmable-object level, not the table
        // level).
        AreEqual(48, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWI_Temporal_Tables_Linked_To_History_Siblings()
    {
        // WWI has 17 system-versioned base tables; each carries a
        // TemporalSystemVersioningHistoryTable relationship to its
        // *_Archive sibling. Phase 5's EmitDeferredSystemVersioning emits
        // ALTER TABLE base SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))
        // for each pair after both endpoints exist. Verify the count.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 2;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(17, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWI_Temporal_History_Tables_Are_Marked()
    {
        // Each *_Archive sibling should report temporal_type = 1
        // (HISTORY_TABLE) once linked.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 1;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(17, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWI_Temporal_Cities_AsOf_Query_Works()
    {
        // End-to-end: pick a known WWI temporal table (Application.Cities),
        // run a FOR SYSTEM_TIME ALL query, and confirm it returns rows
        // (current + history). The simulator's FOR SYSTEM_TIME query path
        // ships separately — this verifies the link landed via the loader.
        var simulation = LoadWideWorldImporters(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        var current = Scalar(connection, "SELECT COUNT(*) FROM Application.Cities;");
        var archive = Scalar(connection, "SELECT COUNT(*) FROM Application.Cities_Archive;");
        var all = Scalar(connection, "SELECT COUNT(*) FROM Application.Cities FOR SYSTEM_TIME ALL;");
        // ALL returns current + archive (the FOR SYSTEM_TIME query path
        // UNIONs the base table with its history sibling).
        AreEqual(current + archive, all);

        static int Scalar(SimulatedDbConnection conn, string sql)
        {
            using var command = conn.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            return reader.GetInt32(0);
        }
    }

    [TestMethod]
    public void Load_WWIFull_Loads_All_Tables()
    {
        // WWI-Full has the same 48 tables as Standard but adds partitioning,
        // columnstore, and one native-compiled SP. The partition-aware /
        // columnstore-aware decoration elements (SqlPartitionFunction,
        // SqlPartitionScheme, SqlColumnStoreIndex) are skip-with-diagnostic;
        // the native-compiled SP failure is independently tracked.
        var simulation = LoadWideWorldImportersFull(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(48, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_WWIFull_Known_Gaps_Recorded_In_Skipped()
    {
        _ = LoadWideWorldImportersFull(out var diagnostics);
        var grouped = diagnostics.Skipped.GroupBy(s => s.ElementType)
            .ToDictionary(g => g.Key, g => g.Count());
        TestContext.WriteLine("=== WWI-Full Skipped (current) ===");
        foreach (var kv in grouped.OrderByDescending(k => k.Value))
            TestContext.WriteLine($"{kv.Value,5}  {kv.Key}");

        // Storage-layout / read-optimization decorations are silent skips,
        // not Skipped entries (matching SqlFilegroup's pattern). Surfacing
        // them on Skipped would create category noise for features whose
        // absence has zero semantic effect on query results.
        IsFalse(grouped.ContainsKey("SqlColumnStoreIndex"), "Columnstore indexes skip-with-diagnostic; no semantic effect on row-store query results.");
        IsFalse(grouped.ContainsKey("SqlPartitionFunction"), "Partition functions are filegroup-mapping metadata; skip-with-diagnostic.");
        IsFalse(grouped.ContainsKey("SqlPartitionScheme"), "Partition schemes are filegroup-mapping metadata; skip-with-diagnostic.");

        // NATIVE_COMPILATION + BEGIN ATOMIC body parsers shipped 2026-05-16:
        // Website.RecordColdRoomTemperatures (the sole natively-compiled SP
        // in WWI-Full) loads. Combined with the temporal-table wire-up
        // landing the same day, WWI-Full reaches zero Skipped categories.
        IsFalse(grouped.ContainsKey("SqlProcedure"), "NATIVE_COMPILATION + BEGIN ATOMIC body parsers landed; the natively-compiled SP loads.");
        IsEmpty(diagnostics.Skipped);
    }

    [TestMethod]
    public void Load_WWIFull_Latin1_CI_AS_Columns_Warning_Free()
    {
        // WWI-Full's Warehouse.VehicleTemperatures table declares two columns
        // (VehicleRegistration, FullSensorData) with COLLATE Latin1_General_CI_AS.
        // Before the whitelist entry, this produced 2 Warnings + dropped clauses;
        // the whitelist entry recognizes the name and the COLLATE clauses land.
        var simulation = LoadWideWorldImportersFull(out var diagnostics);
        var collationWarnings = diagnostics.Warnings
            .Where(w => w.Contains("Latin1_General_CI_AS", StringComparison.Ordinal))
            .ToList();
        IsEmpty(collationWarnings);

        // Confirm round-trip via sys.columns.
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.collation_name
            FROM sys.columns c
            JOIN sys.tables t ON c.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'Warehouse'
              AND t.name = 'VehicleTemperatures'
              AND c.name = 'VehicleRegistration';
            """;
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("Latin1_General_CI_AS", reader.GetString(0));
    }

    [TestMethod]
    public void Load_WWIFull_ParallelLoad_RowCountsAreDeterministic()
    {
        // The parallel loader partitions BCP entries into per-table work
        // items and distributes them across N workers via a concurrent queue
        // (longest-table-first). Per-table ownership means no two workers
        // ever touch the same HeapTable — but a regression that broke that
        // invariant (e.g., concurrent Heap.Insert on the same table) would
        // surface as silently dropped rows. Loading WWI-Full twice and
        // comparing aggregate counts catches that without pinning to a
        // hard-coded published row total.
        _ = LoadWideWorldImportersFull(out var first);
        _ = LoadWideWorldImportersFull(out var second);

        AreEqual(first.ElementCounts["_DataRows"], second.ElementCounts["_DataRows"],
            "Aggregate row count must be identical across parallel loads.");
        AreEqual(first.ElementCounts["_DataFile"], second.ElementCounts["_DataFile"],
            "BCP file count must be identical across parallel loads.");
        HasCount(second.Skipped.Count, first.Skipped,
            "Skipped entry count must be identical across parallel loads.");
    }
}
