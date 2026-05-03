using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

[TestClass]
public class InsertTests
{
    [TestMethod]
    public void InsertRequiresTableToExist() => Assert.Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("insert t ( v ) values ( 1 )")
        .ExecuteNonQuery()
    );

    [TestMethod]
    [DataRow("t values ( 1 )", 1)]
    [DataRow("T values ( 1 )", 1)]
    [DataRow("t ( v ) values ( 1 )", 1)]
    [DataRow("t ( V ) values ( 1 )", 1)]
    [DataRow("t values ( 1 ), ( 2 )", 2)]
    public void Insert(string commandText, int expectedRecordsAffected)
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("create table t ( v int )")
            .ExecuteNonQuery();

        var result = simulation
            .CreateCommand($"insert {commandText}")
            .ExecuteNonQuery();

        Assert.AreEqual(expectedRecordsAffected, result);
    }

    [TestMethod]
    public void InsertParameterized()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p0", 1))
            .ExecuteNonQuery();

        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void InsertParameterizedNameMismatch() => Assert.Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p1", 1))
        .ExecuteNonQuery()
    );

    [TestMethod]
    public void InsertRequiresValidColumnNames() => Assert.Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t ( x ) values ( 1 )")
        .ExecuteNonQuery()
    );

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoTinyInt_NarrowsInRange()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 200 )").ExecuteNonQuery();

        Assert.AreEqual((byte)200, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoSmallInt_NarrowsInRange()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v smallint )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 12345 )").ExecuteNonQuery();

        Assert.AreEqual((short)12345, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_NegativeLiteral_LandsAsNegative()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( -42 )").ExecuteNonQuery();

        Assert.AreEqual(-42, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_ExpressionValue_IsEvaluated()
    {
        // INSERT VALUES accepts arbitrary scalar expressions (arithmetic,
        // parenthesized, function calls) — anything Expression.Parse handles.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 2 + 3 * 4 ), ( -(10 - 7) ), ( abs(-9) )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { 14, -3, 9 }, values);
    }

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoTinyInt_OverflowRaisesSqlException()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();
        var insert = connection.CreateCommand("insert t values ( 300 )");
        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        StringAssert.Contains(ex.Message, "Arithmetic overflow");
        StringAssert.Contains(ex.Message, "tinyint");
    }

    [TestMethod]
    public void InsertCoercion_TinyIntParameterIntoInt32Column_Widens()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "insert t values ( @p )";
            AddTypedParameter(insert, "p", DbType.Byte, (byte)200);
            Assert.AreEqual(1, insert.ExecuteNonQuery());
        }

        Assert.AreEqual(200, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32ParameterIntoTinyIntColumn_OverflowRaisesSqlException()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Int32, 300);

        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        StringAssert.Contains(ex.Message, "Arithmetic overflow");
    }

    [TestMethod]
    public void InsertVarchar_AtMaxLength_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "hello");
        Assert.AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertVarchar_OverMaxLength_RaisesTruncation()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "hello world");

        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        Assert.AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: 'hello'.", ex.Message);
    }

    [TestMethod]
    public void InsertNVarchar_OverMaxLength_RaisesTruncation()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v nvarchar(3) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.String, "héllo");

        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        Assert.AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: 'hél'.", ex.Message);
    }

    [TestMethod]
    public void InsertVarchar_Cp1252Char_CountsAsOneByte()
    {
        // varchar uses Windows-1252; "café" is 4 bytes (every char in CP1252)
        // and fits varchar(4) exactly. This is the inverse of the misconception
        // that "multi-byte char in .NET = multi-byte in varchar" — true under
        // UTF-8, false under SQL Server's default CP1252 collation.
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varchar(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "café");
        Assert.AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertVarchar_OutOfCp1252Char_StoresAsReplacement()
    {
        // Characters outside CP1252 are silently replaced with '?'. The simulator
        // matches SQL Server's lossy default — "Ω" round-trips as "?".
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varchar(10) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "Ω");
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        var read = connection.CreateCommand("select v from t").ExecuteScalar();
        Assert.AreEqual("?", read);
    }

    [TestMethod]
    public void InsertNVarchar_MultiByteChar_CountsCodeUnitsNotBytes()
    {
        // nvarchar limit is UCS-2 code units; "café" is 4 code units and fits in nvarchar(4).
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v nvarchar(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.String, "café");
        Assert.AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertNullIntoConstrainedVarchar_Succeeds()
    {
        // NULL bypasses the length check.
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, DBNull.Value);
        Assert.AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertWithExplicitMultiColumnList_RoutesValuesByName()
    {
        // INSERT t (b, a) VALUES (...) maps the first value to b and the second
        // to a, regardless of declared column order. This is the comma-separated
        // column list path EF Core depends on.
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t ( b, a ) values ( 1, 2 )").ExecuteNonQuery();

        using var read = connection.CreateCommand("select a from t").ExecuteReader();
        Assert.IsTrue(read.Read());
        Assert.AreEqual(2, read.GetInt32(0));
    }

    [TestMethod]
    public void InsertVarbinary_AtMaxLength_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varbinary(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        var read = (byte[]?)connection.CreateCommand("select v from t").ExecuteScalar();
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, read);
    }

    [TestMethod]
    public void InsertVarbinary_OverMaxLength_RaisesTruncationWithHexValue()
    {
        // Verbose Msg 2628 should render the truncated prefix as 0xHEX rather
        // than as a string, matching SQL Server's varbinary formatting.
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varbinary(2) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        Assert.AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: '0xDEAD'.", ex.Message);
    }

    [TestMethod]
    public void InsertNullIntoConstrainedVarbinary_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v varbinary(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, DBNull.Value);
        Assert.AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertDate_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Date, new DateOnly(2026, 5, 4));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        var read = connection.CreateCommand("select d from t").ExecuteScalar();
        Assert.AreEqual(new DateTime(2026, 5, 4), read);
    }

    [TestMethod]
    public void InsertDate_ViaDateTimeParameter_RoundTrips()
    {
        // EF Core's legacy DateTime mapping arrives as DbType.Date with a
        // DateTime value; only the date portion should land in storage.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Date, new DateTime(2026, 5, 4, 13, 45, 30));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        var read = connection.CreateCommand("select d from t").ExecuteScalar();
        Assert.AreEqual(new DateTime(2026, 5, 4), read);
    }

    [TestMethod]
    public void InsertDate_NullValue_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date null )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Date, DBNull.Value);
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        var read = connection.CreateCommand("select d from t").ExecuteScalar();
        Assert.AreEqual(DBNull.Value, read);
    }

    [TestMethod]
    public void InsertDate_AutoDetectedDbType_RoundTrips()
    {
        // SimulatedDbParameter infers DbType.Date when Value is a DateOnly.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", new DateOnly(2026, 5, 4)));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(new DateTime(2026, 5, 4), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2(7) )").ExecuteNonQuery();

        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dt));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(dt, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_LowerPrecisionTruncates()
    {
        // Storing a precision-7 value into a precision-3 column rounds the
        // sub-millisecond portion. The destination column's precision wins
        // because that's how the value gets encoded into the column's bytes.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2(3) )").ExecuteNonQuery();

        // 5_000 ticks (= 0.5ms) above the millisecond boundary rounds half-up.
        var dt = new DateTime(2026, 5, 4, 13, 45, 30, 100).AddTicks(5_000);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dt));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(new DateTime(2026, 5, 4, 13, 45, 30, 101), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_NullValue_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2(7) null )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.DateTime2, DBNull.Value);
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(DBNull.Value, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_DefaultPrecisionColumn_AcceptsFullPrecisionValue()
    {
        // `datetime2` (no parens) defaults to precision 7 in SQL Server; full
        // DateTime ticks should round-trip without loss.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2 )").ExecuteNonQuery();

        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dt));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(dt, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_ViaTimeSpanParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) )").ExecuteNonQuery();

        var ts = new TimeSpan(13, 45, 30).Add(TimeSpan.FromTicks(1234567));
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", ts));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(ts, connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_ViaTimeOnlyParameter_RoundTrips()
    {
        // EF Core's TimeOnly mapping arrives as DbType.Time with a TimeOnly value.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", new TimeOnly(13, 45, 30)));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(new TimeSpan(13, 45, 30), connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_LowerPrecisionTruncates()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(0) )").ExecuteNonQuery();

        // 0.5s exactly => round half-up to next whole second.
        var ts = new TimeSpan(13, 45, 30).Add(TimeSpan.FromTicks(5_000_000));
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", ts));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(new TimeSpan(13, 45, 31), connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_NullValue_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) null )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Time, DBNull.Value);
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(DBNull.Value, connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    [DataRow(-1L)]                          // negative
    [DataRow(TimeSpan.TicksPerDay)]         // exactly 24:00:00
    [DataRow(TimeSpan.TicksPerDay + 1L)]    // past 24:00:00
    public void InsertTime_OutOfRangeParameter_Rejected(long ticks)
    {
        // SQL Server's `time` is bounded to [00:00:00, 24:00:00). A TimeSpan
        // outside that window has no valid encoding; the simulator surfaces it
        // at parameter-conversion time rather than silently truncating.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Time, new TimeSpan(ticks));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertDateTimeOffset_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset(7) )").ExecuteNonQuery();

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dto));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(dto, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTimeOffset_PreservesOffsetAcrossRoundTrip()
    {
        // Two values share a UTC instant but differ in offset; both must
        // come back unchanged (the offset is part of the user-visible value
        // even though equality compares by UTC).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetimeoffset(0) )").ExecuteNonQuery();

        var east = new DateTimeOffset(2026, 5, 4, 20, 45, 30, TimeSpan.FromHours(7));
        var west = new DateTimeOffset(2026, 5, 4, 6, 45, 30, TimeSpan.FromHours(-7));
        using var ins = connection.CreateCommand("insert t values (1, @a), (2, @b)", ("a", east), ("b", west));
        _ = ins.ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id, d from t").ExecuteReader();
        var rows = new List<(int id, DateTimeOffset d)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (DateTimeOffset)reader[1]));
        rows.Sort((a, b) => a.id.CompareTo(b.id));
        Assert.AreEqual(east, rows[0].d);
        Assert.AreEqual(TimeSpan.FromHours(7), rows[0].d.Offset);
        Assert.AreEqual(west, rows[1].d);
        Assert.AreEqual(TimeSpan.FromHours(-7), rows[1].d.Offset);
    }

    [TestMethod]
    public void InsertDateTimeOffset_LowerPrecisionRoundsHalfUp()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset(0) )").ExecuteNonQuery();

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(5_000_000); // +0.5s
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dto));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        var expected = new DateTimeOffset(2026, 5, 4, 13, 45, 31, TimeSpan.FromHours(-7));
        Assert.AreEqual(expected, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTimeOffset_NullValue_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset(7) null )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.DateTimeOffset, DBNull.Value);
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(DBNull.Value, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTimeOffset_DefaultPrecisionColumn_AcceptsFullPrecisionValue()
    {
        // `datetimeoffset` (no parens) defaults to precision 7 in SQL Server.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset )").ExecuteNonQuery();

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(2)).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dto));
        Assert.AreEqual(1, insert.ExecuteNonQuery());

        Assert.AreEqual(dto, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    private static void AddTypedParameter(DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
