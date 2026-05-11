using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>sys.columns</c> plus the three <c>INFORMATION_SCHEMA</c>
/// views (<c>TABLES</c>, <c>COLUMNS</c>, <c>SCHEMATA</c>). Shapes and
/// per-type metadata values probe-confirmed against SQL Server 2025
/// (2026-05-11). The simulator pins length on its variable-length type
/// singletons (<c>VarcharSqlType(N).length</c> etc.), so the catalog row
/// generators read the metadata straight off the column's <c>SqlType</c>
/// without a parallel length channel.
/// </summary>
[TestClass]
public sealed class InformationSchemaTests
{
    // ---- sys.columns ----

    [TestMethod]
    public void SysColumns_EmptyByDefault()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from sys.columns"));

    [TestMethod]
    public void SysColumns_AfterCreateTable_HasOneRowPerColumn()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table foo (id int, name nvarchar(50), qty decimal(10,2));
            select count(*) from sys.columns where object_id = object_id('foo')
            """));

    [TestMethod]
    public void SysColumns_ColumnIdsAreOneBased()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (a int, b int, c int);
            select name, column_id from sys.columns where object_id = object_id('foo') order by column_id
            """);
        var rows = new List<(string, int)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { ("a", 1), ("b", 2), ("c", 3) }, rows);
    }

    [TestMethod]
    public void SysColumns_IntColumnMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select system_type_id, user_type_id, max_length, [precision], scale, is_nullable, is_identity, is_computed, collation_name from sys.columns where object_id = object_id('foo')
            """);
        IsTrue(reader.Read());
        AreEqual((byte)56, reader.GetByte(0));   // system_type_id for int
        AreEqual(56, reader.GetInt32(1));        // user_type_id
        AreEqual((short)4, reader.GetInt16(2));  // max_length
        AreEqual((byte)10, reader.GetByte(3));   // precision
        AreEqual((byte)0, reader.GetByte(4));    // scale
        IsTrue(reader.GetBoolean(5));            // is_nullable (no NOT NULL)
        IsFalse(reader.GetBoolean(6));           // is_identity
        IsFalse(reader.GetBoolean(7));           // is_computed
        IsTrue(reader.IsDBNull(8));              // collation_name NULL for non-string
    }

    [TestMethod]
    public void SysColumns_NVarcharMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (n nvarchar(50));
            select system_type_id, max_length, collation_name from sys.columns where object_id = object_id('foo')
            """);
        IsTrue(reader.Read());
        AreEqual((byte)231, reader.GetByte(0));      // system_type_id for nvarchar
        AreEqual((short)100, reader.GetInt16(1));    // 2 * 50 — byte length
        AreEqual("SQL_Latin1_General_CP1_CI_AS", reader.GetString(2));
    }

    [TestMethod]
    public void SysColumns_VarcharMaxMetadata()
        => AreEqual((short)-1, (short)new Simulation().ExecuteScalar("""
            create table foo (descr varchar(max));
            select max_length from sys.columns where object_id = object_id('foo')
            """)!);

    [TestMethod]
    public void SysColumns_CharMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (code char(5));
            select system_type_id, max_length from sys.columns where object_id = object_id('foo')
            """);
        IsTrue(reader.Read());
        AreEqual((byte)175, reader.GetByte(0));      // char
        AreEqual((short)5, reader.GetInt16(1));
    }

    [TestMethod]
    public void SysColumns_DecimalMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (qty decimal(10,2));
            select system_type_id, max_length, [precision], scale from sys.columns where object_id = object_id('foo')
            """);
        IsTrue(reader.Read());
        AreEqual((byte)106, reader.GetByte(0));      // decimal
        AreEqual((short)9, reader.GetInt16(1));      // p<=19 -> 9 bytes
        AreEqual((byte)10, reader.GetByte(2));
        AreEqual((byte)2, reader.GetByte(3));
    }

    [TestMethod]
    public void SysColumns_Datetime2PrecisionMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (a datetime2(0), b datetime2(3), c datetime2(7));
            select name, max_length, [precision], scale from sys.columns where object_id = object_id('foo') order by column_id
            """);
        var rows = new List<(string, short, byte, byte)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt16(1), reader.GetByte(2), reader.GetByte(3)));
        CollectionAssert.AreEqual(
            new[] { ("a", (short)6, (byte)19, (byte)0), ("b", (short)7, (byte)22, (byte)3), ("c", (short)8, (byte)26, (byte)7) },
            rows);
    }

    [TestMethod]
    public void SysColumns_TimePrecisionMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (a time(0), b time(2), c time(7));
            select max_length, [precision], scale from sys.columns where object_id = object_id('foo') order by column_id
            """);
        var rows = new List<(short, byte, byte)>();
        while (reader.Read()) rows.Add((reader.GetInt16(0), reader.GetByte(1), reader.GetByte(2)));
        CollectionAssert.AreEqual(
            new[] { ((short)3, (byte)8, (byte)0), ((short)3, (byte)10, (byte)2), ((short)5, (byte)15, (byte)7) },
            rows);
    }

    [TestMethod]
    public void SysColumns_UniqueIdentifierMetadata()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (g uniqueidentifier);
            select system_type_id, max_length, [precision], scale from sys.columns where object_id = object_id('foo')
            """);
        IsTrue(reader.Read());
        AreEqual((byte)36, reader.GetByte(0));
        AreEqual((short)16, reader.GetInt16(1));
        AreEqual((byte)0, reader.GetByte(2));
        AreEqual((byte)0, reader.GetByte(3));
    }

    [TestMethod]
    public void SysColumns_NotNull_IsNullableFalse()
        => IsFalse((bool)new Simulation().ExecuteScalar("""
            create table foo (id int not null);
            select is_nullable from sys.columns where object_id = object_id('foo')
            """)!);

    [TestMethod]
    public void SysColumns_Identity_IsIdentityTrue()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table foo (id int identity primary key);
            select is_identity from sys.columns where object_id = object_id('foo') and name = 'id'
            """)!);

    [TestMethod]
    public void SysColumns_Computed_IsComputedTrue()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table foo (id int, dbl as id * 2);
            select is_computed from sys.columns where object_id = object_id('foo') and name = 'dbl'
            """)!);

    [TestMethod]
    public void SysColumns_DropTableRemovesRows()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table foo (id int, name nvarchar(50));
            drop table foo;
            select count(*) from sys.columns where name in ('id', 'name')
            """));

    [TestMethod]
    public void SysColumns_JoinSysTablesByObjectId()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int, name nvarchar(20));
            select t.name as table_name, c.name as col_name from sys.tables t inner join sys.columns c on c.object_id = t.object_id where t.name = 'foo' order by c.column_id
            """);
        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { ("foo", "id"), ("foo", "name") }, rows);
    }

    // ---- INFORMATION_SCHEMA.TABLES ----

    [TestMethod]
    public void IsTables_EmptyByDefault()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from INFORMATION_SCHEMA.TABLES"));

    [TestMethod]
    public void IsTables_RowShape()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE from INFORMATION_SCHEMA.TABLES where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual("simulated", reader.GetString(0));
        AreEqual("dbo", reader.GetString(1));
        AreEqual("foo", reader.GetString(2));
        AreEqual("BASE TABLE", reader.GetString(3));
    }

    [TestMethod]
    public void IsTables_UserSchemaSurfaces()
    {
        using var reader = new Simulation().ExecuteReader("""
            create schema audit;
            create table audit.events (id int);
            select TABLE_SCHEMA, TABLE_NAME from INFORMATION_SCHEMA.TABLES where TABLE_NAME = 'events'
            """);
        IsTrue(reader.Read());
        AreEqual("audit", reader.GetString(0));
        AreEqual("events", reader.GetString(1));
    }

    // ---- INFORMATION_SCHEMA.COLUMNS ----

    [TestMethod]
    public void IsColumns_BasicRow()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int, name nvarchar(50));
            select COLUMN_NAME, ORDINAL_POSITION, IS_NULLABLE, DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo' order by ORDINAL_POSITION
            """);
        var rows = new List<(string Name, int Pos, string Nullable, string Type)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        CollectionAssert.AreEqual(
            new[] { ("id", 1, "YES", "int"), ("name", 2, "YES", "nvarchar") },
            rows);
    }

    [TestMethod]
    public void IsColumns_IsNullableYesNoStrings()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (a int not null, b int null);
            select COLUMN_NAME, IS_NULLABLE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo' order by ORDINAL_POSITION
            """);
        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { ("a", "NO"), ("b", "YES") }, rows);
    }

    [TestMethod]
    public void IsColumns_CharacterMaxAndOctetLength_NvarcharIsCharsVsBytes()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (n nvarchar(50));
            select CHARACTER_MAXIMUM_LENGTH, CHARACTER_OCTET_LENGTH from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual(50, reader.GetInt32(0));    // chars
        AreEqual(100, reader.GetInt32(1));   // bytes
    }

    [TestMethod]
    public void IsColumns_VarcharLengths()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (v varchar(100));
            select CHARACTER_MAXIMUM_LENGTH, CHARACTER_OCTET_LENGTH from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual(100, reader.GetInt32(0));
        AreEqual(100, reader.GetInt32(1));
    }

    [TestMethod]
    public void IsColumns_VarcharMaxLengths_MinusOne()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (v varchar(max));
            select CHARACTER_MAXIMUM_LENGTH, CHARACTER_OCTET_LENGTH from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual(-1, reader.GetInt32(0));
        AreEqual(-1, reader.GetInt32(1));
    }

    [TestMethod]
    public void IsColumns_TextSentinels()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (t text, nt ntext);
            select COLUMN_NAME, CHARACTER_MAXIMUM_LENGTH, CHARACTER_OCTET_LENGTH from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo' order by ORDINAL_POSITION
            """);
        var rows = new List<(string, int, int)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        CollectionAssert.AreEqual(
            new[] { ("t", 2147483647, 2147483647), ("nt", 1073741823, 2147483646) },
            rows);
    }

    [TestMethod]
    public void IsColumns_NumericPrecisionScale_Int()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual((byte)10, reader.GetByte(0));
        AreEqual((short)10, reader.GetInt16(1));
        AreEqual(0, reader.GetInt32(2));
    }

    [TestMethod]
    public void IsColumns_NumericPrecisionScale_Decimal()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (qty decimal(15,4));
            select NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual((byte)15, reader.GetByte(0));
        AreEqual((short)10, reader.GetInt16(1));
        AreEqual(4, reader.GetInt32(2));
    }

    [TestMethod]
    public void IsColumns_FloatHasRadix2AndNullScale()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (v float);
            select NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual((byte)53, reader.GetByte(0));
        AreEqual((short)2, reader.GetInt16(1));
        IsTrue(reader.IsDBNull(2));
    }

    [TestMethod]
    public void IsColumns_BitHasAllNumericNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (b bit);
            select NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsTrue(reader.IsDBNull(1));
        IsTrue(reader.IsDBNull(2));
    }

    [TestMethod]
    public void IsColumns_DateTimePrecision_Datetime2()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (a date, b datetime, c datetime2(3), d datetime2(7), e time(0), f datetimeoffset(5));
            select COLUMN_NAME, DATETIME_PRECISION from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo' order by ORDINAL_POSITION
            """);
        var rows = new List<(string, short)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt16(1)));
        CollectionAssert.AreEqual(
            new[] { ("a", (short)0), ("b", (short)3), ("c", (short)3), ("d", (short)7), ("e", (short)0), ("f", (short)5) },
            rows);
    }

    [TestMethod]
    public void IsColumns_CharacterSetName_Variants()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (a varchar(10), b nvarchar(10), c char(5), d nchar(5), e binary(4), f int);
            select COLUMN_NAME, CHARACTER_SET_NAME from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo' order by ORDINAL_POSITION
            """);
        var rows = new List<(string, string?)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        CollectionAssert.AreEqual(
            new (string, string?)[] { ("a", "iso_1"), ("b", "UNICODE"), ("c", "iso_1"), ("d", "UNICODE"), ("e", null), ("f", null) },
            rows);
    }

    [TestMethod]
    public void IsColumns_CollationName_OnlyOnStrings()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (s varchar(10), i int);
            select COLUMN_NAME, COLLATION_NAME from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo' order by ORDINAL_POSITION
            """);
        var rows = new List<(string, string?)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        CollectionAssert.AreEqual(
            new (string, string?)[] { ("s", "SQL_Latin1_General_CP1_CI_AS"), ("i", null) },
            rows);
    }

    [TestMethod]
    public void IsColumns_ColumnDefault_AlwaysNull_FidelityGap()
    {
        // Real SQL Server renders the default expression as '(getutcdate())'.
        // The simulator returns NULL until expression-to-SQL serialization
        // lands as its own bundle; documented in CLAUDE.md.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (ts datetime2 default getutcdate());
            select COLUMN_DEFAULT from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'foo'
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    // ---- INFORMATION_SCHEMA.SCHEMATA ----

    [TestMethod]
    public void IsSchemata_ListsBuiltInSchemas()
    {
        using var reader = new Simulation().ExecuteReader(
            "select SCHEMA_NAME, SCHEMA_OWNER from INFORMATION_SCHEMA.SCHEMATA order by SCHEMA_NAME");
        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEquivalent(
            new[] { ("dbo", "dbo"), ("INFORMATION_SCHEMA", "INFORMATION_SCHEMA"), ("sys", "sys") },
            rows);
    }

    [TestMethod]
    public void IsSchemata_IncludesUserSchema()
        => AreEqual(4, new Simulation().ExecuteScalar("""
            create schema audit;
            select count(*) from INFORMATION_SCHEMA.SCHEMATA
            """));

    [TestMethod]
    public void IsSchemata_CatalogIsDatabaseName()
    {
        using var reader = new Simulation().ExecuteReader(
            "select CATALOG_NAME from INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = 'dbo'");
        IsTrue(reader.Read());
        AreEqual("simulated", reader.GetString(0));
    }

    [TestMethod]
    public void IsSchemata_DefaultCharsetName_Iso1()
    {
        using var reader = new Simulation().ExecuteReader(
            "select DEFAULT_CHARACTER_SET_NAME from INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = 'dbo'");
        IsTrue(reader.Read());
        AreEqual("iso_1", reader.GetString(0));
    }

    // ---- routing ----

    [TestMethod]
    public void InformationSchema_CaseInsensitiveQualifier()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from information_schema.tables"));

    [TestMethod]
    public void Sys_CaseInsensitiveQualifier()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from SYS.COLUMNS"));

    [TestMethod]
    public void InformationSchema_UnknownView_Msg208()
        => new Simulation().AssertSqlError(
            "select * from INFORMATION_SCHEMA.no_such_view", 208);

    [TestMethod]
    public void InformationSchema_QualifiedFromCurrentDatabase()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from simulated.INFORMATION_SCHEMA.TABLES"));

    [TestMethod]
    public void InformationSchema_WrongDatabaseQualifier_Msg208()
        => new Simulation().AssertSqlError(
            "select count(*) from baddb.INFORMATION_SCHEMA.TABLES", 208);
}
