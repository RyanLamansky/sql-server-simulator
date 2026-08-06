using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class InsertTests
{
    [TestMethod]
    public void InsertRequiresTableToExist() => Throws<DbException>(() =>
        new Simulation().ExecuteNonQuery("insert t ( v ) values ( 1 )"));

    [TestMethod]
    [DataRow("t values ( 1 )", 1)]
    [DataRow("T values ( 1 )", 1)]
    [DataRow("into t values ( 1 )", 1)]    // optional INTO keyword accepted
    [DataRow("t ( v ) values ( 1 )", 1)]
    [DataRow("t ( V ) values ( 1 )", 1)]
    [DataRow("t values ( 1 ), ( 2 )", 2)]
    public void Insert(string commandText, int expectedRecordsAffected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v int )");
        AreEqual(expectedRecordsAffected, simulation.ExecuteNonQuery($"insert {commandText}"));
    }

    [TestMethod]
    public void InsertParameterized()
        => AreEqual(1, new Simulation()
            .CreateOpenConnection()
            .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p0", 1))
            .ExecuteNonQuery());

    [TestMethod]
    public void InsertParameterizedNameMismatch() => Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p1", 1))
        .ExecuteNonQuery());

    [TestMethod]
    public void InsertRequiresValidColumnNames() => Throws<DbException>(() =>
        new Simulation().ExecuteNonQuery("create table t ( v int );insert t ( x ) values ( 1 )"));

    [TestMethod]
    [DataRow("tinyint", "200", (byte)200)]
    [DataRow("smallint", "12345", (short)12345)]
    [DataRow("int", "-42", -42)]
    public void InsertCoercion_IntLiteralIntoColumn(string columnType, string literal, object expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t ( v {columnType} );
            insert t values ( {literal} );
            select v from t
            """));

    [TestMethod]
    public void InsertCoercion_ExpressionValue_IsEvaluated()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( v int );
            insert t values ( 2 + 3 * 4 ), ( -(10 - 7) ), ( abs(-9) )
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { 14, -3, 9 }, values);
    }

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoTinyInt_OverflowRaisesSqlException()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t ( v tinyint );
            insert t values ( 300 )
            """));
        Contains("Arithmetic overflow", ex.Message);
        Contains("tinyint", ex.Message);
    }

    [TestMethod]
    public void InsertCoercion_TinyIntParameterIntoInt32Column_Widens()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v int );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Byte, (byte)200);
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(200, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32ParameterIntoTinyIntColumn_OverflowRaisesSqlException()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v tinyint );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Int32, 300);

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        Contains("Arithmetic overflow", ex.Message);
    }

    [TestMethod]
    public void InsertVarchar_AtMaxLength_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varchar(5) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "hello");
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertVarchar_OverMaxLength_RaisesTruncation()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varchar(5) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "hello world");

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: 'hello'.", ex.Message);
    }

    [TestMethod]
    public void InsertNVarchar_OverMaxLength_RaisesTruncation()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v nvarchar(3) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.String, "héllo");

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: 'hél'.", ex.Message);
    }

    // varchar uses Windows-1252; "café" is 4 bytes (every char in CP1252) and fits varchar(4).
    [TestMethod]
    public void InsertVarchar_Cp1252Char_CountsAsOneByte()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varchar(4) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "café");
        AreEqual(1, insert.ExecuteNonQuery());
    }

    // Characters outside CP1252 are silently replaced with '?'.
    [TestMethod]
    public void InsertVarchar_OutOfCp1252Char_StoresAsReplacement()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varchar(10) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, "Ω");
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual("?", connection.CreateCommand("select v from t").ExecuteScalar());
    }

    // nvarchar limit is UCS-2 code units; "café" is 4 code units → fits nvarchar(4).
    [TestMethod]
    public void InsertNVarchar_MultiByteChar_CountsCodeUnitsNotBytes()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v nvarchar(4) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.String, "café");
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertNullIntoConstrainedVarchar_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varchar(5) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.AnsiString, DBNull.Value);
        AreEqual(1, insert.ExecuteNonQuery());
    }

    // INSERT t (b, a) VALUES maps first value to b regardless of declared order — the path EF Core depends on.
    [TestMethod]
    public void InsertWithExplicitMultiColumnList_RoutesValuesByName()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t ( a int, b int );
            insert t ( b, a ) values ( 1, 2 );
            select a from t
            """));

    [TestMethod]
    public void InsertVarbinary_AtMaxLength_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varbinary(4) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        AreEqual(1, insert.ExecuteNonQuery());

        var read = (byte[]?)connection.CreateCommand("select v from t").ExecuteScalar();
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, read);
    }

    // Msg 2628 renders the truncated prefix as 0xHEX — varbinary formatting.
    [TestMethod]
    public void InsertVarbinary_OverMaxLength_RaisesTruncationWithHexValue()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varbinary(2) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var ex = Throws<DbException>(() => insert.ExecuteNonQuery());
        AreEqual("String or binary data would be truncated in table 't', column 'v'. Truncated value: '0xDEAD'.", ex.Message);
    }

    [TestMethod]
    public void InsertNullIntoConstrainedVarbinary_Succeeds()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( v varbinary(4) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Binary, DBNull.Value);
        AreEqual(1, insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertDate_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( d date );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Date, new DateOnly(2026, 5, 4));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    // EF Core's legacy DateTime mapping arrives as DbType.Date with a DateTime value;
    // only the date portion lands in storage.
    [TestMethod]
    public void InsertDate_ViaDateTimeParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( d date );insert t values ( @p )";
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
        using var insert = connection.CreateCommand();
        insert.CommandText = $"create table t ( d {columnType} null );insert t values ( @p )";
        AddTypedParameter(insert, "p", dbType, DBNull.Value);
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(DBNull.Value, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    // SimulatedDbParameter infers DbType.Date when Value is a DateOnly.
    [TestMethod]
    public void InsertDate_AutoDetectedDbType_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand(
            "create table t ( d date );insert t values ( @p )", ("p", new DateOnly(2026, 5, 4)));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        using var insert = connection.CreateCommand(
            "create table t ( d datetime2(7) );insert t values ( @p )", ("p", dt));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dt, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTime2_LowerPrecisionTruncates()
    {
        using var connection = new Simulation().CreateOpenConnection();
        // 5_000 ticks (= 0.5ms) above the millisecond boundary rounds half-up.
        var dt = new DateTime(2026, 5, 4, 13, 45, 30, 100).AddTicks(5_000);
        using var insert = connection.CreateCommand(
            "create table t ( d datetime2(3) );insert t values ( @p )", ("p", dt));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new DateTime(2026, 5, 4, 13, 45, 30, 101), connection.CreateCommand("select d from t").ExecuteScalar());
    }

    // `datetime2` (no parens) defaults to precision 7.
    [TestMethod]
    public void InsertDateTime2_DefaultPrecisionColumn_AcceptsFullPrecisionValue()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        using var insert = connection.CreateCommand(
            "create table t ( d datetime2 );insert t values ( @p )", ("p", dt));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dt, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_ViaTimeSpanParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ts = new TimeSpan(13, 45, 30).Add(TimeSpan.FromTicks(1234567));
        using var insert = connection.CreateCommand(
            "create table t ( t time(7) );insert t values ( @p )", ("p", ts));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(ts, connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_ViaTimeOnlyParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand(
            "create table t ( t time(7) );insert t values ( @p )", ("p", new TimeOnly(13, 45, 30)));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new TimeSpan(13, 45, 30), connection.CreateCommand("select t from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertTime_LowerPrecisionTruncates()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ts = new TimeSpan(13, 45, 30).Add(TimeSpan.FromTicks(5_000_000));
        using var insert = connection.CreateCommand(
            "create table t ( t time(0) );insert t values ( @p )", ("p", ts));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(new TimeSpan(13, 45, 31), connection.CreateCommand("select t from t").ExecuteScalar());
    }

    // SQL Server's `time` is bounded to [00:00:00, 24:00:00); simulator surfaces it
    // at parameter-conversion time.
    [TestMethod]
    [DataRow(-1L)]                          // negative
    [DataRow(TimeSpan.TicksPerDay)]         // exactly 24:00:00
    [DataRow(TimeSpan.TicksPerDay + 1L)]    // past 24:00:00
    public void InsertTime_OutOfRangeParameter_Rejected(long ticks)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var insert = connection.CreateCommand();
        insert.CommandText = "create table t ( t time(7) );insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Time, new TimeSpan(ticks));

        _ = Throws<ArgumentOutOfRangeException>(() => insert.ExecuteNonQuery());
    }

    [TestMethod]
    public void InsertDateTimeOffset_ViaParameter_RoundTrips()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(1234567);
        using var insert = connection.CreateCommand(
            "create table t ( d datetimeoffset(7) );insert t values ( @p )", ("p", dto));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dto, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    // Two values share a UTC instant but differ in offset; both round-trip with offset preserved.
    [TestMethod]
    public void InsertDateTimeOffset_PreservesOffsetAcrossRoundTrip()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var east = new DateTimeOffset(2026, 5, 4, 20, 45, 30, TimeSpan.FromHours(7));
        var west = new DateTimeOffset(2026, 5, 4, 6, 45, 30, TimeSpan.FromHours(-7));
        using var ins = connection.CreateCommand(
            "create table t ( id int, d datetimeoffset(0) );insert t values (1, @a), (2, @b)",
            ("a", east), ("b", west));
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
        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(5_000_000); // +0.5s
        using var insert = connection.CreateCommand(
            "create table t ( d datetimeoffset(0) );insert t values ( @p )", ("p", dto));
        AreEqual(1, insert.ExecuteNonQuery());

        var expected = new DateTimeOffset(2026, 5, 4, 13, 45, 31, TimeSpan.FromHours(-7));
        AreEqual(expected, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertDateTimeOffset_DefaultPrecisionColumn_AcceptsFullPrecisionValue()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(2)).AddTicks(1234567);
        using var insert = connection.CreateCommand(
            "create table t ( d datetimeoffset );insert t values ( @p )", ("p", dto));
        AreEqual(1, insert.ExecuteNonQuery());

        AreEqual(dto, connection.CreateCommand("select d from t").ExecuteScalar());
    }

    [TestMethod]
    public void Insert_ExplicitNullIntoNotNull_RaisesMsg515()
        => new Simulation().AssertSqlError("""
            create table t (a int not null);
            insert t values (null)
            """, 515,
            "Cannot insert the value NULL into column 'a', table 'simulated.dbo.t'; column does not allow nulls. INSERT fails.");

    // No DEFAULT, no IDENTITY → omitted column auto-fills with NULL; NOT NULL catches it
    // the same as explicit NULL.
    [TestMethod]
    public void Insert_OmittedColumnFallsThroughToNullIntoNotNull_RaisesMsg515()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, x int);
            insert t (x) values (10)
            """, 515);

    [TestMethod]
    public void Insert_NullableColumn_AcceptsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("""
            create table t (a int null);
            insert t values (null);
            select a from t
            """));

    // DEFAULT NULL on a NOT NULL column is degenerate but parseable; omitted insert
    // fills NULL, NOT NULL catches it.
    [TestMethod]
    public void Insert_NullDefault_RaisesMsg515OnNotNullColumn()
        => _ = new Simulation().AssertSqlError("""
            create table t (a int not null default null, b int);
            insert t (b) values (1)
            """, 515);

    [TestMethod]
    public void Insert_PersistedComputedNotNull_ResultNullRaisesMsg515()
        => _ = new Simulation().AssertSqlError("""
            create table t (a int, c as a + 1 persisted not null);
            insert t (a) values (null)
            """, 515);

    [TestMethod]
    public void InsertSelect_VanillaWithColumnList_CopiesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (id int, name varchar(50));
            create table dst (id int, name varchar(50));
            insert src (id, name) values (1, 'alpha'), (2, 'beta'), (3, 'gamma')
            """);
        AreEqual(3, simulation.ExecuteNonQuery("insert dst (id, name) select id, name from src"));
        AreEqual(3, simulation.ExecuteScalar("select count(*) from dst"));
        AreEqual("beta", simulation.ExecuteScalar("select name from dst where id = 2"));
    }

    // No column list — INSERT skips IDENTITY and computed columns from the destination,
    // and the SELECT projection must match the remaining writable columns.
    [TestMethod]
    public void InsertSelect_NoColumnList_TargetsWritableColumns()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (v varchar(20));
            create table dst (id int identity, v varchar(20));
            insert src (v) values ('alpha')
            """);
        AreEqual(1, simulation.ExecuteNonQuery("insert dst select v from src"));
        AreEqual("alpha", simulation.ExecuteScalar("select v from dst where id = 1"));
    }

    [TestMethod]
    public void InsertSelect_TooManyColumns_RaisesMsg121()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int, b int, c int);
            create table dst (a int, b int);
            insert src (a, b, c) values (1, 2, 3)
            """);
        simulation.AssertSqlError("insert dst (a, b) select a, b, c from src", 121,
            "The select list for the INSERT statement contains more items than the insert list. The number of SELECT values must match the number of INSERT columns.");
    }

    [TestMethod]
    public void InsertSelect_TooFewColumns_RaisesMsg120()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int);
            create table dst (a int, b int);
            insert src (a) values (1)
            """);
        simulation.AssertSqlError("insert dst (a, b) select a from src", 120,
            "The select list for the INSERT statement contains fewer items than the insert list. The number of SELECT values must match the number of INSERT columns.");
    }

    [TestMethod]
    public void InsertSelect_EmptySource_SilentSuccess()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int);
            create table dst (a int)
            """);
        AreEqual(0, simulation.ExecuteNonQuery("insert dst (a) select a from src where a > 0"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from dst"));
    }

    [TestMethod]
    public void InsertSelect_WhereJoinAggregateOffset_AllSelectionFeaturesWorkInSource()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (id int, score int);
            create table aux (id int, label varchar(20));
            create table dst (a int, b varchar(20));
            insert src (id, score) values (1, 10), (2, 20), (3, 30);
            insert aux (id, label) values (1, 'one'), (2, 'two'), (3, 'three')
            """);
        AreEqual(2, simulation.ExecuteNonQuery("""
            insert dst (a, b)
                select s.id, a.label
                from src s inner join aux a on a.id = s.id
                where s.score >= 20
                order by s.id
                offset 0 rows fetch next 5 rows only
            """));
        AreEqual("two", simulation.ExecuteScalar("select b from dst where a = 2"));
        AreEqual("three", simulation.ExecuteScalar("select b from dst where a = 3"));
    }

    [TestMethod]
    public void InsertSelect_UnionAllSource_BothBranchesInserted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (id int, score int);
            create table dst (v int);
            insert src (id, score) values (1, 10), (2, 20), (3, 30)
            """);
        AreEqual(6, simulation.ExecuteNonQuery("insert dst (v) select id from src union all select score from src"));
        AreEqual(6, simulation.ExecuteScalar("select count(*) from dst"));
        AreEqual(66, simulation.ExecuteScalar("select sum(v) from dst"));
    }

    [TestMethod]
    public void InsertSelect_SelfInsertBuffersSourceBeforeWrite()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int);
            insert t (a) values (1), (2)
            """);
        AreEqual(2, simulation.ExecuteNonQuery("insert t (a) select a + 100 from t"));
        AreEqual(4, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual(206, simulation.ExecuteScalar("select sum(a) from t"));
    }

    [TestMethod]
    public void InsertSelect_IdentityColumnSkippedFromAutoColumnList()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (v varchar(20));
            create table dst (id int identity primary key, v varchar(20));
            insert src (v) values ('alpha'), ('beta'), ('gamma')
            """);
        AreEqual(3, simulation.ExecuteNonQuery("insert dst select v from src"));
        AreEqual(1, simulation.ExecuteScalar("select id from dst where v = 'alpha'"));
        AreEqual(3, simulation.ExecuteScalar("select id from dst where v = 'gamma'"));
    }

    // Explicit IDENTITY column without SET IDENTITY_INSERT ON — same Msg 544 path
    // as the VALUES-side; SELECT-source uses the existing pre-source identity check.
    [TestMethod]
    public void InsertSelect_ExplicitIdentityWithoutInsertOn_RaisesMsg544()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (id int, v varchar(20));
            create table dst (id int identity, v varchar(20));
            insert src (id, v) values (5, 'x')
            """);
        _ = simulation.AssertSqlError("insert dst (id, v) select id, v from src", 544);
    }

    [TestMethod]
    public void InsertSelect_DefaultAppliesForOmittedColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int);
            create table dst (a int, b int default 99);
            insert src (a) values (1), (2)
            """);
        AreEqual(2, simulation.ExecuteNonQuery("insert dst (a) select a from src"));
        AreEqual(99, simulation.ExecuteScalar("select b from dst where a = 1"));
        AreEqual(99, simulation.ExecuteScalar("select b from dst where a = 2"));
    }

    [TestMethod]
    public void InsertSelect_TypeCoercion_IntToBigInt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int);
            create table dst (a bigint);
            insert src (a) values (12345)
            """);
        AreEqual(1, simulation.ExecuteNonQuery("insert dst (a) select a from src"));
        AreEqual(12345L, simulation.ExecuteScalar("select a from dst"));
    }

    // CHECK violation in mid-SELECT-source rolls back the whole statement
    // — same statement-level atomicity the VALUES path enjoys. The earlier
    // rows from the same INSERT must not survive.
    [TestMethod]
    public void InsertSelect_CheckViolation_RollsBackEntireStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int);
            create table dst (a int check (a > 0));
            insert src (a) values (10), (20), (-1)
            """);
        _ = simulation.AssertSqlError("insert dst (a) select a from src", 547);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from dst"));
    }

    [TestMethod]
    public void InsertSelect_OutputClause_ProjectsInsertedRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (v varchar(20));
            create table dst (id int identity, v varchar(20));
            insert src (v) values ('alpha'), ('beta')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("insert dst (v) output inserted.id, inserted.v select v from src order by v").ExecuteReader();
        var rows = new List<(int id, string v)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { (1, "alpha"), (2, "beta") }, rows);
    }

    [TestMethod]
    public void InsertSelect_BareWithoutInto_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (a int, b varchar(20));
            create table dst (a int, b varchar(20));
            insert src (a, b) values (1, 'x'), (2, 'y')
            """);
        AreEqual(2, simulation.ExecuteNonQuery("insert dst select a, b from src"));
    }

    [TestMethod]
    public void InsertSelect_AggregateSource_SingleRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (score int);
            create table dst (total int);
            insert src (score) values (10), (20), (30)
            """);
        AreEqual(1, simulation.ExecuteNonQuery("insert dst (total) select sum(score) from src"));
        AreEqual(60, simulation.ExecuteScalar("select total from dst"));
    }

    private static void AddTypedParameter(DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    // --- A parenthesized SELECT source ---

    /// <summary>
    /// <c>INSERT INTO t (cols) (SELECT …)</c> — the parens are only reachable
    /// once an explicit column list has been consumed, which is why the
    /// no-column-list form stays a syntax error on both engines.
    /// </summary>
    [TestMethod]
    public void ParenthesizedSelectSource_Inserts()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            declare @t table (id int);
            insert into @t (id) (select 1 union select 2);
            select count(*) from @t
            """));

    [TestMethod]
    public void NestedParenthesizedSelectSource_Inserts()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            declare @t table (id int);
            insert into @t (id) ((select 5));
            select id from @t
            """));

    [TestMethod]
    public void ParenthesizedSource_StillReportsTheArityError()
        => AreEqual(
            "The select list for the INSERT statement contains more items than the insert list. "
                + "The number of SELECT values must match the number of INSERT columns.",
            new Simulation().AssertSqlError("""
                declare @t table (id int);
                insert into @t (id) (select 1, 2)
                """, 121).Message);

    [TestMethod]
    public void ParenthesizedSource_WithoutAColumnList_IsASyntaxError()
        => new Simulation().AssertSqlError("""
            declare @t table (id int);
            insert into @t (select 7)
            """, 102);
}
