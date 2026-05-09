using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class InsertTests
{
    [TestMethod]
    public void InsertRequiresTableToExist() => Throws<DbException>(() => new Simulation()
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
        _ = simulation.CreateOpenConnection().CreateCommand("create table t ( v int )").ExecuteNonQuery();
        var result = simulation.CreateCommand($"insert {commandText}").ExecuteNonQuery();
        AreEqual(expectedRecordsAffected, result);
    }

    [TestMethod]
    public void InsertParameterized()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p0", 1))
            .ExecuteNonQuery();
        AreEqual(1, result);
    }

    [TestMethod]
    public void InsertParameterizedNameMismatch() => Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p1", 1))
        .ExecuteNonQuery()
    );

    [TestMethod]
    public void InsertRequiresValidColumnNames() => Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t ( x ) values ( 1 )")
        .ExecuteNonQuery()
    );

    [TestMethod]
    [DataRow("tinyint", "200", (byte)200)]
    [DataRow("smallint", "12345", (short)12345)]
    [DataRow("int", "-42", -42)]
    public void InsertCoercion_IntLiteralIntoColumn(string columnType, string literal, object expected)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( v {columnType} )").ExecuteNonQuery();
        _ = connection.CreateCommand($"insert t values ( {literal} )").ExecuteNonQuery();
        AreEqual(expected, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_ExpressionValue_IsEvaluated()
    {
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
        var ex = Throws<DbException>(() => connection.CreateCommand("insert t values ( 300 )").ExecuteNonQuery());
        Contains("Arithmetic overflow", ex.Message);
        Contains("tinyint", ex.Message);
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
            AreEqual(1, insert.ExecuteNonQuery());
        }

        AreEqual(200, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32ParameterIntoTinyIntColumn_OverflowRaisesSqlException()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Int32, 300);

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        Contains("Arithmetic overflow", ex.Message);
    }

    [TestMethod]
    public void InsertVarchar_AtMaxLength_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "hello");
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertVarchar_OverMaxLength_RaisesTruncation()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "hello world");

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: 'hello'.", ex.Message);
    }

    [TestMethod]
    public void InsertNVarchar_OverMaxLength_RaisesTruncation()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v nvarchar(3) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.String, "héllo");

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: 'hél'.", ex.Message);
    }

    [TestMethod]
    public void InsertVarchar_Cp1252Char_CountsAsOneByte()
    {
        // varchar uses Windows-1252; "café" is 4 bytes (every char in CP1252) and fits varchar(4).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "café");
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertVarchar_OutOfCp1252Char_StoresAsReplacement()
    {
        // Characters outside CP1252 are silently replaced with '?'.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(10) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "Ω");
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual("?", connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertNVarchar_MultiByteChar_CountsCodeUnitsNotBytes()
    {
        // nvarchar limit is UCS-2 code units; "café" is 4 code units → fits nvarchar(4).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v nvarchar(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.String, "café");
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertNullIntoConstrainedVarchar_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, DBNull.Value);
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertWithExplicitMultiColumnList_RoutesValuesByName()
    {
        // INSERT t (b, a) VALUES maps first value to b regardless of declared order — the path EF Core depends on.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t ( b, a ) values ( 1, 2 )").ExecuteNonQuery();

        using var read = connection.CreateCommand("select a from t").ExecuteReader();
        IsTrue(read.Read());
        AreEqual(2, read.GetInt32(0));
    }

    [TestMethod]
    public void InsertVarbinary_AtMaxLength_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varbinary(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        AreEqual(1, insert.ExecuteNonQuery());

        var read = (byte[]?)connection.CreateCommand("select v from t").ExecuteScalar();
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, read);
    }

    [TestMethod]
    public void InsertVarbinary_OverMaxLength_RaisesTruncationWithHexValue()
    {
        // Msg 2628 renders the truncated prefix as 0xHEX — varbinary formatting.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varbinary(2) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: '0xDEAD'.", ex.Message);
    }

    [TestMethod]
    public void InsertNullIntoConstrainedVarbinary_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varbinary(4) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, DBNull.Value);
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertDate_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Date, new DateOnly(2026, 5, 4));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDate_ViaDateTimeParameter_RoundTrips()
    {
        // EF Core's legacy DateTime mapping arrives as DbType.Date with a DateTime value; only the date portion lands in storage.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Date, new DateTime(2026, 5, 4, 13, 45, 30));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("date", DbType.Date)]
    [DataRow("datetime2(7)", DbType.DateTime2)]
    [DataRow("time(7)", DbType.Time)]
    [DataRow("datetimeoffset(7)", DbType.DateTimeOffset)]
    public void InsertDateTimeFamily_NullValue_RoundTrips(string columnType, DbType dbType)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( d {columnType} null )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", dbType, DBNull.Value);
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(DBNull.Value, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDate_AutoDetectedDbType_RoundTrips()
    {
        // SimulatedDbParameter infers DbType.Date when Value is a DateOnly.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", new DateOnly(2026, 5, 4)));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2(7) )").ExecuteNonQuery();

        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dt));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dt, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_LowerPrecisionTruncates()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2(3) )").ExecuteNonQuery();

        // 5_000 ticks (= 0.5ms) above the millisecond boundary rounds half-up.
        var dt = new DateTime(2026, 5, 4, 13, 45, 30, 100).AddTicks(5_000);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dt));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4, 13, 45, 30, 101), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_DefaultPrecisionColumn_AcceptsFullPrecisionValue()
    {
        // `datetime2` (no parens) defaults to precision 7.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetime2 )").ExecuteNonQuery();

        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dt));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dt, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_ViaTimeSpanParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) )").ExecuteNonQuery();

        var ts = new TimeSpan(13, 45, 30).Add(TimeSpan.FromTicks(1234567));
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", ts));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(ts, connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_ViaTimeOnlyParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", new TimeOnly(13, 45, 30)));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new TimeSpan(13, 45, 30), connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_LowerPrecisionTruncates()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(0) )").ExecuteNonQuery();

        var ts = new TimeSpan(13, 45, 30).Add(TimeSpan.FromTicks(5_000_000));
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", ts));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new TimeSpan(13, 45, 31), connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    [DataRow(-1L)]                          // negative
    [DataRow(TimeSpan.TicksPerDay)]         // exactly 24:00:00
    [DataRow(TimeSpan.TicksPerDay + 1L)]    // past 24:00:00
    public void InsertTime_OutOfRangeParameter_Rejected(long ticks)
    {
        // SQL Server's `time` is bounded to [00:00:00, 24:00:00); simulator surfaces it at parameter-conversion time.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( t time(7) )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Time, new TimeSpan(ticks));

        _ = Throws<ArgumentOutOfRangeException>(() => insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertDateTimeOffset_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset(7) )").ExecuteNonQuery();

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dto));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dto, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTimeOffset_PreservesOffsetAcrossRoundTrip()
    {
        // Two values share a UTC instant but differ in offset; both round-trip with offset preserved.
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
        AreEqual(east, rows[0].d);
        AreEqual(TimeSpan.FromHours(7), rows[0].d.Offset);
        AreEqual(west, rows[1].d);
        AreEqual(TimeSpan.FromHours(-7), rows[1].d.Offset);
    }

    [TestMethod]
    public void InsertDateTimeOffset_LowerPrecisionRoundsHalfUp()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset(0) )").ExecuteNonQuery();

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(5_000_000); // +0.5s
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dto));
        AreEqual(1, insert.ExecuteNonQuery());

        var expected = new DateTimeOffset(2026, 5, 4, 13, 45, 31, TimeSpan.FromHours(-7));
        AreEqual(expected, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTimeOffset_DefaultPrecisionColumn_AcceptsFullPrecisionValue()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d datetimeoffset )").ExecuteNonQuery();

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(2)).AddTicks(1234567);
        using var insert = connection.CreateCommand("insert t values ( @p )", ("p", dto));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dto, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void Insert_ExplicitNullIntoNotNull_RaisesMsg515()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null)");
        simulation.AssertSqlError(
            "insert into t values (null)", 515,
            "Cannot insert the value NULL into column 'a', table 'simulated.dbo.t'; column does not allow nulls. INSERT fails.");
    }

    [TestMethod]
    public void Insert_OmittedColumnFallsThroughToNullIntoNotNull_RaisesMsg515()
    {
        // No DEFAULT, no IDENTITY → omitted column auto-fills with NULL; NOT NULL catches it the same as explicit NULL.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null, x int)");
        _ = simulation.AssertSqlError("insert into t (x) values (10)", 515);
    }

    [TestMethod]
    public void Insert_NullableColumn_AcceptsNull()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int null)");
        _ = simulation.ExecuteNonQuery("insert into t values (null)");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select a from t"));
    }

    [TestMethod]
    public void Insert_NullDefault_RaisesMsg515OnNotNullColumn()
    {
        // DEFAULT NULL on a NOT NULL column is degenerate but parseable; omitted insert fills NULL, NOT NULL catches it.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null default null, b int)");
        _ = simulation.AssertSqlError("insert into t (b) values (1)", 515);
    }

    [TestMethod]
    public void Insert_PersistedComputedNotNull_ResultNullRaisesMsg515()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, c as a + 1 persisted not null)");
        _ = simulation.AssertSqlError("insert into t (a) values (null)", 515);
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
