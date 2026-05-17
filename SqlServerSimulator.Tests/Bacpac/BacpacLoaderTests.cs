using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Bacpac;

/// <summary>
/// Migrated bacpac-loader tests. Each test owns a synthetic bacpac built
/// via <see cref="BacpacBuilder"/> that exercises exactly the feature
/// its assertion measures — no shared multi-GB reference fixtures.
/// </summary>
[TestClass]
public class BacpacLoaderTests
{
    [TestMethod]
    public void ParallelLoad_Same_Bacpac_Gives_Deterministic_Counts()
    {
        // The parallel loader partitions BCP entries into per-table work
        // items distributed across N workers. Per-table ownership means
        // no two workers touch the same HeapTable — a regression breaking
        // that invariant (concurrent Heap.Insert) would silently drop
        // rows. Loading twice and comparing aggregate counts catches it.
        // Use a builder bacpac to keep this hermetic.
        var bytes = StreamToArray(BacpacBuilder.Create()
            .Table("dbo", "T1", t => { _ = t.Column("Id", "int"); for (var i = 0; i < 50; i++) _ = t.Row(i); })
            .Table("dbo", "T2", t => { _ = t.Column("Id", "int"); for (var i = 0; i < 75; i++) _ = t.Row(i); })
            .Table("dbo", "T3", t => { _ = t.Column("Id", "int"); for (var i = 0; i < 30; i++) _ = t.Row(i); })
            .Build());

        _ = Simulation.FromBacpac(new MemoryStream(bytes, writable: false), out var first);
        _ = Simulation.FromBacpac(new MemoryStream(bytes, writable: false), out var second);

        AreEqual(first.ElementCounts["_DataRows"], second.ElementCounts["_DataRows"]);
        AreEqual(first.ElementCounts["_DataFile"], second.ElementCounts["_DataFile"]);
        HasCount(second.Skipped.Count, first.Skipped);
        AreEqual(155, first.ElementCounts["_DataRows"]);

        static byte[] StreamToArray(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    [TestMethod]
    public void NamedSchemas_LandIn_sys_schemas()
    {
        using var bacpac = BacpacBuilder.Create()
            .Schema("HumanResources")
            .Schema("Person")
            .Schema("Production")
            .Schema("Purchasing")
            .Schema("Sales")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diagnostics);
        IsEmpty(diagnostics.Skipped);
        AreEqual(5, diagnostics.ElementCounts["SqlSchema"]);

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('dbo','sys','INFORMATION_SCHEMA','guest','public') ORDER BY name;";
        using var reader = command.ExecuteReader();

        var schemas = new List<string>();
        while (reader.Read())
            schemas.Add(reader.GetString(0));

        HasCount(5, schemas);
        AreEqual("HumanResources", schemas[0]);
        AreEqual("Person", schemas[1]);
        AreEqual("Production", schemas[2]);
        AreEqual("Purchasing", schemas[3]);
        AreEqual("Sales", schemas[4]);
    }

    [TestMethod]
    public void PrimaryKey_LandsIn_sys_key_constraints_WithName()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Product", t => t
                .Column("Id", "int")
                .Column("Name", "nvarchar(50)")
                .PrimaryKey("PK_Product_Id", "Id"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, diag.ElementCounts["SqlPrimaryKeyConstraint"]);

        AreEqual("PK_Product_Id", sim.ExecuteScalar("""
            SELECT kc.name
              FROM sys.key_constraints kc
              JOIN sys.tables t ON kc.parent_object_id = t.object_id
             WHERE t.name = 'Product' AND kc.type = 'PK';
            """));
    }

    [TestMethod]
    public void AllFiveConstraintTypes_LandIn_CatalogViews()
    {
        // One bacpac that exercises PK + UNIQUE + FK + CHECK + DEFAULT —
        // mirrors the AW counts-by-type smoke test without the 71-table
        // fixture cost.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Parent", t => t
                .Column("Id", "int")
                .Column("Code", "nvarchar(20)")
                .PrimaryKey("PK_Parent_Id", "Id")
                .Unique("UQ_Parent_Code", "Code"))
            .Table("dbo", "Child", t => t
                .Column("Id", "int")
                .Column("ParentId", "int")
                .Column("Age", "int")
                .Column("Status", "nvarchar(20)", nullable: true)
                .PrimaryKey("PK_Child_Id", "Id")
                .ForeignKey("FK_Child_Parent", ["ParentId"], "dbo", "Parent", ["Id"])
                .Check("CK_Child_Age", "[Age] > 0")
                .Default("DF_Child_Status", "Status", "'active'"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK';"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'UQ';"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.foreign_keys;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.check_constraints;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.default_constraints;"));
    }

    [TestMethod]
    public void DatabaseOption_ReadCommittedSnapshot_TogglesFlag()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("IsReadCommittedSnapshot", "True")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, diag.ElementCounts["SqlDatabaseOptions"]);
        IsTrue((bool)sim.ExecuteScalar("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = 'simulated';")!);
    }

    [TestMethod]
    public void MultipleTables_LandIn_sys_tables()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T1", t => t.Column("Id", "int"))
            .Table("dbo", "T2", t => t.Column("Id", "int"))
            .Table("dbo", "T3", t => t.Column("Id", "int"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(3, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.tables;"));
        AreEqual(3, diag.ElementCounts["SqlTable"]);
    }

    [TestMethod]
    public void UserDefinedDataType_RegistersIn_sys_types_AndNullabilityPropagates()
    {
        // Alias type CREATE TYPE [dbo].[Name] FROM nvarchar(50) NOT NULL —
        // surfaces on sys.types with a >= 256 user_type_id. A column
        // declared via the alias inherits the NOT NULL nullability default.
        // (sys.columns.user_type_id continues to reflect the underlying
        // built-in's id — the simulator resolves alias to base at column-
        // create time; alias identity is metadata-only.)
        using var bacpac = BacpacBuilder.Create()
            .UserDefinedDataType("dbo", "Name", "nvarchar(50)", nullable: false)
            .Table("dbo", "ProductCategory", t => t
                .Column("Id", "int")
                .Column("CategoryName", "[dbo].[Name]", nullable: true))   // explicit nullable to suppress builder default
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.types WHERE name = 'Name' AND user_type_id >= 256;"));
        // Column should be NOT NULL because the alias declares NOT NULL,
        // overriding the explicit-nullable builder default per probe
        // behavior (UDDT-default nullability wins when the usage site
        // doesn't specify its own).
        IsFalse((bool)sim.ExecuteScalar("""
            SELECT c.is_nullable FROM sys.columns c
              JOIN sys.tables t ON c.object_id = t.object_id
             WHERE t.name = 'ProductCategory' AND c.name = 'CategoryName';
            """)!);
    }

    [TestMethod]
    public void Columns_ShapeIn_sys_columns_PreservesNameTypeNullableIdentity()
    {
        // Mirrors the AW ProductCategory column-shape test: an IDENTITY PK,
        // a NOT-NULL bounded nvarchar, a NOT-NULL datetime, plus a sysname
        // and a hierarchyid column — the last two exercise the catalog
        // view's per-type max_length/precision/scale branches that AW used
        // to cover via its naturally-occurring sysname/hierarchyid columns.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Product", t => t
                .Column("ProductId", "int", identity: true)
                .Column("Name", "nvarchar(50)")
                .Column("ModifiedDate", "datetime")
                .Column("ObjectName", "sysname")
                .Column("OrgNode", "hierarchyid", nullable: true)
                .PrimaryKey("PK_Product", "ProductId"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, t.name AS type_name, c.is_nullable, c.is_identity
              FROM sys.columns c
              JOIN sys.tables tab ON c.object_id = tab.object_id
              JOIN sys.types t ON c.user_type_id = t.user_type_id
             WHERE tab.name = 'Product'
             ORDER BY c.column_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(string Name, string TypeName, bool Nullable, bool Identity)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));

        HasCount(5, rows);
        AreEqual("ProductId", rows[0].Name);
        AreEqual("int", rows[0].TypeName);
        IsFalse(rows[0].Nullable);
        IsTrue(rows[0].Identity);
        AreEqual("Name", rows[1].Name);
        AreEqual("nvarchar", rows[1].TypeName);
        IsFalse(rows[1].Nullable);
        IsFalse(rows[1].Identity);
        AreEqual("ModifiedDate", rows[2].Name);
        AreEqual("datetime", rows[2].TypeName);
        AreEqual("ObjectName", rows[3].Name);
        AreEqual("sysname", rows[3].TypeName);
        AreEqual("OrgNode", rows[4].Name);
        AreEqual("hierarchyid", rows[4].TypeName);
    }

    [TestMethod]
    public void BcpDataRoundTrips_AcrossCommonTypes()
    {
        // Single bacpac that exercises the most common BCP wire shapes the
        // loader handles: fixed-raw NOT NULL (int / bigint / datetime / date),
        // 1-byte-prefix (bit / uniqueidentifier), 2-byte-prefix bounded
        // text/binary (varchar / nvarchar / varbinary), and 8-byte-prefix
        // MAX types (nvarchar(MAX) / varbinary(MAX)).
        var guid = Guid.Parse("12345678-1234-5678-1234-567812345678");
        var stamp = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Unspecified);
        var dateOnly = new DateOnly(2025, 1, 2);
        var blob = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 };
        var maxBlob = new byte[1024];
        for (var i = 0; i < maxBlob.Length; i++) maxBlob[i] = (byte)(i % 256);

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Mixed", t => t
                .Column("Id", "int")
                .Column("Big", "bigint")
                .Column("When", "datetime")
                .Column("Day", "date")
                .Column("Active", "bit")
                .Column("Token", "uniqueidentifier")
                .Column("Name", "nvarchar(50)")
                .Column("Notes", "nvarchar(max)", nullable: true)
                .Column("Tag", "varchar(20)")
                .Column("Blob", "varbinary(16)")
                .Column("Big_Blob", "varbinary(max)", nullable: true)
                .Row(1, 9_000_000_000L, stamp, dateOnly, true, guid, "Alice", "Long notes here", "tag-1", blob, maxBlob))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, diag.ElementCounts["_DataRows"]);

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Big, [When], Day, Active, Token, Name, Notes, Tag, Blob, Big_Blob FROM Mixed;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(9_000_000_000L, reader.GetInt64(1));
        AreEqual(stamp, reader.GetDateTime(2));
        AreEqual(new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Unspecified), reader.GetDateTime(3));
        IsTrue(reader.GetBoolean(4));
        AreEqual(guid, reader.GetGuid(5));
        AreEqual("Alice", reader.GetString(6));
        AreEqual("Long notes here", reader.GetString(7));
        AreEqual("tag-1", reader.GetString(8));
        var blobOut = (byte[])reader.GetValue(9);
        CollectionAssert.AreEqual(blob, blobOut);
        var maxBlobOut = (byte[])reader.GetValue(10);
        CollectionAssert.AreEqual(maxBlob, maxBlobOut);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void DecimalColumn_RoundTripsThroughBcp_AcrossPrecisionBuckets()
    {
        // Three precision buckets exercise the 4 / 8 / 12-byte mantissa
        // widths the DACFx wire format uses. Negative values pin the
        // sign byte (positive=1 / negative=0). The 1899.00 / -360.000 /
        // 10.000 values mirror the original WWI Invoice/Stock pins.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "MoneyRow", t => t
                .Column("Id", "int")
                .Column("UnitPrice", "decimal(18, 2)")
                .Column("Quantity", "decimal(10, 3)")
                .Column("TaxRate", "decimal(8, 3)")
                .Row(1, 1899.00m, 12.500m, 10.000m)
                .Row(2, 0.66m, -360.000m, 15.000m))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UnitPrice, Quantity, TaxRate FROM MoneyRow ORDER BY Id;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1899.00m, reader.GetDecimal(0));
        AreEqual(12.500m, reader.GetDecimal(1));
        AreEqual(10.000m, reader.GetDecimal(2));
        IsTrue(reader.Read());
        AreEqual(0.66m, reader.GetDecimal(0));
        AreEqual(-360.000m, reader.GetDecimal(1));
        AreEqual(15.000m, reader.GetDecimal(2));
    }

    [TestMethod]
    public void HierarchyIdColumn_RoundTrips_ForCanonicalPaths()
    {
        // OrdPath wire bytes packed via MakeHierarchyIdBytes; the loader
        // decodes back to canonical "/N/" form. Tests the 4-prefix code
        // (01XX1 / 100XX1 / 101XXX1 / 110…1) over a few representative
        // ordinals from each range.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "OrgChart", t => t
                .Column("Id", "int")
                .Column("OrgNode", "hierarchyid", nullable: true)
                .Row(1, Array.Empty<byte>())                       // root "/"
                .Row(2, BacpacBuilder.MakeHierarchyIdBytes(1))       // "/1/"
                .Row(3, BacpacBuilder.MakeHierarchyIdBytes(1, 1))    // "/1/1/"
                .Row(4, BacpacBuilder.MakeHierarchyIdBytes(5))       // "/5/" — range [4..7]
                .Row(5, BacpacBuilder.MakeHierarchyIdBytes(20))      // "/20/" — range [16..79]
                .Row(6, null))                                       // NULL
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        AreEqual(6, sim.ExecuteScalar("SELECT COUNT(*) FROM OrgChart;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM OrgChart WHERE OrgNode IS NULL;"));
        AreEqual("/", sim.ExecuteScalar("SELECT OrgNode.ToString() FROM OrgChart WHERE Id = 1;"));
        AreEqual("/1/", sim.ExecuteScalar("SELECT OrgNode.ToString() FROM OrgChart WHERE Id = 2;"));
        AreEqual("/1/1/", sim.ExecuteScalar("SELECT OrgNode.ToString() FROM OrgChart WHERE Id = 3;"));
        AreEqual("/5/", sim.ExecuteScalar("SELECT OrgNode.ToString() FROM OrgChart WHERE Id = 4;"));
        AreEqual("/20/", sim.ExecuteScalar("SELECT OrgNode.ToString() FROM OrgChart WHERE Id = 5;"));
    }

    [TestMethod]
    public void XmlColumn_RoundTripsThroughBcp()
    {
        var xmlBody = "<Resume><Name>Alice</Name><Skills><Skill>SQL</Skill></Skills></Resume>";
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "JobCandidate", t => t
                .Column("Id", "int")
                .Column("Resume", "xml")
                .Row(1, xmlBody))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(xmlBody, sim.ExecuteScalar("SELECT CAST(Resume AS nvarchar(MAX)) FROM JobCandidate;"));
    }

    [TestMethod]
    public void GeographyColumn_PointBytes_DecodeTo_PointWkt()
    {
        // Microsoft spatial UDT simple-point wire form → POINT (long lat)
        // WKT through the SpatialWkbDecoder. The decoder inverts axes for
        // geography vs geometry, so the WKT prints longitude first.
        var pointBytes = BacpacBuilder.MakeGeographyPoint(latitude: 47.61, longitude: -122.20);

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Place", t => t
                .Column("Id", "int")
                .Column("Loc", "geography")
                .Row(1, pointBytes))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        var wkt = (string)sim.ExecuteScalar("SELECT CAST(Loc AS nvarchar(MAX)) FROM Place;")!;
        IsTrue(wkt.StartsWith("POINT (-122.2", StringComparison.Ordinal), $"unexpected WKT '{wkt}'");
    }

    [TestMethod]
    public void MoneyAndSmallMoney_RoundTripThroughBcp()
    {
        // money / smallmoney share the int64-scaled-by-10000 storage form
        // but differ in width (8 vs 4 bytes). The money decoder splits the
        // 8-byte payload into high (signed int32) + low (uint32) halves;
        // the encoder mirrors that split.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Pricing", t => t
                .Column("Id", "int")
                .Column("Discount", "money")
                .Column("UnitPrice", "smallmoney")
                .Row(1, 12345.6789m, 9.99m)
                .Row(2, -250.00m, -1.50m))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Discount, UnitPrice FROM Pricing ORDER BY Id;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(12345.6789m, reader.GetDecimal(0));
        AreEqual(9.99m, reader.GetDecimal(1));
        IsTrue(reader.Read());
        AreEqual(-250.00m, reader.GetDecimal(0));
        AreEqual(-1.50m, reader.GetDecimal(1));
    }

    [TestMethod]
    public void DateTime2_RoundTripsThroughBcp_AtMultiplePrecisions()
    {
        // datetime2(N) wire format: variable-byte LE ticks-at-precision-unit
        // + 3-byte LE day count. Precision determines time-bytes width
        // (3 / 4 / 5 for 0-2 / 3-4 / 5-7).
        var t0 = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Unspecified);
        var t7 = new DateTime(2025, 6, 15, 12, 30, 45, 123, DateTimeKind.Unspecified).AddTicks(4567);

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Timestamps", t => t
                .Column("Id", "int")
                .Column("LowPrec", "datetime2(0)")
                .Column("MidPrec", "datetime2(3)")
                .Column("HighPrec", "datetime2(7)")
                .Row(1, t0, t0, t7))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LowPrec, MidPrec, HighPrec FROM Timestamps;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(t0, reader.GetDateTime(0));
        AreEqual(t0, reader.GetDateTime(1));
        AreEqual(t7, reader.GetDateTime(2));
    }

    [TestMethod]
    public void BcpNullValues_Decode_Across1And2And8BytePrefixes()
    {
        // Null markers: 1-byte 0xFF for 1-byte-prefix types, 2-byte 0xFFFF
        // for 2-byte-prefix bounded text/binary, 8-byte -1 for MAX types.
        // Exercises every NULL branch in one row.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Nullable", t => t
                .Column("Id", "int", nullable: true)
                .Column("Active", "bit", nullable: true)
                .Column("Token", "uniqueidentifier", nullable: true)
                .Column("Name", "nvarchar(50)", nullable: true)
                .Column("Notes", "nvarchar(max)", nullable: true)
                .Row(null, null, null, null, null))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM Nullable WHERE Id IS NULL AND Active IS NULL AND Token IS NULL AND Name IS NULL AND Notes IS NULL;"));
    }

    [TestMethod]
    public void View_LandsIn_sys_views_AndQueries()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t
                .Column("Id", "int")
                .Column("Active", "bit")
                .Row(1, true)
                .Row(2, false)
                .Row(3, true))
            .View("dbo", "ActiveItem", "CREATE VIEW dbo.ActiveItem AS SELECT Id FROM dbo.Item WHERE Active = 1;")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.views WHERE name = 'ActiveItem';"));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.ActiveItem;"));
    }

    [TestMethod]
    public void Procedure_LandsIn_sys_procedures_AndExecutes()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Box", t => t
                .Column("Id", "int")
                .Column("Side", "int")
                .Row(1, 5)
                .Row(2, 7))
            .Procedure("dbo", "GetBoxArea", """
                CREATE PROCEDURE dbo.GetBoxArea @Id int
                AS BEGIN
                    SELECT Side * Side AS Area FROM dbo.Box WHERE Id = @Id;
                END
                """)
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.procedures WHERE name = 'GetBoxArea';"));
        AreEqual(49, sim.ExecuteScalar("EXEC dbo.GetBoxArea @Id = 2;"));
    }

    [TestMethod]
    public void Procedure_WithSysnameParameters_Loads()
    {
        // Sysname is treated as a keyword by the procedure-parameter
        // parser; this test ensures it survives the bacpac header+body
        // re-concatenation path the loader uses.
        using var bacpac = BacpacBuilder.Create()
            .Procedure("dbo", "ReseedSequence", """
                CREATE PROCEDURE dbo.ReseedSequence
                    @seq_name sysname,
                    @new_start bigint
                AS BEGIN
                    SELECT @seq_name AS name, @new_start AS start_value;
                END
                """)
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.procedures WHERE name = 'ReseedSequence';"));
    }

    [TestMethod]
    public void ScalarFunction_LandsIn_sys_objects_AndExecutes()
    {
        using var bacpac = BacpacBuilder.Create()
            .ScalarFunction("dbo", "Doubled", """
                CREATE FUNCTION dbo.Doubled(@x int) RETURNS int
                AS BEGIN
                    RETURN @x * 2;
                END
                """)
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.objects WHERE type = 'FN' AND name = 'Doubled';"));
        AreEqual(42, sim.ExecuteScalar("SELECT dbo.Doubled(21);"));
    }

    [TestMethod]
    public void MultiStatementTvf_LandsIn_sys_objects_AndExecutes()
    {
        // Multi-statement table-valued function — exercises a different
        // CreateFunction code path from scalar UDFs (RETURNS @table TABLE(...)
        // shape vs RETURNS <scalar> AS BEGIN RETURN scalar END).
        using var bacpac = BacpacBuilder.Create()
            .MultiStatementTvf("dbo", "Splitter", """
                CREATE FUNCTION dbo.Splitter(@n int)
                RETURNS @result TABLE (Value int NOT NULL)
                AS BEGIN
                    DECLARE @i int = 1;
                    WHILE @i <= @n BEGIN
                        INSERT INTO @result (Value) VALUES (@i);
                        SET @i = @i + 1;
                    END
                    RETURN;
                END
                """)
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.objects WHERE type = 'TF' AND name = 'Splitter';"));
        AreEqual(3, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.Splitter(3);"));
        AreEqual(6, sim.ExecuteScalar("SELECT SUM(Value) FROM dbo.Splitter(3);"));
    }

    [TestMethod]
    public void Trigger_LandsIn_sys_triggers_AndFires()
    {
        // Exercises both INSERT and UPDATE trigger paths — the loader-emitted
        // trigger is AFTER INSERT, UPDATE.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Audited", t => t
                .Column("Id", "int")
                .Column("Note", "nvarchar(50)", nullable: true))
            .Table("dbo", "AuditLog", t => t
                .Column("Action", "nvarchar(20)"))
            .Trigger("dbo", "Audited", "trg_AuditedInsertUpdate", """
                CREATE TRIGGER dbo.trg_AuditedInsertUpdate ON dbo.Audited
                AFTER INSERT, UPDATE
                AS BEGIN
                    DECLARE @action nvarchar(20) = N'UPDATE';
                    IF NOT EXISTS (SELECT 1 FROM deleted) SET @action = N'INSERT';
                    INSERT INTO dbo.AuditLog (Action) VALUES (@action);
                END
                """)
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_AuditedInsertUpdate';"));
        _ = sim.ExecuteNonQuery("INSERT INTO dbo.Audited (Id, Note) VALUES (1, N'hello');");
        _ = sim.ExecuteNonQuery("UPDATE dbo.Audited SET Note = N'changed' WHERE Id = 1;");
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.AuditLog WHERE Action = N'INSERT';"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.AuditLog WHERE Action = N'UPDATE';"));
    }

    [TestMethod]
    public void Column_Collation_LandsIn_sys_columns_WarningFree()
    {
        // Column-level COLLATE override. The loader emits the clause only
        // when the name is on the recognized whitelist; the synthetic test
        // uses Latin1_General_CI_AS (whitelisted) and verifies it
        // round-trips through sys.columns without generating a warning.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "VehicleTemp", t => t
                .Column("Id", "int")
                .Column("Reg", "nvarchar(20)", collation: "Latin1_General_CI_AS"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        var collationWarnings = diag.Warnings.Where(w => w.Contains("Latin1_General_CI_AS", StringComparison.Ordinal)).ToList();
        IsEmpty(collationWarnings);
        AreEqual("Latin1_General_CI_AS", sim.ExecuteScalar("""
            SELECT c.collation_name FROM sys.columns c
              JOIN sys.tables t ON c.object_id = t.object_id
             WHERE t.name = 'VehicleTemp' AND c.name = 'Reg';
            """));
    }

    [TestMethod]
    public void Collation_RoundsTripsThrough_sys_databases_AndDatabasePropertyEx()
    {
        // Whitelisted collation stored on Database.CollationName + surfaced
        // through sys.databases.collation_name and DATABASEPROPERTYEX.
        // Comparison semantics still route through the default per
        // Collation.Default; the metadata is honest about the declaration.
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("Collation", "Latin1_General_100_CI_AS")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar("SELECT collation_name FROM sys.databases;"));
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar("SELECT DATABASEPROPERTYEX('simulated', 'Collation');"));
    }

    [TestMethod]
    public void Sequence_LandsIn_sys_sequences_AndAdvances()
    {
        using var bacpac = BacpacBuilder.Create()
            .Sequence("dbo", "OrderId", "int", startValue: 100, increment: 10)
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.sequences WHERE name = 'OrderId';"));
        AreEqual(100, sim.ExecuteScalar("SELECT NEXT VALUE FOR dbo.OrderId;"));
        AreEqual(110, sim.ExecuteScalar("SELECT NEXT VALUE FOR dbo.OrderId;"));
    }

    [TestMethod]
    public void Grant_DatabaseScope_LandsIn_sys_database_permissions()
    {
        // Database-scope GRANT to the pre-seeded `public` role — the
        // canonical AW / WWI bacpac form. The loader translates the
        // camel-case DACFx token to a space-separated multi-word
        // permission name (ViewAnyColumnEncryptionKeyDefinition →
        // "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION").
        using var bacpac = BacpacBuilder.Create()
            .Grant("ViewAnyColumnEncryptionKeyDefinition", "public")
            .Grant("ViewAnyColumnMasterKeyDefinition", "public")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(2, sim.ExecuteScalar("""
            SELECT COUNT(*)
            FROM sys.database_permissions p
            JOIN sys.database_principals g ON p.grantee_principal_id = g.principal_id
            WHERE g.name = 'public' AND permission_name LIKE 'VIEW ANY COLUMN%';
            """));
    }

    [TestMethod]
    public void Role_LandsIn_sys_database_principals()
    {
        using var bacpac = BacpacBuilder.Create()
            .Role("data_reader_role")
            .Role("data_writer_role")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.database_principals WHERE name IN ('data_reader_role', 'data_writer_role');"));
    }

    [TestMethod]
    public void SequenceBackedDefault_Applies_On_Insert()
    {
        // Sequence in phase 1, DEFAULT bound to NEXT VALUE FOR in phase 3 —
        // mirrors WWI's [Sequences].[CityID] / Application.Cities.CityID
        // dependency without the rest of WWI.
        using var bacpac = BacpacBuilder.Create()
            .Sequence("dbo", "OrderId", "int", startValue: 1000, increment: 1)
            .Table("dbo", "Order", t => t
                .Column("Id", "int")
                .Column("Note", "nvarchar(50)")
                .Default("DF_Order_Id", "Id", "NEXT VALUE FOR dbo.OrderId"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        _ = sim.ExecuteNonQuery("INSERT INTO dbo.[Order] (Note) VALUES (N'a'), (N'b');");
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.[Order];"));
        AreEqual(1000, sim.ExecuteScalar("SELECT MIN(Id) FROM dbo.[Order];"));
        AreEqual(1001, sim.ExecuteScalar("SELECT MAX(Id) FROM dbo.[Order];"));
    }

    [TestMethod]
    public void ParenWrappedLhs_CheckConstraint_LoadsAndEnforces()
    {
        // Canonical DACFx CHECK expression shape: `((<scalar>) = (<literal>))`.
        // The inner LHS paren-wrap was a parser regression that landed via
        // a WWI CK_*_Exactly_One_NOT_NULL_*_Required constraint. The
        // constraint must (a) load (CREATE TABLE succeeds) and (b) enforce
        // (INSERTing a violating row raises Msg 547).
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Deals", t => t
                .Column("Id", "int")
                .Column("Discount", "decimal(10, 2)", nullable: true)
                .Column("UnitPrice", "decimal(10, 2)", nullable: true)
                .Check("CK_Deals_Exactly_One_NOT_NULL_Pricing", "((case when [Discount] is not null then 1 else 0 end + case when [UnitPrice] is not null then 1 else 0 end) = (1))"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        // Insert with both NULL — violates the constraint.
        var ex = sim.AssertSqlError("INSERT INTO dbo.Deals (Id, Discount, UnitPrice) VALUES (1, NULL, NULL);", 547);
        Contains("CK_Deals_Exactly_One_NOT_NULL_Pricing", ex.Message);
    }

    [TestMethod]
    public void TemporalTable_PairsBaseAndHistory_AndAsOfReturnsBoth()
    {
        // System-versioned base table + matching history sibling. The loader
        // emits the ALTER TABLE SET (SYSTEM_VERSIONING = ON …) in phase 5,
        // after both endpoints exist. sys.tables.temporal_type ends up at 2
        // for the base, 1 for the history. FOR SYSTEM_TIME ALL UNIONs both.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Cities", t => t
                .Column("CityID", "int")
                .Column("CityName", "nvarchar(50)")
                .Column("ValidFrom", "datetime2(7)", periodKind: PeriodColumnKind.Start)
                .Column("ValidTo", "datetime2(7)", periodKind: PeriodColumnKind.End)
                .PrimaryKey("PK_Cities", "CityID")
                .SystemVersioned("dbo", "Cities_Archive"))
            .Table("dbo", "Cities_Archive", t => t
                .Column("CityID", "int")
                .Column("CityName", "nvarchar(50)")
                .Column("ValidFrom", "datetime2(7)")
                .Column("ValidTo", "datetime2(7)"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'Cities' AND temporal_type = 2;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'Cities_Archive' AND temporal_type = 1;"));
        AreEqual(0, sim.ExecuteScalar("SELECT COUNT(*) FROM Cities FOR SYSTEM_TIME ALL;"));
    }

    [TestMethod]
    public void TableType_LandsIn_sys_table_types()
    {
        using var bacpac = BacpacBuilder.Create()
            .TableType("dbo", "IdList", t => t
                .Column("Id", "int")
                .PrimaryKey("PK_IdList", "Id"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.table_types WHERE name = 'IdList';"));
    }

    [TestMethod]
    public void ComputedColumn_LandsAs_is_computed_AndEvaluatesOnRead()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Rectangle", t => t
                .Column("Id", "int")
                .Column("Width", "int")
                .Column("Height", "int")
                .ComputedColumn("Area", "Width * Height")
                .Row(1, 4, 5)
                .Row(2, 6, 7))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.columns WHERE name = 'Area' AND is_computed = 1;"));
        AreEqual(20, sim.ExecuteScalar("SELECT Area FROM Rectangle WHERE Id = 1;"));
        AreEqual(42, sim.ExecuteScalar("SELECT Area FROM Rectangle WHERE Id = 2;"));
    }

    [TestMethod]
    public void ExtendedProperties_LandIn_sys_extended_properties_AcrossHostKinds()
    {
        // Six host kinds exercised: column / table / schema / database /
        // index / constraint. The loader's switch dispatches each to the
        // right sp_addextendedproperty shape; the catalog view round-trips
        // all six.
        using var bacpac = BacpacBuilder.Create()
            .Schema("audit")
            .Table("audit", "Trail", t => t
                .Column("Id", "int")
                .Column("Message", "nvarchar(200)")
                .PrimaryKey("PK_Trail", "Id")
                .Index("IX_Trail_Message", ["Message"])
                .Check("CK_Trail_NonNegId", "[Id] >= 0"))
            .ExtendedProperty("MS_Description", "row id", schemaName: "audit", tableName: "Trail", columnName: "Id")
            .ExtendedProperty("MS_Description", "audit trail table", schemaName: "audit", tableName: "Trail")
            .ExtendedProperty("MS_Description", "audit schema", schemaName: "audit")
            .ExtendedProperty("MS_Description", "database-level marker")
            .IndexExtendedProperty("audit", "Trail", "IX_Trail_Message", "MS_Description", "search index")
            .ConstraintExtendedProperty("audit", "CK_Trail_NonNegId", "MS_Description", "Id must not be negative")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(6, diag.ElementCounts["SqlExtendedProperty"]);
        AreEqual(6, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.extended_properties WHERE name = 'MS_Description';"));
        // Verify the index host specifically (class=7).
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.extended_properties WHERE name = 'MS_Description' AND class = 7;"));
        // And the constraint host (class=1 with major_id on the constraint).
        AreEqual("Id must not be negative", sim.ExecuteScalar("""
            SELECT CAST(ep.value AS nvarchar(MAX))
              FROM sys.extended_properties ep
              JOIN sys.check_constraints c ON ep.major_id = c.object_id
             WHERE c.name = 'CK_Trail_NonNegId' AND ep.name = 'MS_Description';
            """));
        // fn_listextendedproperty TVF — exercises a separate enumeration
        // path from sys.extended_properties. Table-level → 1 row (the
        // "audit trail table" property bound at level1=TABLE/level2=NULL).
        AreEqual(1, sim.ExecuteScalar("""
            SELECT COUNT(*) FROM fn_listextendedproperty(N'MS_Description', N'SCHEMA', N'audit', N'TABLE', N'Trail', NULL, NULL);
            """));
    }

    [TestMethod]
    public void IndexOnView_LandsOn_Skipped_WithSchemabindingReason()
    {
        // Indexed views need SCHEMABINDING machinery the simulator doesn't
        // model; the loader pre-scans SqlView Names and routes any
        // view-targeted SqlIndex to Skipped with a clear reason. Exercise
        // that deferral path with a view + a matching SqlIndex.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int"))
            .View("dbo", "ItemView", "CREATE VIEW dbo.ItemView AS SELECT Id FROM dbo.Item;")
            .IndexOnView("dbo", "ItemView", "IX_ItemView_Id", ["Id"])
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        AreEqual(0, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ItemView_Id';"));
        var skippedIndexes = diag.Skipped.Where(s => s.ElementType == "SqlIndex").ToList();
        HasCount(1, skippedIndexes);
        Contains("on view", skippedIndexes[0].Reason);
    }

    [TestMethod]
    public void Index_LandsIn_sys_indexes_WithExpectedShape()
    {
        // Non-unique nonclustered index with an INCLUDE clause — the most
        // common shape DACFx emits for AW / WWI.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Customer", t => t
                .Column("Id", "int")
                .Column("LastName", "nvarchar(50)")
                .Column("Email", "nvarchar(200)", nullable: true)
                .PrimaryKey("PK_Customer", "Id")
                .Index("IX_Customer_LastName", ["LastName"], includedColumns: ["Email"]))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, diag.ElementCounts["SqlIndex"]);

        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Customer_LastName';"));
    }

    [TestMethod]
    public void ForeignKey_CascadeAction_LandsIn_sys_foreign_keys()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Parent", t => t
                .Column("Id", "int")
                .PrimaryKey("PK_Parent_Id", "Id"))
            .Table("dbo", "Child", t => t
                .Column("Id", "int")
                .Column("ParentId", "int")
                .PrimaryKey("PK_Child_Id", "Id")
                .ForeignKey("FK_Child_Parent", ["ParentId"], "dbo", "Parent", ["Id"], onDelete: "CASCADE"))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.foreign_keys WHERE delete_referential_action = 1;"));
    }
}
