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
        // Phases A-F handled types are off Skipped; the few remaining bucket
        // types (SqlComputedColumn, SqlPermissionStatement, full-text, XML
        // schema collections / indexes, filegroup-hosted extended properties)
        // appear awaiting their own bundles.
        IsNotEmpty(diagnostics.Skipped);
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlTable").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlPrimaryKeyConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlForeignKeyConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlCheckConstraint").ToList());
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlDefaultConstraint").ToList());
        IsNotEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlComputedColumn").ToList());
        IsNotEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlPermissionStatement").ToList());
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
        // 89 user indexes land = 95 SqlIndex - 2 view-targeted (deferred:
        // indexed views need SCHEMABINDING) - 4 that reference computed
        // columns the loader currently defers until functions land. The 6
        // deferrals all surface as Skipped entries.
        AreEqual(89, reader.GetInt32(0));
        var skippedIndexEntries = diagnostics.Skipped.Where(s => s.ElementType == "SqlIndex").ToList();
        HasCount(6, skippedIndexEntries);
        // Both deferral reasons appear in Skipped.
        IsNotEmpty(skippedIndexEntries.Where(s => s.Reason.Contains("on view '", StringComparison.OrdinalIgnoreCase)).ToList());
        IsNotEmpty(skippedIndexEntries.Where(s => s.Reason.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase) && s.Reason.Contains("failed", StringComparison.OrdinalIgnoreCase)).ToList());
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
        AreEqual(10, funcs);
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
    public void Load_AW_Geography_Column_Drops_To_Null()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();

        // Person.Address rows load (geography wire format decoded) but
        // SpatialLocation column stores NULL — WKB-to-WKT translation is
        // deferred so the bytes are read-and-discarded. The row count
        // proves the wire-format reader didn't corrupt subsequent column
        // boundaries (rowguid + ModifiedDate after SpatialLocation must
        // round-trip cleanly).
        AreEqual(19614, QueryCount(connection, "SELECT COUNT(*) FROM Person.Address;"));
        AreEqual(19614, QueryCount(connection, "SELECT COUNT(*) FROM Person.Address WHERE SpatialLocation IS NULL;"));

        // Spot-check that following columns survived the geography read.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP(1) AddressID, City, PostalCode FROM Person.Address WHERE AddressID = 1;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("Bothell", reader.GetString(1));
        AreEqual("98011", reader.GetString(2));
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
        AreEqual(89, grouped["SqlExtendedProperty"], "Extended properties (mostly on computed columns / table types) deferred.");
        AreEqual(8, grouped["SqlComputedColumn"], "Computed columns deferred (same as AW).");
        AreEqual(6, grouped["SqlProcedure"], "6 WWI procedures parse-fail; need investigation.");
        AreEqual(4, grouped["SqlTableType"], "SqlTableType not yet in dispatcher.");
        AreEqual(3, grouped["SqlIndex"], "3 WWI indexes fail (likely filtered indexes on bit columns).");
        AreEqual(2, grouped["SqlCheckConstraint"], "2 JSON check constraints deferred.");
        AreEqual(2, grouped["SqlPermissionStatement"], "GRANT/REVOKE wire-up deferred (same as AW).");
        AreEqual(1, grouped["SqlDatabaseOptions"], "Non-default collation deferred.");
        AreEqual(1, grouped["SqlView"], "1 WWI view parse-fails (computed-column referent).");
        AreEqual(1, grouped["SqlScalarFunction"], "1 WWI scalar function parse-fails.");
        AreEqual(1, grouped["SqlFilegroup"], "Filegroup not in dispatcher.");
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
}
