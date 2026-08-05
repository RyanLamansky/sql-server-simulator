using System.Data.Common;
using System.Data.SqlTypes;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public sealed class DataReaderTests
{
    private static DbDataReader OpenReader(string sql) => new Simulation().ExecuteReader(sql);

    /// <summary>
    /// The peek-and-buffer <c>HasRows</c> shape is written twice — once per
    /// cursor kind — and an ordinary SELECT only ever reaches the byte-encoded
    /// one. A result set built from already-projected values (the catalog
    /// procedures, the <c>sp_help</c> family) takes the other, so it needs its
    /// own pass through peek-before-read, read-after-peek and the sticky bit.
    /// </summary>
    [TestMethod]
    public void HasRows_OnAValueProjectedResultSet_PeeksAndStaysSticky()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (id int)");
        using var reader = sim.ExecuteReader("exec sp_helpdb");
        IsTrue(reader.HasRows);
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        IsTrue(reader.HasRows);
        while (reader.Read())
        {
        }

        IsTrue(reader.HasRows);
    }

    [TestMethod]
    public void HasRows_OnAnEmptyValueProjectedResultSet_IsFalse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (id int)");
        using var reader = sim.ExecuteReader("exec sp_helptrigger 'dbo.t'");
        IsFalse(reader.HasRows);
        IsFalse(reader.Read());
        IsFalse(reader.HasRows);
    }

    [TestMethod]
    public void HasRows_TrueBeforeAndAfterRead_StickyOnceObserved()
    {
        using var reader = OpenReader("select 1 union all select 2");
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        IsTrue(reader.HasRows);
        IsFalse(reader.Read());
        IsTrue(reader.HasRows);
    }

    [TestMethod]
    public void HasRows_FalseForEmptyResult()
    {
        using var reader = OpenReader("select 1 where 1 = 0");
        IsFalse(reader.HasRows);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void HasRows_PeekDoesNotConsumeFirstRow()
    {
        using var reader = OpenReader("select 42");
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual(42, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void HasRows_AcrossNextResult_BothResultsHaveRows()
    {
        using var reader = OpenReader("select 1; select 2");
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void HasRows_AcrossNextResult_MixedEmptyAndNonEmpty()
    {
        using var reader = OpenReader("select 1; select 2 where 1 = 0; select 3");
        IsTrue(reader.HasRows);
        IsTrue(reader.NextResult());
        IsFalse(reader.HasRows);
        IsFalse(reader.Read());
        IsTrue(reader.NextResult());
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
    }

    [TestMethod]
    public void IsClosed_FalseInitially_TrueAfterDispose()
    {
        var reader = OpenReader("select 1");
        IsFalse(reader.IsClosed);
        reader.Dispose();
        IsTrue(reader.IsClosed);
    }

    [TestMethod]
    public void Depth_AlwaysZero()
    {
        using var reader = OpenReader("select 1");
        AreEqual(0, reader.Depth);
        IsTrue(reader.Read());
        AreEqual(0, reader.Depth);
    }

    [TestMethod]
    public void GetByte_ReturnsTinyIntValue()
    {
        using var reader = OpenReader("select cast(200 as tinyint)");
        IsTrue(reader.Read());
        AreEqual((byte)200, reader.GetByte(0));
    }

    [TestMethod]
    public void GetInt64_ReturnsBigIntValue()
    {
        using var reader = OpenReader("select cast(9000000000 as bigint)");
        IsTrue(reader.Read());
        AreEqual(9_000_000_000L, reader.GetInt64(0));
    }

    [TestMethod]
    public void GetByte_NullRaisesSqlNullValueException()
    {
        using var reader = OpenReader("select cast(null as tinyint)");
        IsTrue(reader.Read());
        _ = ThrowsExactly<SqlNullValueException>(() => reader.GetByte(0));
    }

    [TestMethod]
    public void GetValues_FillsArrayUpToFieldCount()
    {
        using var reader = OpenReader("select 1, 'two', cast(3.5 as decimal(5,1))");
        IsTrue(reader.Read());
        var values = new object[5];
        AreEqual(3, reader.GetValues(values));
        AreEqual(1, values[0]);
        AreEqual("two", values[1]);
        AreEqual(3.5m, values[2]);
        IsNull(values[3]);
        IsNull(values[4]);
    }

    [TestMethod]
    public void GetValues_TruncatesToShorterBuffer()
    {
        using var reader = OpenReader("select 1, 'two', 3");
        IsTrue(reader.Read());
        var values = new object[2];
        AreEqual(2, reader.GetValues(values));
        AreEqual(1, values[0]);
        AreEqual("two", values[1]);
    }

    [TestMethod]
    public void GetValues_NullArgRaises()
        => ThrowsExactly<ArgumentNullException>(() =>
        {
            using var reader = OpenReader("select 1");
            _ = reader.Read();
            _ = reader.GetValues(null!);
        });

    [TestMethod]
    public void GetEnumerator_IteratesRows()
    {
        using var reader = OpenReader("select 1 union all select 2 union all select 3");
        var values = new List<int>();
        foreach (DbDataRecord record in reader)
            values.Add(record.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
    }

    [TestMethod]
    [DataRow("cast(1 as int)", "int")]
    [DataRow("cast(1 as bigint)", "bigint")]
    [DataRow("cast(1 as smallint)", "smallint")]
    [DataRow("cast(1 as tinyint)", "tinyint")]
    [DataRow("cast(1 as bit)", "bit")]
    [DataRow("cast(1 as decimal(18,2))", "decimal")]
    [DataRow("cast(1.5 as float)", "float")]
    [DataRow("cast(1.5 as real)", "real")]
    [DataRow("cast(1 as money)", "money")]
    [DataRow("cast(1 as smallmoney)", "smallmoney")]
    [DataRow("cast('abc' as varchar(10))", "varchar")]
    [DataRow("cast('abc' as nvarchar(10))", "nvarchar")]
    [DataRow("cast('abc' as char(5))", "char")]
    [DataRow("cast('abc' as nchar(5))", "nchar")]
    [DataRow("cast(0x1234 as varbinary(10))", "varbinary")]
    [DataRow("cast(0x1234 as binary(4))", "binary")]
    [DataRow("cast('2024-01-01' as date)", "date")]
    [DataRow("cast('2024-01-01 12:00' as datetime)", "datetime")]
    [DataRow("cast('2024-01-01 12:00' as smalldatetime)", "smalldatetime")]
    [DataRow("cast('2024-01-01 12:00' as datetime2(3))", "datetime2")]
    [DataRow("cast('12:00' as time(3))", "time")]
    [DataRow("cast('2024-01-01T12:00:00+00:00' as datetimeoffset(3))", "datetimeoffset")]
    [DataRow("cast('00000000-0000-0000-0000-000000000000' as uniqueidentifier)", "uniqueidentifier")]
    public void GetDataTypeName_ReturnsBareSqlServerName(string expression, string expected)
    {
        using var reader = OpenReader($"select {expression}");
        AreEqual(expected, reader.GetDataTypeName(0));
    }

    [TestMethod]
    [DataRow("cast(1 as int)", typeof(int))]
    [DataRow("cast(1 as bigint)", typeof(long))]
    [DataRow("cast(1 as smallint)", typeof(short))]
    [DataRow("cast(1 as tinyint)", typeof(byte))]
    [DataRow("cast(1 as bit)", typeof(bool))]
    [DataRow("cast(1 as decimal(18,2))", typeof(decimal))]
    [DataRow("cast(1.5 as float)", typeof(double))]
    [DataRow("cast(1.5 as real)", typeof(float))]
    [DataRow("cast(1 as money)", typeof(decimal))]
    [DataRow("cast('abc' as varchar(10))", typeof(string))]
    [DataRow("cast('abc' as nvarchar(10))", typeof(string))]
    [DataRow("cast(0x1234 as varbinary(10))", typeof(byte[]))]
    [DataRow("cast('2024-01-01' as date)", typeof(DateTime))]
    [DataRow("cast('2024-01-01 12:00' as datetime)", typeof(DateTime))]
    [DataRow("cast('2024-01-01 12:00' as datetime2(3))", typeof(DateTime))]
    [DataRow("cast('12:00' as time(3))", typeof(TimeSpan))]
    [DataRow("cast('2024-01-01T12:00:00+00:00' as datetimeoffset(3))", typeof(DateTimeOffset))]
    [DataRow("cast('00000000-0000-0000-0000-000000000000' as uniqueidentifier)", typeof(Guid))]
    [DataRow("cast(1 as smallmoney)", typeof(decimal))]
    [DataRow("cast('2024-01-01' as smalldatetime)", typeof(DateTime))]
    [DataRow("cast('abc' as char(5))", typeof(string))]
    [DataRow("cast('abc' as nchar(5))", typeof(string))]
    [DataRow("cast(0x1234 as binary(4))", typeof(byte[]))]
    public void GetFieldType_ReturnsClrType(string expression, Type expected)
    {
        using var reader = OpenReader($"select {expression}");
        AreEqual(expected, reader.GetFieldType(0));
    }

    [TestMethod]
    [DataRow("text", "text", typeof(string))]
    [DataRow("ntext", "ntext", typeof(string))]
    [DataRow("image", "image", typeof(byte[]))]
    public void LobTypes_FieldTypeAndDataTypeName_ViaColumnRoundTrip(string columnType, string expectedSqlName, Type expectedClr)
    {
        // text / ntext / image can't appear as a CAST target; reach their
        // type-metadata paths by declaring a column and reading it back.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (id int, c {columnType})");
        _ = sim.ExecuteNonQuery("insert t (id) values (1)");
        using var reader = sim.ExecuteReader("select c from t");
        AreEqual(expectedSqlName, reader.GetDataTypeName(0));
        AreEqual(expectedClr, reader.GetFieldType(0));
    }

    [TestMethod]
    public void RowVersion_FieldTypeAndDataTypeName_ViaColumnRoundTrip()
    {
        // rowversion stores 8 bytes, surfaces under the legacy "timestamp" name.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, rv rowversion)");
        _ = sim.ExecuteNonQuery("insert t (id) values (1)");
        using var reader = sim.ExecuteReader("select rv from t");
        AreEqual("timestamp", reader.GetDataTypeName(0));
        AreEqual(typeof(byte[]), reader.GetFieldType(0));
    }

    /// <summary>
    /// <c>sysname</c> is an alias for <c>nvarchar(128)</c> and TDS carries the
    /// base type, so a SqlClient consumer never sees the alias name: real
    /// answers <c>nvarchar</c> for a <c>sysname</c> column, for every scalar in
    /// the <c>TYPE_NAME</c> / <c>SCHEMA_NAME</c> / <c>DB_NAME</c> /
    /// <c>USER_NAME</c> family, for <c>CURRENT_USER</c> and its siblings, and
    /// for <c>CAST(… AS sysname)</c> (probed over SqlClient 7.0.2, 2026-08-05,
    /// across <c>GetDataTypeName</c> and <c>GetSchemaTable</c>).
    /// </summary>
    [TestMethod]
    public void SysName_FieldTypeAndDataTypeName_ViaSystemTable()
    {
        // sysname can't be declared in user CREATE TABLE; the systypes
        // catalog table exposes it via the "name" column.
        using var reader = new Simulation().ExecuteReader("select name from systypes");
        AreEqual("nvarchar", reader.GetDataTypeName(0));
        AreEqual(typeof(string), reader.GetFieldType(0));
    }

    /// <summary>
    /// The rest of the <c>sysname</c>-producing surface reports the same
    /// <c>nvarchar</c>, and a <c>sysname</c> operand carries the national
    /// family into a concatenation the way an <c>nvarchar</c> does (real types
    /// <c>TYPE_NAME(56) + ''</c> as <c>nvarchar(129)</c>).
    /// </summary>
    [TestMethod]
    public void SysNameProducersAllReportNvarchar()
    {
        using var reader = new Simulation().ExecuteReader(
            "select type_name(56) as a, schema_name(1) as b, db_name() as c, user_name() as d,"
            + " current_user as e, cast('x' as sysname) as f, type_name(56) + '' as g");
        for (var i = 0; i < reader.FieldCount; i++)
            AreEqual("nvarchar", reader.GetDataTypeName(i));
    }

    /// <summary>
    /// A <c>decimal</c>-family column reports <c>decimal</c> whatever its
    /// origin. The server does distinguish <c>numeric</c> from <c>decimal</c>
    /// and the simulator carries that onto the wire, but SqlClient maps both
    /// TDS tokens to <see cref="System.Data.SqlDbType.Decimal"/> and answers
    /// <c>decimal</c> for both — so an in-process consumer must see the same
    /// (probed over SqlClient 7.0.2, 2026-08-05). The name split itself is
    /// pinned in <c>Tests.Internal/DecimalTypeNameTests</c>.
    /// </summary>
    [TestMethod]
    public void DecimalFamily_AlwaysReportsDecimal_MatchingSqlClient()
    {
        using var reader = new Simulation().ExecuteReader(
            "select 1.0 as a, 1.0 + 2.0 as b, cast(1.0 as numeric(9,2)) as c, cast(1.0 as decimal(9,2)) as d,"
            + " convert(numeric(9,2), 1.0) as e");
        for (var i = 0; i < reader.FieldCount; i++)
            AreEqual("decimal", reader.GetDataTypeName(i));
    }

    [TestMethod]
    public void GetOrdinal_CaseSensitiveThenCaseInsensitive()
    {
        using var reader = OpenReader("select 1 as Alpha, 2 as alpha, 3 as Beta");
        // Exact-case match wins even when a lowercase sibling exists.
        AreEqual(0, reader.GetOrdinal("Alpha"));
        AreEqual(1, reader.GetOrdinal("alpha"));
        // Case-insensitive only matches when no exact match exists.
        AreEqual(2, reader.GetOrdinal("BETA"));
    }

    [TestMethod]
    public void GetOrdinal_UnknownColumnRaises()
        => ThrowsExactly<IndexOutOfRangeException>(() =>
        {
            using var reader = OpenReader("select 1 as a");
            _ = reader.GetOrdinal("missing");
        });

    [TestMethod]
    public void IndexerByName_ReturnsValue()
    {
        using var reader = OpenReader("select 1 as a, 'two' as b");
        IsTrue(reader.Read());
        AreEqual(1, reader["a"]);
        AreEqual("two", reader["b"]);
    }

    [TestMethod]
    public void GetBytes_NullBufferReturnsLength()
    {
        using var reader = OpenReader("select cast(0x0102030405 as varbinary(10))");
        IsTrue(reader.Read());
        AreEqual(5L, reader.GetBytes(0, 0, null, 0, 0));
    }

    [TestMethod]
    public void GetBytes_FullReadCopiesAllBytes()
    {
        using var reader = OpenReader("select cast(0x0102030405 as varbinary(10))");
        IsTrue(reader.Read());
        var buffer = new byte[5];
        AreEqual(5L, reader.GetBytes(0, 0, buffer, 0, 5));
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, buffer);
    }

    [TestMethod]
    public void GetBytes_PartialReadFromOffset()
    {
        using var reader = OpenReader("select cast(0x0102030405 as varbinary(10))");
        IsTrue(reader.Read());
        var buffer = new byte[3];
        AreEqual(3L, reader.GetBytes(0, 2, buffer, 0, 3));
        CollectionAssert.AreEqual(new byte[] { 0x03, 0x04, 0x05 }, buffer);
    }

    [TestMethod]
    public void GetBytes_OffsetPastEndReturnsZero()
    {
        using var reader = OpenReader("select cast(0x0102 as varbinary(10))");
        IsTrue(reader.Read());
        var buffer = new byte[3];
        AreEqual(0L, reader.GetBytes(0, 5, buffer, 0, 3));
    }

    [TestMethod]
    public void GetChars_NullBufferReturnsLength()
    {
        using var reader = OpenReader("select cast('abcde' as varchar(10))");
        IsTrue(reader.Read());
        AreEqual(5L, reader.GetChars(0, 0, null, 0, 0));
    }

    [TestMethod]
    public void GetChars_PartialReadFromOffset()
    {
        using var reader = OpenReader("select cast('abcde' as nvarchar(10))");
        IsTrue(reader.Read());
        var buffer = new char[3];
        AreEqual(3L, reader.GetChars(0, 2, buffer, 0, 3));
        CollectionAssert.AreEqual(new[] { 'c', 'd', 'e' }, buffer);
    }

    [TestMethod]
    public void GetChar_AlwaysThrowsInvalidCast()
    {
        using var reader = OpenReader("select cast('a' as char(1))");
        IsTrue(reader.Read());
        _ = ThrowsExactly<InvalidCastException>(() => reader.GetChar(0));
    }

    [TestMethod]
    public void GetFloat_ReadsRealColumn()
    {
        using var reader = OpenReader("select cast(3.5 as real)");
        IsTrue(reader.Read());
        AreEqual(3.5f, reader.GetFloat(0));
    }

    [TestMethod]
    public void GetFloat_OnNull_ThrowsSqlNullValueException()
    {
        using var reader = OpenReader("select cast(null as real)");
        IsTrue(reader.Read());
        _ = Throws<SqlNullValueException>(() => reader.GetFloat(0));
    }

    [TestMethod]
    public void GetBytes_NullColumn_ThrowsSqlNullValueException()
    {
        using var reader = OpenReader("select cast(null as varbinary(8))");
        IsTrue(reader.Read());
        _ = Throws<SqlNullValueException>(() => reader.GetBytes(0, 0, new byte[4], 0, 4));
    }

    [TestMethod]
    public void GetBytes_NegativeDataOffset_ThrowsArgumentOutOfRange()
    {
        using var reader = OpenReader("select cast(0x010203 as varbinary(8))");
        IsTrue(reader.Read());
        _ = Throws<ArgumentOutOfRangeException>(() => reader.GetBytes(0, -1, new byte[4], 0, 4));
    }

    [TestMethod]
    public void GetBytes_DataOffsetAtEnd_ReturnsZero()
    {
        using var reader = OpenReader("select cast(0x010203 as varbinary(8))");
        IsTrue(reader.Read());
        AreEqual(0L, reader.GetBytes(0, 3, new byte[4], 0, 4));
    }

    [TestMethod]
    public void GetBytes_ZeroLength_ReturnsZero()
    {
        using var reader = OpenReader("select cast(0x010203 as varbinary(8))");
        IsTrue(reader.Read());
        AreEqual(0L, reader.GetBytes(0, 0, new byte[4], 0, 0));
    }

    [TestMethod]
    public void GetChars_NullColumn_ThrowsSqlNullValueException()
    {
        using var reader = OpenReader("select cast(null as nvarchar(8))");
        IsTrue(reader.Read());
        _ = Throws<SqlNullValueException>(() => reader.GetChars(0, 0, new char[4], 0, 4));
    }

    [TestMethod]
    public void GetChars_NegativeDataOffset_ThrowsArgumentOutOfRange()
    {
        using var reader = OpenReader("select cast(N'abc' as nvarchar(8))");
        IsTrue(reader.Read());
        _ = Throws<ArgumentOutOfRangeException>(() => reader.GetChars(0, -1, new char[4], 0, 4));
    }

    [TestMethod]
    public void GetChars_DataOffsetAtEnd_ReturnsZero()
    {
        using var reader = OpenReader("select cast(N'abc' as nvarchar(8))");
        IsTrue(reader.Read());
        AreEqual(0L, reader.GetChars(0, 3, new char[4], 0, 4));
    }

    [TestMethod]
    public void GetChars_ZeroLength_ReturnsZero()
    {
        using var reader = OpenReader("select cast(N'abc' as nvarchar(8))");
        IsTrue(reader.Read());
        AreEqual(0L, reader.GetChars(0, 0, new char[4], 0, 0));
    }

    [TestMethod]
    public void GetDateTime_NonDateColumn_ThrowsInvalidCast()
    {
        using var reader = OpenReader("select 42");
        IsTrue(reader.Read());
        _ = Throws<InvalidCastException>(() => reader.GetDateTime(0));
    }

    [TestMethod]
    public void GetName_OutOfRange_ThrowsIndexOutOfRange()
    {
        // Range check is by ordinal, independent of whether Read has been
        // called.
        using var reader = OpenReader("select 1 as x");
        _ = Throws<IndexOutOfRangeException>(() => reader.GetName(5));
    }

    [TestMethod]
    public void BeforeFirstRead_OrdinalAccess_ThrowsInvalidOperation()
    {
        // Before any Read(), the underlying cursor is the EmptyCursor
        // sentinel; ordinal access throws.
        using var reader = OpenReader("select 42 as v");
        _ = Throws<InvalidOperationException>(() => _ = reader.GetValue(0));
    }

    /// <summary>
    /// <c>HasRows</c> peeks one row ahead without consuming it, stays true once
    /// rows have been seen, and is false for an empty result — matching
    /// SqlClient's contract.
    /// </summary>
    [TestMethod]
    public void HasRows_PeeksWithoutConsuming()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1), (2)");
        using var reader = sim.ExecuteReader("select id from t order by id");
        IsTrue(reader.HasRows);
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        IsFalse(reader.Read());
        IsTrue(reader.HasRows);
    }

    /// <inheritdoc cref="HasRows_PeeksWithoutConsuming"/>
    [TestMethod]
    public void HasRows_EmptyResult_IsFalse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var reader = sim.ExecuteReader("select id from t");
        IsFalse(reader.HasRows);
        IsFalse(reader.Read());
    }
}
