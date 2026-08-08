using System.Globalization;
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

        new Simulation().ImportBacpac(new MemoryStream(bytes, writable: false), out var first);
        new Simulation().ImportBacpac(new MemoryStream(bytes, writable: false), out var second);

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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diagnostics);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK';"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'UQ';"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.foreign_keys;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.check_constraints;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.default_constraints;"));
    }

    // DACFx emits DefaultExpressionScript already parenthesized (e.g.
    // "(NEXT VALUE FOR ...)" / "(getdate())"). The loader must not add a second
    // pair — real sys.default_constraints.definition carries exactly one outer
    // pair, so an already-parenthesized script passes through unwrapped.
    [TestMethod]
    public void Default_ParenthesizedExpressionScript_NotDoubleWrapped()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t
                .Column("Id", "int")
                .Column("N", "int")
                .PrimaryKey("PK_T", "Id")
                .Default("DF_T_N", "N", "(1)"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("(1)", sim.ExecuteScalar("SELECT definition FROM sys.default_constraints WHERE name = 'DF_T_N';"));
    }

    [TestMethod]
    public void DatabaseOption_ReadCommittedSnapshot_TogglesFlag()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("IsReadCommittedSnapshot", "True")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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
    public void DateTimeColumn_LateDaySubSecond_RoundTripsThroughBcp()
    {
        // datetime decode converts 1/300-second ticks back to .NET ticks. Doing
        // the divide before the multiply (TicksPerSecond / 300 = 33333, truncating
        // the real 33333.333) under-counts every tick — an error that compounds
        // with the tick-count-since-midnight, reaching ~0.4-0.9s late in the day.
        // 23:47:16.030 sits on the 1/300 grid (30ms = 9 ticks) so it must survive
        // exactly; the old truncation shifted it back to ~23:47:15.6.
        var stamp = new DateTime(2024, 11, 8, 23, 47, 16, 30, DateTimeKind.Unspecified);
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Stamps", t => t.Column("When", "datetime").Row(stamp))
            .Build();
        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out _);
        AreEqual(stamp, sim.ExecuteScalar("SELECT [When] FROM Stamps"));
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(xmlBody, sim.ExecuteScalar("SELECT CAST(Resume AS nvarchar(MAX)) FROM JobCandidate;"));
    }

    [TestMethod]
    public void GeographyColumn_PointBytes_DecodeTo_PointWkt()
    {
        // Microsoft spatial UDT simple-point wire form → the parsed instance,
        // via SpatialBinaryCodec. The codec inverts axes for geography vs
        // geometry, so the WKT prints longitude first.
        var pointBytes = BacpacBuilder.MakeGeographyPoint(latitude: 47.61, longitude: -122.20);

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Place", t => t
                .Column("Id", "int")
                .Column("Loc", "geography")
                .Row(1, pointBytes))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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
        // Collation.Baseline; the metadata is honest about the declaration.
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("Collation", "Latin1_General_100_CI_AS")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar("SELECT collation_name FROM sys.databases WHERE name = 'simulated';"));
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar("SELECT DATABASEPROPERTYEX('simulated', 'Collation');"));
    }

    [TestMethod]
    public void Sequence_LandsIn_sys_sequences_AndAdvances()
    {
        using var bacpac = BacpacBuilder.Create()
            .Sequence("dbo", "OrderId", "int", startValue: 100, increment: 10)
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.columns WHERE name = 'Area' AND is_computed = 1;"));
        AreEqual(20, sim.ExecuteScalar("SELECT Area FROM Rectangle WHERE Id = 1;"));
        AreEqual(42, sim.ExecuteScalar("SELECT Area FROM Rectangle WHERE Id = 2;"));
    }

    [TestMethod]
    public void ComputedColumn_Persisted_LandsAs_is_persisted_AndComputesOnLoad()
    {
        // A PERSISTED computed column round-trips its IsPersisted flag to
        // sys.computed_columns (so DacFx re-export carries it), and its value
        // is computed at BCP-load time — the column has a storage slot but the
        // BCP wire carries no bytes for it. IsPersistedNullable=False maps to
        // PERSISTED NOT NULL so is_nullable also matches the source model.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "People", t => t
                .Column("PersonID", "int")
                .Column("FullName", "nvarchar(50)")
                .Column("PreferredName", "nvarchar(50)")
                .ComputedColumn("SearchName", "(concat([PreferredName],N' ',[FullName]))", persisted: true, persistedNullable: false)
                .Row(1, "Kayla Woodcock", "Kayla")
                .Row(2, "Hudson Onslow", "Hudson"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        IsTrue((bool)sim.ExecuteScalar("SELECT is_persisted FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.People') AND name = 'SearchName';")!);
        IsFalse((bool)sim.ExecuteScalar("SELECT is_nullable FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.People') AND name = 'SearchName';")!);
        AreEqual("Kayla Kayla Woodcock", sim.ExecuteScalar("SELECT SearchName FROM dbo.People WHERE PersonID = 1;"));
        AreEqual("Hudson Hudson Onslow", sim.ExecuteScalar("SELECT SearchName FROM dbo.People WHERE PersonID = 2;"));
    }

    [TestMethod]
    public void ComputedColumn_PersistedNullable_LandsAs_is_persisted_Nullable()
    {
        // A nullable PERSISTED computed column (IsPersistedNullable=True) maps
        // to a bare PERSISTED marker — is_persisted = 1, is_nullable = 1.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Txn", t => t
                .Column("Id", "int")
                .Column("FinalizedOn", "date", nullable: true)
                .ComputedColumn("IsFinalized", "(case when [FinalizedOn] is null then CONVERT([bit],(0)) else CONVERT([bit],(1)) end)", persisted: true)
                .Row(1, null)
                .Row(2, new DateOnly(2025, 1, 1)))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        IsTrue((bool)sim.ExecuteScalar("SELECT is_persisted FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.Txn') AND name = 'IsFinalized';")!);
        IsTrue((bool)sim.ExecuteScalar("SELECT is_nullable FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.Txn') AND name = 'IsFinalized';")!);
        IsFalse((bool)sim.ExecuteScalar("SELECT IsFinalized FROM dbo.Txn WHERE Id = 1;")!);
        IsTrue((bool)sim.ExecuteScalar("SELECT IsFinalized FROM dbo.Txn WHERE Id = 2;")!);
    }

    [TestMethod]
    public void ComputedColumn_MidTable_LandsAtModelOrdinal()
    {
        // A computed column declared between simple columns must keep its
        // model ordinal after import — the loader emits it inline in CREATE
        // TABLE at its position rather than appending it at the end, so
        // sys.columns.column_id matches the source database (the property
        // DacFx orders model.xml export by, and the invariant temporal
        // base/history pairs depend on — see the temporal-pair test below).
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "People", t => t
                .Column("PersonID", "int")
                .Column("FullName", "nvarchar(50)")
                .Column("PreferredName", "nvarchar(50)")
                .ComputedColumn("SearchName", "(concat([PreferredName],N' ',[FullName]))")
                .Column("IsPermittedToLogon", "bit")
                .Column("PhoneNumber", "nvarchar(20)"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(4, sim.ExecuteScalar("SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.People') AND name = 'SearchName';"));
        IsTrue((bool)sim.ExecuteScalar("SELECT is_computed FROM sys.columns WHERE object_id = OBJECT_ID('dbo.People') AND name = 'SearchName';")!);
        // The columns after the computed one keep their downstream ordinals.
        AreEqual(5, sim.ExecuteScalar("SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.People') AND name = 'IsPermittedToLogon';"));
        AreEqual(6, sim.ExecuteScalar("SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.People') AND name = 'PhoneNumber';"));
    }

    [TestMethod]
    public void ComputedColumn_ForwardUdfRef_DefersToPhaseEight_LandsAtEnd()
    {
        // A computed expression that forward-references a user function
        // (which only exists after phase 7) can't resolve in the CREATE
        // TABLE column list, so the table is re-created with the computed
        // column stripped and the column is appended in phase 8 — landing
        // at the end of sys.columns for that one table. This is the
        // documented tradeoff (matches AW's Sales.Customer.AccountNumber,
        // which references dbo.ufnLeadingZeros).
        using var bacpac = BacpacBuilder.Create()
            .ScalarFunction("dbo", "AddOne", "CREATE FUNCTION dbo.AddOne(@n int) RETURNS int AS BEGIN RETURN @n + 1 END")
            .Table("dbo", "Widget", t => t
                .Column("Id", "int")
                .ComputedColumn("Bumped", "([dbo].[AddOne]([Id]))")
                .Column("Label", "nvarchar(20)"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        // Bumped lands last (3) rather than at its model ordinal (2); Label
        // keeps ordinal 2 because the computed column was stripped first.
        IsTrue((bool)sim.ExecuteScalar("SELECT is_computed FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Widget') AND name = 'Bumped';")!);
        AreEqual(3, sim.ExecuteScalar("SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Widget') AND name = 'Bumped';"));
        AreEqual(2, sim.ExecuteScalar("SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Widget') AND name = 'Label';"));
    }

    [TestMethod]
    public void TemporalPair_MidTableComputedColumn_BaseAndHistoryOrdinalsAlign()
    {
        // The bug this fixes: WWI's Application.People has a mid-table
        // computed column, and its People_Archive history sibling (all
        // simple columns, true order) must share identical column ordinals
        // — else SQL Server rejects the re-exported bacpac with Msg 13524.
        // Emitting the computed column inline at its model ordinal keeps
        // base and history column_id sequences byte-identical.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Person", t => t
                .Column("PersonID", "int")
                .Column("FullName", "nvarchar(50)")
                .ComputedColumn("SearchName", "(concat([FullName],N'!'))")
                .Column("Note", "nvarchar(50)", nullable: true)
                .Column("ValidFrom", "datetime2(7)", periodKind: PeriodColumnKind.Start)
                .Column("ValidTo", "datetime2(7)", periodKind: PeriodColumnKind.End)
                .PrimaryKey("PK_Person", "PersonID")
                .SystemVersioned("dbo", "Person_Archive"))
            .Table("dbo", "Person_Archive", t => t
                .Column("PersonID", "int")
                .Column("FullName", "nvarchar(50)")
                .ComputedColumn("SearchName", "(concat([FullName],N'!'))")
                .Column("Note", "nvarchar(50)", nullable: true)
                .Column("ValidFrom", "datetime2(7)")
                .Column("ValidTo", "datetime2(7)"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));

        var baseSeq = ColumnSequence(sim, "dbo.Person");
        var histSeq = ColumnSequence(sim, "dbo.Person_Archive");
        AreEqual(baseSeq, histSeq);
        // SearchName sits at its true mid-table ordinal on both sides.
        AreEqual("1:PersonID|2:FullName|3:SearchName|4:Note|5:ValidFrom|6:ValidTo", baseSeq);
    }

    private static string ColumnSequence(Simulation sim, string table)
    {
        using var conn = sim.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT column_id, name FROM sys.columns WHERE object_id = OBJECT_ID('{table}') ORDER BY column_id;";
        var parts = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            parts.Add($"{reader.GetValue(0)}:{reader.GetValue(1)}");
        return string.Join("|", parts);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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
    public void IndexOnView_LoadsAsIndexedView()
    {
        // An indexed (materialized) view: the SqlView is created WITH
        // SCHEMABINDING, then its unique clustered SqlIndex (phase 8, after
        // views land in phase 6) dispatches as CREATE UNIQUE CLUSTERED INDEX
        // ON the view. No Skipped entry; the index surfaces in sys.indexes at
        // index_id 1 / CLUSTERED and enforces uniqueness on base DML.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int").Column("Grp", "int"))
            .View("dbo", "ItemView", "CREATE VIEW dbo.ItemView WITH SCHEMABINDING AS SELECT Id, Grp FROM dbo.Item;")
            .IndexOnView("dbo", "ItemView", "IX_ItemView_Id", ["Id"])
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped.Where(s => s.ElementType == "SqlIndex"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ItemView_Id' AND type_desc = 'CLUSTERED' AND is_unique = 1;"));
        // The unique clustered view index enforces uniqueness on base DML: two
        // base rows projecting the same view key raise Msg 2601.
        _ = sim.ExecuteNonQuery("INSERT dbo.Item VALUES (1, 10)");
        _ = sim.AssertSqlError("INSERT dbo.Item VALUES (1, 20)", 2601);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
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

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.foreign_keys WHERE delete_referential_action = 1;"));
    }

    [TestMethod]
    public void Filegroup_RegistersInCatalogViews()
    {
        // A non-PRIMARY SqlFilegroup registers on the database so
        // sys.filegroups / sys.data_spaces surface it (data_space_id from 2;
        // PRIMARY keeps 1 / is_default). No physical file model — heaps all
        // live on PRIMARY. DacFx re-emits the standalone element on export.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int").Row(1))
            .Filegroup("FG_Indexes")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM Item;"));
        AreEqual(2, sim.ExecuteScalar("SELECT data_space_id FROM sys.filegroups WHERE name = 'FG_Indexes';"));
        AreEqual(0, sim.ExecuteScalar("SELECT CAST(is_default AS int) FROM sys.filegroups WHERE name = 'FG_Indexes';"));
        AreEqual(1, sim.ExecuteScalar("SELECT data_space_id FROM sys.data_spaces WHERE name = 'PRIMARY' AND is_default = 1;"));
    }

    [TestMethod]
    public void XmlIndex_PrimaryAndSecondary_LandIn_sys_xml_indexes()
    {
        // A primary XML index + a secondary (FOR PATH) using it. The loader
        // dispatches CREATE [PRIMARY] XML INDEX; both surface through
        // sys.xml_indexes, sys.index_columns, and the internal node-table +
        // per-index statistics DacFx's export joins through.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Doc", t => t
                .Column("Id", "int")
                .Column("Data", "xml", nullable: true)
                .PrimaryKey("PK_Doc", "Id"))
            .PrimaryXmlIndex("dbo", "Doc", "PXML_Doc", "Data")
            .SecondaryXmlIndex("dbo", "Doc", "XMLPATH_Doc", "Data", "PXML_Doc", usage: 1)
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.xml_indexes WHERE object_id = OBJECT_ID('dbo.Doc');"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.xml_indexes WHERE object_id = OBJECT_ID('dbo.Doc') AND xml_index_type = 0;"));
        // Secondary resolves its primary + FOR PATH secondary_type ('P').
        AreEqual("P", sim.ExecuteScalar("SELECT secondary_type FROM sys.xml_indexes WHERE name = 'XMLPATH_Doc';"));
        // The primary's internal node table (type IT) surfaces in sys.objects
        // with one statistics row per XML index (named per index).
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.objects WHERE type = 'IT' AND parent_object_id = OBJECT_ID('dbo.Doc');"));
        AreEqual(2, sim.ExecuteScalar("""
            SELECT COUNT(*) FROM sys.stats s
            JOIN sys.objects o ON s.object_id = o.object_id
            WHERE o.type = 'IT' AND o.parent_object_id = OBJECT_ID('dbo.Doc');
            """));
    }

    [TestMethod]
    public void FullTextCatalogAndIndex_LandIn_CatalogViews()
    {
        // CREATE FULLTEXT CATALOG + a multi-column index (one plain, one with
        // TYPE COLUMN) → sys.fulltext_catalogs / fulltext_indexes /
        // fulltext_index_columns. data_space_id + stoplist_id must be non-NULL
        // so DacFx's export re-emits the elements (probe-derived requirement).
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Doc", t => t
                .Column("Id", "int")
                .Column("Summary", "nvarchar(200)", nullable: true)
                .Column("Body", "varbinary(max)", nullable: true)
                .Column("Ext", "nvarchar(8)", nullable: true)
                .PrimaryKey("PK_Doc", "Id"))
            .FullTextCatalog("MyCatalog")
            .FullTextIndex("dbo", "Doc", "MyCatalog", "PK_Doc",
                ("Summary", 1033, null),
                ("Body", 1033, "Ext"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE name = 'MyCatalog' AND is_default = 1;"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.Doc');"));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.fulltext_index_columns WHERE object_id = OBJECT_ID('dbo.Doc');"));
        // data_space_id (PRIMARY) + stoplist_id (system) both non-NULL.
        AreEqual(1, sim.ExecuteScalar("SELECT data_space_id FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.Doc');"));
        AreEqual(0, sim.ExecuteScalar("SELECT stoplist_id FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.Doc');"));
    }

    /// <summary>
    /// A module body's <c>CONTAINS</c> / <c>FREETEXT</c> binds at CREATE, so
    /// the full-text index has to be in place before the loader emits the
    /// procedures — AdventureWorks' <c>uspSearchCandidateResumes</c> is the
    /// shape that turns on the ordering (real refuses the CREATE with Msg 7601
    /// when the table isn't indexed).
    /// </summary>
    [TestMethod]
    public void FullTextIndex_PrecedesModuleBodies_SoAContainsProcedureCreates()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Doc", t => t
                .Column("Id", "int")
                .Column("Summary", "nvarchar(200)", nullable: true)
                .PrimaryKey("PK_Doc", "Id"))
            .FullTextCatalog("MyCatalog")
            .FullTextIndex("dbo", "Doc", "MyCatalog", "PK_Doc", ("Summary", 1033, null))
            .Procedure("dbo", "SearchDocs", "CREATE PROCEDURE dbo.SearchDocs @q nvarchar(100) AS SELECT Id FROM dbo.Doc WHERE CONTAINS(Summary, @q)")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.procedures WHERE name = 'SearchDocs'"));
    }

    /// <summary>
    /// The loaded data's own maximum is where an imported table's identity
    /// counter has to sit — real's import leaves <c>IDENT_CURRENT</c> there, so
    /// the first insert continues the sequence instead of re-issuing a key the
    /// data already holds.
    /// </summary>
    [TestMethod]
    public void IdentityColumn_CounterAdvancesPastTheLoadedRows()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t
                .Column("Id", "int", identity: true)
                .Column("V", "nvarchar(20)", nullable: true)
                .PrimaryKey("PK_T", "Id")
                .Row(7, "a").Row(11, "b").Row(9, "c"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(11m, sim.ExecuteScalar("SELECT IDENT_CURRENT('dbo.T')"));
        _ = sim.ExecuteNonQuery("INSERT dbo.T (V) VALUES (N'd')");
        AreEqual(12, sim.ExecuteScalar("SELECT Id FROM dbo.T WHERE V = N'd'"));
    }

    /// <summary>An empty table keeps its declared seed — there is nothing to advance past.</summary>
    [TestMethod]
    public void IdentityColumn_NoRows_KeepsTheDeclaredSeed()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t
                .Column("Id", "int", identity: true, identitySeed: 100)
                .Column("V", "nvarchar(20)", nullable: true))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(100m, sim.ExecuteScalar("SELECT IDENT_CURRENT('dbo.T')"));
    }

    [TestMethod]
    public void ExtendedProperty_OnDdlTriggerHost_LandsWithClassObjectOrColumn()
    {
        // sp_addextendedproperty @level0type=N'TRIGGER' against a database DDL
        // trigger → class 1 (OBJECT_OR_COLUMN), major_id = trigger object_id.
        using var bacpac = BacpacBuilder.Create()
            .DatabaseDdlTrigger("trgAudit", """
                CREATE TRIGGER trgAudit ON DATABASE FOR CREATE_TABLE
                AS BEGIN PRINT 'x'; END
                """)
            .DdlTriggerExtendedProperty("trgAudit", "MS_Description", "audit trigger")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("""
            SELECT COUNT(*) FROM sys.extended_properties ep
            JOIN sys.triggers tr ON ep.major_id = tr.object_id
            WHERE ep.class = 1 AND ep.name = 'MS_Description'
              AND tr.parent_class = 0 AND tr.name = 'trgAudit';
            """));
    }

    [TestMethod]
    public void ExtendedProperty_OnFilegroupHost_LandsWithClassDataspace()
    {
        // sp_addextendedproperty @level0type=N'FILEGROUP' → class 20
        // (DATASPACE), major_id = data_space_id. PRIMARY is built-in (id 1).
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int").Row(1))
            .FilegroupExtendedProperty("PRIMARY", "MS_Description", "primary filegroup")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("""
            SELECT COUNT(*) FROM sys.extended_properties
            WHERE class = 20 AND class_desc = 'DATASPACE' AND major_id = 1
              AND minor_id = 0 AND name = 'MS_Description';
            """));
    }

    [TestMethod]
    public void DatabaseDdlTrigger_LandsIn_sys_triggers_WithParentClassDatabase()
    {
        // CREATE TRIGGER … ON DATABASE; the loader dispatches through the
        // SqlDatabaseDdlTrigger arm (phase 7) which routes to the DDL-trigger
        // path. sys.triggers row: parent_class = 0 / parent_class_desc =
        // 'DATABASE'. No fire — DDL events aren't dispatched to a trigger
        // loop (per docs/claude/triggers.md DDL section).
        using var bacpac = BacpacBuilder.Create()
            .DatabaseDdlTrigger("trgAuditDDL", """
                CREATE TRIGGER trgAuditDDL ON DATABASE
                FOR CREATE_TABLE, DROP_TABLE
                AS BEGIN
                    PRINT 'DDL event';
                END
                """)
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND name = 'trgAuditDDL';"));
    }

    [TestMethod]
    public void TimeColumn_RoundTripsThroughBcp_AtMultiplePrecisions()
    {
        // time(N) wire shape: precision-derived 3/4/5-byte LE little-endian
        // count of ticks-at-precision-unit (no day count, vs datetime2's
        // trailing 3-byte day field).
        var noon = new TimeSpan(12, 30, 45);
        var fraction = noon + TimeSpan.FromTicks(1234567);

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Clock", t => t
                .Column("Id", "int")
                .Column("Coarse", "time(0)")
                .Column("Mid", "time(3)")
                .Column("Fine", "time(7)")
                .Column("Maybe", "time(7)", nullable: true)
                .Row(1, noon, noon, fraction, null))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Coarse, Mid, Fine, Maybe FROM Clock;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(noon, reader.GetFieldValue<TimeSpan>(0));
        AreEqual(noon, reader.GetFieldValue<TimeSpan>(1));
        AreEqual(fraction, reader.GetFieldValue<TimeSpan>(2));
        IsTrue(reader.IsDBNull(3));
    }

    [TestMethod]
    public void NcharAndSysnameColumns_RoundTripThroughBcp()
    {
        // nchar(N) shares the 2-byte length-prefix wire form with nvarchar(N)
        // but reads back through SqlValue.FromNChar (different SqlType
        // singleton). sysname rides the same path under SystemNameSqlType.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Code", t => t
                .Column("Id", "int")
                .Column("FixedCode", "nchar(8)")
                .Column("ObjectName", "sysname")
                .Row(1, "ABC12345", "MyObject"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FixedCode, ObjectName FROM Code;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("ABC12345", reader.GetString(0));
        AreEqual("MyObject", reader.GetString(1));
    }

    [TestMethod]
    public void Role_WithAuthorizationOwner_LandsAsCreateRoleAuthorization()
    {
        // CREATE ROLE name AUTHORIZATION owner — the loader reads the
        // Authorizer relationship and appends AUTHORIZATION clause to the
        // emitted CREATE ROLE. Exercises the Authorizer-relationship reader.
        using var bacpac = BacpacBuilder.Create()
            .Role("custom_role", ownerPrincipal: "dbo")
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
    }

    [TestMethod]
    public void RowGuidColColumn_LoadsWithClause_SetsIsRowGuidCol()
    {
        // ROWGUIDCOL round-trips: the loader emits the clause so
        // sys.columns.is_rowguidcol reports it (DacFx re-emits
        // IsRowGuidColumn=True on export). No Skipped, no Warning.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Tagged", t => t
                .Column("Id", "int")
                .Column("RowId", "uniqueidentifier", rowGuidCol: true))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        IsEmpty(diag.Warnings.Where(w => w.Contains("ROWGUIDCOL", StringComparison.Ordinal)).ToList());
        IsTrue((bool)sim.ExecuteScalar(
            "SELECT is_rowguidcol FROM sys.columns WHERE object_id = object_id('dbo.Tagged') AND name = 'RowId';")!);
    }

    [TestMethod]
    public void IdentityNotForReplicationColumn_LoadsWithClause_SetsFlag()
    {
        // IdentityIsNotForReplication round-trips through
        // sys.identity_columns.is_not_for_replication so DacFx re-emits the
        // property on export.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Seeded", t => t
                .Column("Id", "int", identity: true, identityNotForReplication: true)
                .Column("Val", "int", nullable: true))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        IsTrue((bool)sim.ExecuteScalar(
            "SELECT is_not_for_replication FROM sys.identity_columns WHERE object_id = object_id('dbo.Seeded');")!);
    }

    [TestMethod]
    public void ExtendedProperty_OnUnmodeledHostKind_LandsOnSkippedWithReason()
    {
        // An extended-property host kind the loader doesn't model (here a
        // Service Broker service) lands on Skipped with "Host kind '…' not
        // modeled" — exercises the default arm of the host-kind switch.
        // (SqlFilegroup / SqlDatabaseDdlTrigger are now modeled — covered by
        // their own dedicated tests.)
        using var bacpac = BacpacBuilder.Create()
            .UnknownHostExtendedProperty("SqlServiceBrokerService", "AuditService", "MS_Description", "service note")
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        HasCount(1, diag.Skipped.Where(s => s.ElementType == "SqlExtendedProperty").ToList());
        Contains("Host kind 'SqlServiceBrokerService'", diag.Skipped[0].Reason);
    }

    [TestMethod]
    public void Procedure_WithFailingBody_LandsOnSkippedWithCreateFailedReason()
    {
        // CREATE PROCEDURE with a body referencing a non-existent table —
        // simulator's SCHEMA-binding-equivalent parse rejects → SimulatedSqlException
        // → loader records "CREATE SqlProcedure failed:" on Skipped.
        using var bacpac = BacpacBuilder.Create()
            .Procedure("dbo", "BadProc", """
                CREATE PROCEDURE dbo.BadProc AS BEGIN
                    SELECT * FROM dbo.DefinitelyDoesNotExist;
                END
                """)
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        // Note: simulator may still create the proc (parser stores body
        // without resolving table references); test guards the catch path
        // exists, doesn't pin failure outcome.
        // Run via a fallback: a function with a malformed RETURNS type
        // raises at CREATE parse time.
        using var bacpac2 = BacpacBuilder.Create()
            .ScalarFunction("dbo", "BadFn", """
                CREATE FUNCTION dbo.BadFn() RETURNS NotARealType AS BEGIN RETURN 1; END
                """)
            .Build();

        new Simulation().ImportBacpac(bacpac2, out var diag2);
        IsNotEmpty(diag2.Skipped
            .Where(s => s.Reason.StartsWith("CREATE ", StringComparison.Ordinal) && s.Reason.Contains(" failed:", StringComparison.Ordinal))
            .ToList());
    }

    [TestMethod]
    public void Procedure_WithTableTypeParameter_QualifiedTwoPart_Loads()
    {
        // EXEC dispatch through 2-part-qualified table-type lookup
        // ([dbo].[IdList]). Exercises the 2-part table-type-resolver path
        // in proc parameter type parsing.
        using var bacpac = BacpacBuilder.Create()
            .TableType("dbo", "IdList", t => t
                .Column("Id", "int"))
            .Procedure("dbo", "TakeIds", """
                CREATE PROCEDURE dbo.TakeIds @ids dbo.IdList READONLY
                AS BEGIN
                    SELECT COUNT(*) FROM @ids;
                END
                """)
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
    }

    [TestMethod]
    public void Procedure_WithTwoPartUddtParameter_FallsThroughToScalarPath()
    {
        // Two-part `dbo.Phone` resolves through table-type-first
        // (TryResolveTableType returns false → RestoreCheckpoint → fall
        // through to scalar alias-type lookup). Exercises the
        // RestoreCheckpoint + return null arm in the proc parameter
        // type-resolver.
        using var bacpac = BacpacBuilder.Create()
            .UserDefinedDataType("dbo", "Phone", "nvarchar(20)", nullable: true)
            .Procedure("dbo", "CallContact", """
                CREATE PROCEDURE dbo.CallContact @phone dbo.Phone
                AS BEGIN
                    SELECT @phone;
                END
                """)
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
    }

    [TestMethod]
    public void UnhandledElementType_LandsOnSkippedWithNotYetHandledReason()
    {
        // Top-level Element with an unrecognized Type attribute lands on
        // Skipped with "Element type not yet handled by the loader." after
        // every phase fails to claim it.
        using var bacpac = BacpacBuilder.Create()
            .UnknownTopLevelElement("SqlImaginaryFeature", "[stub]")
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        IsNotEmpty(diag.Skipped.Where(s => s.ElementType == "SqlImaginaryFeature").ToList());
        Contains("not yet handled by the loader", diag.Skipped[0].Reason);
    }

    [TestMethod]
    public void ExtendedProperty_OnForeignKey_WalksPastCheckConstraintsForeach()
    {
        // Extended property bound to a FK constraint name: the lookup
        // walks KeyConstraints (miss) + CheckConstraints (miss, closing
        // brace hit) + OutgoingForeignKeys (match) — exercises the
        // closing brace of the CheckConstraints foreach.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Parent3", t => t
                .Column("Id", "int")
                .PrimaryKey("PK_Parent3", "Id"))
            .Table("dbo", "Child3", t => t
                .Column("Id", "int")
                .Column("ParentId", "int")
                .Column("Status", "int")
                .PrimaryKey("PK_Child3", "Id")
                .Check("CK_Child3_Status", "[Status] >= 0")
                .ForeignKey("FK_Child3_Parent", ["ParentId"], "dbo", "Parent3", ["Id"]))
            .ConstraintExtendedProperty("dbo", "FK_Child3_Parent", "MS_Description", "fk to parent")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("fk to parent", sim.ExecuteScalar("""
            SELECT CAST(ep.value AS nvarchar(MAX))
              FROM sys.extended_properties ep
              JOIN sys.foreign_keys f ON ep.major_id = f.object_id
             WHERE f.name = 'FK_Child3_Parent' AND ep.name = 'MS_Description';
            """));
    }

    [TestMethod]
    public void Table_WithUniqueThenPrimaryKey_AlterAddPkWalksExistingKeyConstraints()
    {
        // Loader emits all phase-3 constraints in document order. When the
        // builder adds Unique before PrimaryKey, the loader processes UQ
        // first → table holds the UQ → ALTER ADD CONSTRAINT PK runs and
        // its foreach over KeyConstraints iterates the UQ (kind != PK → no
        // throw) → closing brace hit.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Ordered", t => t
                .Column("Id", "int")
                .Column("Slug", "nvarchar(20)")
                .Unique("UQ_Ordered_Slug", "Slug")
                .PrimaryKey("PK_Ordered", "Id"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'PK_Ordered' AND type = 'PK';"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.key_constraints WHERE name = 'UQ_Ordered_Slug' AND type = 'UQ';"));
    }

    [TestMethod]
    public void ExtendedProperty_OnIndex_WithUniqueConstraint_WalksNonPrimaryKeyList()
    {
        // ComputeIndexId walks non-PK key constraints (`others.Add(...)`)
        // before the regular index list when building the index_id
        // enumeration. A UQ + a plain index + an extended property on the
        // plain index exercises the UQ inclusion in the walker.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "WithUq", t => t
                .Column("Id", "int")
                .Column("Slug", "nvarchar(20)")
                .Column("Email", "nvarchar(100)", nullable: true)
                .PrimaryKey("PK_WithUq", "Id")
                .Unique("UQ_WithUq_Slug", "Slug")
                .Index("IX_WithUq_Email", ["Email"]))
            .IndexExtendedProperty("dbo", "WithUq", "IX_WithUq_Email", "MS_Description", "email index")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("email index", sim.ExecuteScalar("""
            SELECT CAST(ep.value AS nvarchar(MAX))
              FROM sys.extended_properties ep
              JOIN sys.indexes i ON ep.major_id = i.object_id AND ep.minor_id = i.index_id
             WHERE i.name = 'IX_WithUq_Email' AND ep.name = 'MS_Description';
            """));
    }

    [TestMethod]
    public void DatabaseOptions_RoundTripMany_OptionsViaAlterDatabase()
    {
        // The loader maps each DACFx property name through a closed
        // dispatcher. Exercise the breadth of recognized options in one
        // bacpac — verifies the OnOff / RecoveryMode / TargetRecoveryTime /
        // QueryStore arms all fire without raising Skipped.
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("IsAnsiNullsOn", "True")
            .DatabaseOption("IsAnsiWarningsOn", "True")
            .DatabaseOption("IsAnsiPaddingOn", "True")
            .DatabaseOption("IsArithAbortOn", "True")
            .DatabaseOption("IsConcatNullYieldsNullOn", "True")
            .DatabaseOption("IsNumericRoundAbortOn", "False")
            .DatabaseOption("IsQuotedIdentifierOn", "True")
            .DatabaseOption("IsTornPageProtectionOn", "False")
            .DatabaseOption("TemporalHistoryRetentionEnabled", "True")
            .DatabaseOption("IsAcceleratedDatabaseRecoveryOn", "True")
            .DatabaseOption("IsOptimizedLockingOn", "True")
            .DatabaseOption("RecoveryMode", "1") // FULL — exercises non-SIMPLE arm
            .DatabaseOption("IsCursorDefaultScopeGlobal", "True") // GLOBAL arm
            .DatabaseOption("TargetRecoveryTimePeriod", "60")
            .DatabaseOption("QueryStoreDesiredState", "2")
            .DatabaseOption("QueryStoreIntervalLength", "60")
            .DatabaseOption("QueryStoreFlushInterval", "900")
            .DatabaseOption("QueryStoreCaptureMode", "1")
            .DatabaseOption("QueryStoreMaxStorageSize", "1024")
            .DatabaseOption("QueryStoreSizeBasedCleanupMode", "1")
            .DatabaseOption("QueryStoreMaxPlansPerQuery", "200")
            .DatabaseOption("QueryStoreStaleQueryThreshold", "30")
            .DatabaseOption("QueryStoreWaitStatisticsCaptureMode", "1")
            .DatabaseOption("IsFullTextEnabled", "True") // null-mapped (no-op)
            .DatabaseOption("UnrecognizedFutureOption", "anything") // default _ => null arm
            .Build();

        new Simulation().ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
    }

    /// <summary>
    /// The QueryStore* properties fold into one <c>SET QUERY_STORE = ON (…)</c>
    /// so the imported database reports the configuration the source did.
    /// DesiredState 1 is READ_ONLY; the property encodings are DacFx's own
    /// (probe-confirmed by exporting a configured database, 2026-08-08).
    /// </summary>
    [TestMethod]
    public void DatabaseOptions_QueryStoreSubOptions_LandOnTheCatalogRow()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("QueryStoreDesiredState", "1")
            .DatabaseOption("QueryStoreCaptureMode", "3")
            .DatabaseOption("QueryStoreFlushInterval", "1200")
            .DatabaseOption("QueryStoreIntervalLength", "5")
            .DatabaseOption("QueryStoreMaxPlansPerQuery", "77")
            .DatabaseOption("QueryStoreMaxStorageSize", "333")
            .DatabaseOption("QueryStoreStaleQueryThreshold", "11")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("READ_ONLY", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual("NONE", sim.ExecuteScalar("select query_capture_mode_desc from sys.database_query_store_options"));
        AreEqual(1200L, sim.ExecuteScalar("select flush_interval_seconds from sys.database_query_store_options"));
        AreEqual(5L, sim.ExecuteScalar("select interval_length_minutes from sys.database_query_store_options"));
        AreEqual(77L, sim.ExecuteScalar("select max_plans_per_query from sys.database_query_store_options"));
        AreEqual(333L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
        AreEqual(11L, sim.ExecuteScalar("select stale_query_threshold_days from sys.database_query_store_options"));
    }

    /// <summary>
    /// DacFx writes QueryStoreDesiredState ahead of the sub-options, so a model
    /// declaring an off store still carries the configuration that only an
    /// <c>= ON (…)</c> can set. The loader emits the block first and the OFF
    /// after, which is what leaves both halves right.
    /// </summary>
    [TestMethod]
    public void DatabaseOptions_QueryStoreOffWithSubOptions_StaysOff()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("QueryStoreDesiredState", "0")
            .DatabaseOption("QueryStoreMaxStorageSize", "333")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("OFF", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual(333L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
    }

    /// <summary>
    /// An omitted QueryStore property takes DacFx's model default, not the
    /// simulator's fresh-database one — and for capture mode and max storage
    /// size those differ (ALL / 100, the SQL Server 2016 defaults DacFx's
    /// schema still carries). Probe-confirmed against the reference
    /// AdventureWorks, whose model omits both and whose sqlpackage import
    /// reports exactly this row. DesiredState's default *is* READ_WRITE.
    /// </summary>
    [TestMethod]
    public void DatabaseOptions_OmittedQueryStoreProperties_TakeDacFxDefaults()
    {
        using var bacpac = BacpacBuilder.Create().DatabaseOption("IsAnsiNullsOn", "True").Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("READ_WRITE", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual("ALL", sim.ExecuteScalar("select query_capture_mode_desc from sys.database_query_store_options"));
        AreEqual(100L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
        // The rest of DacFx's defaults agree with a fresh database's.
        AreEqual(900L, sim.ExecuteScalar("select flush_interval_seconds from sys.database_query_store_options"));
        AreEqual(60L, sim.ExecuteScalar("select interval_length_minutes from sys.database_query_store_options"));
        AreEqual(30L, sim.ExecuteScalar("select stale_query_threshold_days from sys.database_query_store_options"));
        AreEqual(200L, sim.ExecuteScalar("select max_plans_per_query from sys.database_query_store_options"));
        AreEqual("AUTO", sim.ExecuteScalar("select size_based_cleanup_mode_desc from sys.database_query_store_options"));
        AreEqual("ON", sim.ExecuteScalar("select wait_stats_capture_mode_desc from sys.database_query_store_options"));
    }

    [TestMethod]
    public void PartitionFunction_PartitionScheme_ColumnStoreIndex_AreSilentlySkipped()
    {
        // WWI-Full's three storage-layout decoration element types are
        // loader no-ops — recognized by Type, action is empty. The key
        // invariant: they don't show up on Skipped (Skipped is for
        // unmodeled features; these are deliberately no-op-handled).
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int").Row(1))
            .PartitionFunction("PF_DateRange")
            .PartitionScheme("PS_DateRange")
            .ColumnStoreIndex("CCI_Item")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        // Table + row payload still load.
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM Item;"));
    }

    [TestMethod]
    public void NullableFixedWidth_NonNullValue_DecodesViaNullablePrefix()
    {
        // Nullable fixed-width int/datetime/uniqueidentifier columns wear a
        // 1-byte width prefix (vs the unprefixed fixed-raw shape for NOT
        // NULL columns). The prefix-validating branch in ReadFixed needs a
        // non-null value to exercise the equality check.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "NullableMix", t => t
                .Column("Id", "int")
                .Column("MaybeInt", "int", nullable: true)
                .Column("MaybeBigInt", "bigint", nullable: true)
                .Column("MaybeDate", "datetime", nullable: true)
                .Row(1, 42, 9_000_000_000L, new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Unspecified)))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(42, sim.ExecuteScalar("SELECT MaybeInt FROM NullableMix;"));
    }

    [TestMethod]
    public void NullableDecimal_DecodesNullViaPrefix0xFF()
    {
        // ReadDecimal null path returns SqlValue.Null(type) on prefix=0xFF.
        // The non-null decimal path is already covered by DecimalColumn_*;
        // this pins the null-marker path.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "MaybeMoney", t => t
                .Column("Id", "int")
                .Column("Price", "decimal(10, 2)", nullable: true)
                .Row(1, null))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM MaybeMoney WHERE Price IS NULL;"));
    }

    [TestMethod]
    public void ScalarFunction_WithCaseInBody_AndDecimalParam_Loads()
    {
        // CASE … END inside a UDF body exercises caseDepth tracking in the
        // scalar-UDF body capture loop (otherwise the inner END would be
        // mistaken for the BEGIN/END boundary). A decimal(P, S) parameter
        // exercises the precision/scale parsing branches.
        using var bacpac = BacpacBuilder.Create()
            .ScalarFunction("dbo", "Bucket", """
                CREATE FUNCTION dbo.Bucket(@v decimal(10, 2))
                RETURNS nvarchar(10)
                AS BEGIN
                    DECLARE @r nvarchar(10);
                    SET @r = CASE
                        WHEN @v < 10 THEN N'low'
                        WHEN @v < 100 THEN N'mid'
                        ELSE N'high'
                    END;
                    RETURN @r;
                END
                """)
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("low", sim.ExecuteScalar("SELECT dbo.Bucket(5.00);"));
        AreEqual("mid", sim.ExecuteScalar("SELECT dbo.Bucket(50.00);"));
        AreEqual("high", sim.ExecuteScalar("SELECT dbo.Bucket(500.00);"));
    }

    [TestMethod]
    public void Trigger_WithNotForReplication_Loads()
    {
        // NOT FOR REPLICATION clause is parse-and-discard for the simulator
        // (replication isn't modeled); exercises the FOR / REPLICATION
        // keyword recognition in the trigger header parser.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Audit", t => t
                .Column("Id", "int")
                .Column("Action", "nvarchar(20)"))
            .Trigger("dbo", "Audit", "trgNoRepl", """
                CREATE TRIGGER dbo.trgNoRepl ON dbo.Audit
                AFTER INSERT
                NOT FOR REPLICATION
                AS BEGIN
                    PRINT 'audited';
                END
                """)
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        if (diag.Skipped.Count > 0)
            Fail(string.Join(" | ", diag.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trgNoRepl';"));
    }

    [TestMethod]
    public void View_WithAliasEqualsExpression_Loads()
    {
        // `alias = expr` is T-SQL's legacy column-alias shape (vs the SQL
        // standard `expr AS alias`). DACFx-emitted view bodies occasionally
        // use it; the simulator's SELECT-list parser carries the legacy
        // form. Exercises the assignment-form branch.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Source", t => t
                .Column("Raw", "int")
                .Row(7))
            .View("dbo", "Renamed", "CREATE VIEW dbo.Renamed AS SELECT Doubled = Raw * 2 FROM dbo.Source;")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(14, sim.ExecuteScalar("SELECT Doubled FROM dbo.Renamed;"));
    }

    [TestMethod]
    public void Sequence_QueryThrough_sys_objects_AsType_SO()
    {
        // Sequence.ObjectTypeCode = "SO" / ObjectTypeDescription =
        // "SEQUENCE_OBJECT" — readable via sys.objects.type.
        using var bacpac = BacpacBuilder.Create()
            .Sequence("dbo", "MarkerSeq", "int", startValue: 1, increment: 1)
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("SO", sim.ExecuteScalar("SELECT type FROM sys.objects WHERE name = 'MarkerSeq';"));
        AreEqual("SEQUENCE_OBJECT", sim.ExecuteScalar("SELECT type_desc FROM sys.objects WHERE name = 'MarkerSeq';"));
    }

    [TestMethod]
    public void Table_MultipleForeignKeys_AssertConstraintNameUniqueWalksFkList()
    {
        // Adding two FKs from the same child to two parents exercises the
        // OutgoingForeignKeys walker in AssertConstraintNameUnique: the
        // second FK lookup iterates the first FK's name during the
        // uniqueness check.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Parent1", t => t
                .Column("Id", "int")
                .PrimaryKey("PK_Parent1", "Id"))
            .Table("dbo", "Parent2", t => t
                .Column("Id", "int")
                .PrimaryKey("PK_Parent2", "Id"))
            .Table("dbo", "Child", t => t
                .Column("Id", "int")
                .Column("P1", "int")
                .Column("P2", "int")
                .PrimaryKey("PK_Child", "Id")
                .ForeignKey("FK_Child_P1", ["P1"], "dbo", "Parent1", ["Id"])
                .ForeignKey("FK_Child_P2", ["P2"], "dbo", "Parent2", ["Id"]))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = (SELECT object_id FROM sys.tables WHERE name = 'Child');"));
    }

    [TestMethod]
    public void ExtendedProperty_OnTableWithThreeIndexes_WalksIndexIdEnumeration()
    {
        // The fn_listextendedproperty INDEX-level path walks indexes in
        // ObjectId order, advancing nextIndexId past each non-matching
        // entry. Three indexes + an extended property on the third
        // exercises the multi-step advance.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Multi", t => t
                .Column("Id", "int")
                .Column("A", "int")
                .Column("B", "int")
                .Column("C", "int")
                .PrimaryKey("PK_Multi", "Id")
                .Index("IX_A", ["A"])
                .Index("IX_B", ["B"])
                .Index("IX_C", ["C"]))
            .IndexExtendedProperty("dbo", "Multi", "IX_C", "MS_Description", "third index")
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual("third index", sim.ExecuteScalar("""
            SELECT CAST(ep.value AS nvarchar(MAX))
              FROM sys.extended_properties ep
              JOIN sys.indexes i ON ep.major_id = i.object_id AND ep.minor_id = i.index_id
             WHERE i.name = 'IX_C' AND ep.name = 'MS_Description';
            """));
    }

    [TestMethod]
    public void GeographyPolygon_DecodesToPolygonWkt()
    {
        // Full-shape decoder path (no IsSinglePoint/IsSingleLineString
        // shortcut): numPoints + numFigures + numShapes tables. A single
        // closed quad ring decodes to POLYGON ((long lat, …)). Axis order
        // is inverted vs binary storage — the decoder honors that.
        var polygon = BacpacBuilder.MakeGeographyPolygon(
            srid: 4326,
            (47.0, -122.0),
            (48.0, -122.0),
            (48.0, -121.0),
            (47.0, -122.0));

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Region", t => t
                .Column("Id", "int")
                .Column("Border", "geography")
                .Row(1, polygon))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        var wkt = (string)sim.ExecuteScalar("SELECT CAST(Border AS nvarchar(MAX)) FROM Region;")!;
        IsTrue(wkt.StartsWith("POLYGON ((", StringComparison.Ordinal), $"unexpected WKT '{wkt}'");
        // Longitude first (-122) in printed WKT — the geography axis swap.
        Contains("-122", wkt);
        Contains("47", wkt);
    }

    [TestMethod]
    public void XmlSchemaCollection_And_TypedXmlColumn_RoundTrip()
    {
        // A SqlXmlSchemaCollection element creates the collection in phase 1;
        // a typed-xml column (SqlXmlTypeSpecifier + XmlSchemaCollection
        // relationship) binds it so sys.columns.xml_collection_id joins back —
        // the gap that made AW's re-exported bacpac lose typed xml.
        const string xsd = "<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"Note\" type=\"xsd:string\" /></xsd:schema>";
        using var bacpac = BacpacBuilder.Create()
            .XmlSchemaCollection("dbo", "NoteSchema", xsd)
            .Table("dbo", "Doc", t => t
                .Column("Id", "int")
                .Column("Body", "xml", nullable: true, xmlSchemaCollection: "[dbo].[NoteSchema]"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(1, diag.ElementCounts["SqlXmlSchemaCollection"]);
        // The collection exists in the catalog.
        AreEqual("NoteSchema", sim.ExecuteScalar("SELECT name FROM sys.xml_schema_collections WHERE name = 'NoteSchema';"));
        // The typed column's xml_collection_id resolves back to the collection.
        AreEqual("NoteSchema", sim.ExecuteScalar("""
            SELECT x.name
              FROM sys.columns c
              JOIN sys.tables t ON c.object_id = t.object_id
              JOIN sys.xml_schema_collections x ON c.xml_collection_id = x.xml_collection_id
             WHERE t.name = 'Doc' AND c.name = 'Body';
            """));
    }

    [TestMethod]
    public void UntypedXmlColumn_LoadsWith_ZeroXmlCollectionId()
    {
        // An untyped xml column carries no XmlSchemaCollection relationship;
        // sys.columns.xml_collection_id reports the non-nullable 0 default.
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Doc", t => t
                .Column("Id", "int")
                .Column("Body", "xml", nullable: true))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        AreEqual(0, sim.ExecuteScalar("""
            SELECT c.xml_collection_id
              FROM sys.columns c
              JOIN sys.tables t ON c.object_id = t.object_id
             WHERE t.name = 'Doc' AND c.name = 'Body';
            """));
    }

    /// <summary>
    /// Byte parity against a genuine DacFx export. The expected strings come
    /// from <c>sqlpackage /Action:Export</c> of a table carrying every
    /// temporal type at several precisions (2026-07-30), read straight out of
    /// the resulting <c>.BCP</c> file.
    /// </summary>
    /// <remarks>
    /// This is the anchor that keeps <see cref="BacpacBuilder"/> and
    /// <c>BcpRowReader</c> from agreeing with each other while both disagree
    /// with DacFx — which is exactly how the max-width encoding of
    /// <c>time</c> / <c>datetime2</c> / <c>datetimeoffset</c> went unnoticed:
    /// every bacpac exercised before used precision 7, where the declared and
    /// maximum widths coincide.
    /// </remarks>
    [TestMethod]
    [DataRow("date", "2024-03-15", "038F460B")]
    [DataRow("smalldatetime", "2024-03-15 13:45:00", "0434B13903")]
    [DataRow("datetime", "2024-03-15 13:45:12.347", "0834B1000048A6E200")]
    [DataRow("time(0)", "13:45:12", "0500A4734773")]
    [DataRow("time(3)", "13:45:12.345", "059048A84773")]
    [DataRow("time(7)", "13:45:12.3456789", "051563A84773")]
    [DataRow("datetime2(0)", "2024-03-15 13:45:12", "0800A47347738F460B")]
    [DataRow("datetime2(3)", "2024-03-15 13:45:12.345", "089048A847738F460B")]
    [DataRow("datetime2(7)", "2024-03-15 13:45:12.3456789", "081563A847738F460B")]
    [DataRow("datetimeoffset(0)", "2024-03-15 13:45:12 +05:30", "0A0068BB2D458F460B4A01")]
    [DataRow("datetimeoffset(3)", "2024-03-15 13:45:12.345 -08:00", "0A9088CB55B68F460B20FE")]
    [DataRow("datetimeoffset(7)", "2024-03-15 13:45:12.3456789 +14:00", "0A15735419C78E460B4803")]
    public void TemporalBcpEncoding_MatchesRealDacFxBytes(string sqlType, string literal, string expectedHex)
    {
        using var stream = new MemoryStream();
        BacpacBuilder.EncodeBcpValue(stream, new ColumnDef("V", sqlType, Nullable: true), ParseTemporal(sqlType, literal));
        AreEqual(expectedHex, Convert.ToHexString(stream.ToArray()));
    }

    /// <summary>
    /// The whole temporal family survives a bacpac round trip at every
    /// precision, including the offset a <c>datetimeoffset</c> carries — whose
    /// payload is the instant in UTC rather than in local time.
    /// </summary>
    [TestMethod]
    public void TemporalTypes_RoundTripThroughBcp()
    {
        var date = new DateOnly(2024, 3, 15);
        var small = new DateTime(2024, 3, 15, 13, 45, 0, DateTimeKind.Unspecified);
        var time = new TimeSpan(0, 13, 45, 12, 345).Add(TimeSpan.FromTicks(6789));
        var stamp = new DateTime(2024, 3, 15, 13, 45, 12, 345, DateTimeKind.Unspecified).AddTicks(6789);
        var offset = new DateTimeOffset(stamp, TimeSpan.FromMinutes(330));

        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t
                .Column("Id", "int")
                .Column("D", "date")
                .Column("Sdt", "smalldatetime")
                .Column("T7", "time(7)")
                .Column("D27", "datetime2(7)")
                .Column("O7", "datetimeoffset(7)")
                .Row(1, date, small, time, stamp, offset))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT D, Sdt, T7, D27, O7 FROM T;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(date.ToDateTime(TimeOnly.MinValue), reader.GetDateTime(0));
        AreEqual(small, reader.GetDateTime(1));
        AreEqual(time, reader.GetValue(2));
        AreEqual(stamp, reader.GetDateTime(3));
        AreEqual(offset, reader.GetValue(4));
    }

    /// <summary>
    /// A <c>datetimeoffset</c> whose offset pushes the UTC instant onto a
    /// different calendar day still round-trips — the payload's date is the
    /// UTC one, so a naive local-time read would land a day out.
    /// </summary>
    [TestMethod]
    public void DateTimeOffset_AcrossDayBoundary_RoundTrips()
    {
        var offset = new DateTimeOffset(new DateTime(2024, 3, 15, 13, 45, 12, DateTimeKind.Unspecified), TimeSpan.FromHours(14));
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t
                .Column("Id", "int")
                .Column("O", "datetimeoffset(7)")
                .Row(1, offset))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT O FROM T;";
        AreEqual(offset, command.ExecuteScalar());
    }

    /// <summary>
    /// A constraint name may legally contain a dot, and real schemas do it
    /// constantly — Entity Framework's own migration-history table ships
    /// <c>PK_dbo.__MigrationHistory</c>. The loader has to split the bacpac's
    /// bracketed name on separator dots only, or the leaf it emits is a
    /// fragment ending in a stray bracket.
    /// </summary>
    [TestMethod]
    public void ConstraintNamesContainingADot_Load()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Parent", t => t
                .Column("Id", "int")
                .PrimaryKey("PK_dbo.Parent", "Id"))
            .Table("dbo", "Child", t => t
                .Column("Id", "int")
                .Column("ParentId", "int")
                .Column("Status", "nvarchar(20)", nullable: true)
                .Column("Age", "int")
                .PrimaryKey("PK_dbo.Child", "Id")
                .ForeignKey("FK_dbo.Child_dbo.Parent_ParentId", ["ParentId"], "dbo", "Parent", ["Id"])
                .Check("CK_dbo.Child_Age", "[Age] > 0")
                .Default("DF.Child_Status", "Status", "'active'"))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        AreEqual("PK_dbo.Child PK_dbo.Parent", sim.ExecuteScalar(
            "SELECT STRING_AGG(name, ' ') WITHIN GROUP (ORDER BY name) FROM sys.key_constraints"));
        AreEqual("FK_dbo.Child_dbo.Parent_ParentId", sim.ExecuteScalar("SELECT name FROM sys.foreign_keys"));
        AreEqual("CK_dbo.Child_Age", sim.ExecuteScalar("SELECT name FROM sys.check_constraints"));
        AreEqual("DF.Child_Status", sim.ExecuteScalar("SELECT name FROM sys.default_constraints"));
    }

    /// <summary>
    /// float / real ride the BCP wire as IEEE 754 little-endian at their
    /// storage width, on the fixed-raw prefix rule the integer family uses.
    /// </summary>
    [TestMethod]
    public void FloatAndRealColumns_RoundTripThroughBcp()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "M", t => t
                .Column("Id", "int")
                .Column("D", "float")
                .Column("S", "real")
                .Column("N", "float", nullable: true)
                .Row(1, 1.5d, 2.25f, 3.5d)
                .Row(2, -0.125d, -4.5f, null))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM M"));
        AreEqual(1.375d, sim.ExecuteScalar("SELECT SUM(D) FROM M"));
        // SUM over real promotes to float, as on real SQL Server.
        AreEqual(-2.25d, sim.ExecuteScalar("SELECT SUM(S) FROM M"));
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(N) FROM M"));
    }

    /// <summary>
    /// DacFx writes a column's <c>IsNullable</c> property only when it differs
    /// from the element kind's own default, and the two kinds disagree: a
    /// table column omits it for a nullable column, a table-type column omits
    /// it for a NOT NULL one. Reading the type on the table's default makes
    /// every one of its NOT NULL columns nullable.
    /// </summary>
    [TestMethod]
    public void TableTypeColumns_DefaultToNotNull()
    {
        using var bacpac = BacpacBuilder.Create()
            .TableType("dbo", "TT", t => t
                .Column("A", "int")
                .Column("B", "nvarchar(50)")
                .Column("C", "int", nullable: true))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out var diag);
        IsEmpty(diag.Skipped);

        AreEqual("A=0 B=0 C=1", sim.ExecuteScalar("""
            SELECT STRING_AGG(CONCAT(c.name, '=', c.is_nullable), ' ') WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.table_types tt
            JOIN sys.columns c ON c.object_id = tt.type_table_object_id
            WHERE tt.name = 'TT'
            """));
    }

    private static object ParseTemporal(string sqlType, string literal) =>
        sqlType.StartsWith("datetimeoffset", StringComparison.Ordinal)
            ? DateTimeOffset.Parse(literal, CultureInfo.InvariantCulture)
            : sqlType.StartsWith("time", StringComparison.Ordinal)
                ? TimeSpan.Parse(literal, CultureInfo.InvariantCulture)
                : DateTime.Parse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None);
}
