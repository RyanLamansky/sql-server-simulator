namespace SqlServerSimulator;

/// <summary>
/// An integer argument outside the declared parameter's range raises SQL
/// Server's own conversion error rather than leaking .NET's narrowing
/// exception, across the catalog-id scalars, the date-part builders, the
/// spatial constructors, the legacy hex functions and the system procedures.
/// Every number / state / wording here is probe-confirmed against SQL Server
/// 2025 (2026-07-31).
/// </summary>
[TestClass]
public sealed class ScalarArgumentOverflowTests
{
    /// <summary>
    /// The <c>int</c>-parameter majority: a <c>bigint</c> argument past int
    /// range reports the generic Msg 8115 naming the target.
    /// </summary>
    [TestMethod]
    [DataRow("select col_name(cast(3000000000 as bigint), 1)")]
    [DataRow("select col_name(1, cast(3000000000 as bigint))")]
    [DataRow("select columnproperty(cast(3000000000 as bigint), 'x', 'ColumnId')")]
    [DataRow("select object_name(cast(3000000000 as bigint))")]
    [DataRow("select object_name(1, cast(3000000000 as bigint))")]
    [DataRow("select object_schema_name(cast(3000000000 as bigint))")]
    [DataRow("select object_definition(cast(3000000000 as bigint))")]
    [DataRow("select objectproperty(cast(3000000000 as bigint), 'IsTable')")]
    [DataRow("select objectpropertyex(cast(3000000000 as bigint), 'BaseType')")]
    [DataRow("select schema_name(cast(3000000000 as bigint))")]
    [DataRow("select db_name(cast(3000000000 as bigint))")]
    [DataRow("select type_name(cast(3000000000 as bigint))")]
    [DataRow("select file_name(cast(3000000000 as bigint))")]
    [DataRow("select user_name(cast(3000000000 as bigint))")]
    [DataRow("select indexproperty(cast(3000000000 as bigint), 'x', 'IndexDepth')")]
    [DataRow("select stats_date(cast(3000000000 as bigint), 1)")]
    [DataRow("select * from fn_virtualfilestats(cast(3000000000 as bigint), 1)")]
    public void IntParameter_OutOfRange_RaisesMsg8115NamingInt(string sql)
        => new Simulation().AssertSqlError(sql, 8115, "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// These resolve the object named by an earlier argument before converting
    /// the later ids, so the overflow only surfaces for a table that exists —
    /// real converts every argument up front instead.
    /// </summary>
    [TestMethod]
    [DataRow("select index_col('t', cast(3000000000 as bigint), 1)")]
    [DataRow("select index_col('t', 1, cast(3000000000 as bigint))")]
    [DataRow("select indexkey_property(object_id('t'), cast(3000000000 as bigint), 1, 'ColumnId')")]
    [DataRow("select stats_date(object_id('t'), cast(3000000000 as bigint))")]
    public void IndexIdParameter_OutOfRange_RaisesMsg8115NamingInt(string sql)
        => new Simulation().AssertSqlError(
            $"create table t (id int not null primary key); {sql}",
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// The handful of parameters declared <c>smallint</c> name that narrower
    /// type instead — FILEGROUP_NAME's filegroup id, INDEXKEY_PROPERTY's key
    /// ordinal, and the minute offset SWITCHOFFSET / TODATETIMEOFFSET take.
    /// </summary>
    [TestMethod]
    [DataRow("select filegroup_name(cast(3000000000 as bigint))")]
    [DataRow("select switchoffset(sysdatetimeoffset(), cast(3000000000 as bigint))")]
    [DataRow("select todatetimeoffset(sysdatetime(), cast(3000000000 as bigint))")]
    public void SmallIntParameter_OutOfRange_RaisesMsg8115NamingSmallInt(string sql)
        => new Simulation().AssertSqlError(sql, 8115, "Arithmetic overflow error converting expression to data type smallint.");

    [TestMethod]
    public void SmallIntParameter_KeyOrdinalOutOfRange_RaisesMsg8115NamingSmallInt()
        => new Simulation().AssertSqlError(
            "create table t (id int not null primary key); select indexkey_property(object_id('t'), 1, cast(3000000000 as bigint), 'ColumnId')",
            8115,
            "Arithmetic overflow error converting expression to data type smallint.");

    /// <summary>
    /// An <c>int</c> argument narrowing to a <c>smallint</c> parameter takes
    /// the value-bearing Msg 220 instead, the same splinter CAST and column
    /// assignment use.
    /// </summary>
    [TestMethod]
    [DataRow("select filegroup_name(cast(40000 as int))")]
    [DataRow("select switchoffset(sysdatetimeoffset(), cast(40000 as int))")]
    public void SmallIntParameter_IntArgument_RaisesMsg220WithValue(string sql)
        => new Simulation().AssertSqlError(sql, 220, "Arithmetic overflow error for data type smallint, value = 40000.");

    /// <summary>
    /// The source type picks the error family: <c>float</c> gives the
    /// value-bearing Msg 232 and <c>money</c> the int-specific Msg 237.
    /// </summary>
    [TestMethod]
    public void FloatArgument_OutOfRange_RaisesMsg232()
        => Assert.Contains(
            "Arithmetic overflow error for type int, value = 2999999999999999",
            new Simulation().AssertSqlError("select col_name(cast(3e30 as float), 1)", 232).Message);

    [TestMethod]
    public void FloatArgument_SmallIntParameter_RaisesMsg232NamingSmallInt()
        => Assert.Contains(
            "Arithmetic overflow error for type smallint, value = 2999999999999999",
            new Simulation().AssertSqlError("select filegroup_name(cast(3e30 as float))", 232).Message);

    [TestMethod]
    public void MoneyArgument_OutOfRange_RaisesMsg237()
        => new Simulation().AssertSqlError(
            "select col_name(cast(3000000000 as money), 1)",
            237,
            "There is insufficient result space to convert a money value to int.");

    /// <summary>
    /// The date-part builders, EOMONTH's month offset and DATE_BUCKET's width
    /// share the same int-parameter treatment.
    /// </summary>
    [TestMethod]
    [DataRow("select eomonth(getdate(), cast(3000000000 as bigint))")]
    [DataRow("select date_bucket(day, cast(3000000000 as bigint), getdate())")]
    [DataRow("select datefromparts(cast(3000000000 as bigint), 1, 1)")]
    [DataRow("select datetimefromparts(2020, 1, 1, 1, 1, 1, cast(3000000000 as bigint))")]
    public void DateScalarArgument_OutOfRange_RaisesMsg8115(string sql)
        => new Simulation().AssertSqlError(sql, 8115, "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// The spatial index / SRID arguments — the member call, the constructor,
    /// and the <c>SET @g.STSrid</c> assignment form.
    /// </summary>
    [TestMethod]
    [DataRow("select geometry::Parse('LINESTRING(0 0, 1 1)').STPointN(cast(3000000000 as bigint)).ToString()")]
    [DataRow("select geometry::Point(1, 2, cast(3000000000 as bigint)).ToString()")]
    [DataRow("declare @g geometry = geometry::Point(1, 2, 0); set @g.STSrid = cast(3000000000 as bigint); select @g.STSrid")]
    public void SpatialArgument_OutOfRange_RaisesMsg8115(string sql)
        => new Simulation().AssertSqlError(sql, 8115, "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// <c>fn_varbintohexsubstring</c>'s offset and length are int; its leading
    /// flag is declared <c>bit</c>, so any non-zero magnitude reads as set
    /// rather than overflowing.
    /// </summary>
    [TestMethod]
    [DataRow("select master.dbo.fn_varbintohexsubstring(1, 0x1234, cast(3000000000 as bigint), 1)")]
    [DataRow("select master.dbo.fn_varbintohexsubstring(1, 0x1234, 1, cast(3000000000 as bigint))")]
    public void VarbinaryToHexArgument_OutOfRange_RaisesMsg8115(string sql)
        => new Simulation().AssertSqlError(sql, 8115, "Arithmetic overflow error converting expression to data type int.");

    [TestMethod]
    public void VarbinaryToHexFlag_OutOfIntRange_ReadsAsSet()
        => Assert.AreEqual(
            "0x12",
            new Simulation().ExecuteScalar("select master.dbo.fn_varbintohexsubstring(cast(3000000000 as bigint), 0x1234, 1, 1)"));

    [TestMethod]
    public void ConvertStyleArgument_OutOfRange_RaisesMsg8115()
        => new Simulation().AssertSqlError(
            "select convert(varchar(30), getdate(), cast(3000000000 as bigint))",
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// A system procedure's parameter reports differently from a function
    /// argument: Msg 8114 naming both the source family and the parameter's
    /// declared type — int for sp_getapplock's timeout, tinyint for
    /// sp_datatype_info_100's ODBC version.
    /// </summary>
    [TestMethod]
    public void ProcedureIntParameter_OutOfRange_RaisesMsg8114()
        => new Simulation().AssertSqlError(
            "declare @r int; exec @r = sp_getapplock @Resource = 'x', @LockMode = 'Exclusive', @LockTimeout = 3000000000",
            8114,
            "Error converting data type bigint to int.");

    [TestMethod]
    public void ProcedureTinyIntParameter_OutOfRange_RaisesMsg8114NamingTinyInt()
        => new Simulation().AssertSqlError(
            "exec sp_datatype_info_100 @ODBCVer = 3000000000",
            8114,
            "Error converting data type bigint to tinyint.");

    /// <summary>
    /// A FETCH offset *literal* past int range is a grammar-level failure
    /// (Msg 1080, class 15); the same value through a variable is accepted and
    /// simply positions past the end.
    /// </summary>
    [TestMethod]
    public void CursorFetchOffsetLiteral_OutOfIntRange_RaisesMsg1080()
        => new Simulation().AssertSqlError(
            "declare c cursor scroll for select 1 as n; open c; fetch absolute 3000000000 from c",
            1080,
            "The integer value 3000000000 is out of range.");

    [TestMethod]
    public void CursorFetchOffsetVariable_OutOfIntRange_ReturnsNoRow()
        => Assert.IsNull(new Simulation().ExecuteScalar("""
            declare @n bigint = 3000000000;
            declare c cursor scroll for select 1 as n;
            open c;
            fetch absolute @n from c;
            close c;
            deallocate c
            """));
}
