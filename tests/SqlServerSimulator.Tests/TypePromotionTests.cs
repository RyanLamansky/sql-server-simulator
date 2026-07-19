using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the rules governing how different types interact in expressions:
/// cross-family promotion in <c>SqlType.Promote</c>, implicit string→date/time
/// and integer→datetime/smalldatetime conversions in comparisons, and the
/// rejection paths (Msg 206 / 402 / 529 / 8117) that fire when types can't
/// reconcile. Per-type behavior lives in the type's own test file; this file
/// hosts only the multi-type interaction surface.
/// </summary>
[TestClass]
public class TypePromotionTests
{
    [TestMethod]
    [DataRow("date", "2024-01-15", "'2024-01-15'")]
    [DataRow("date", "2024-01-15", "'20240115'")]
    [DataRow("datetime", "2024-01-15 12:30:45", "'2024-01-15 12:30:45'")]
    [DataRow("smalldatetime", "2024-01-15 12:30:00", "'2024-01-15 12:30'")]
    [DataRow("datetime2(7)", "2024-01-15 12:30:45.1234567", "'2024-01-15 12:30:45.1234567'")]
    [DataRow("datetime2(3)", "2024-01-15 12:30:45.123", "'2024-01-15T12:30:45.123'")]
    [DataRow("time(0)", "12:30:45", "'12:30:45'")]
    [DataRow("datetimeoffset(0)", "2024-01-15 12:30:45 +00:00", "'2024-01-15 12:30:45 +00:00'")]
    public void StringLiteralPromotesToDateTimeColumn(string columnType, string seed, string literal)
        => AssertColumnEquals(columnType, seed, literal, expectMatch: true);

    // Promotion is in Promote, not just equality, so it works for ordering operators.
    [TestMethod]
    public void StringLiteralPromotesToDateColumn_OrderingAlsoPromotes()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( id int, d date );
            insert t values (1, '2024-01-14'), (2, '2024-01-15'), (3, '2024-01-16')
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id from t where d < '2024-01-16'").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void BadStringLiteralAgainstDateColumn_RaisesMsg241()
        => AssertWhereError("date", "2024-01-15", "d = 'not-a-date'",
            "Conversion failed when converting date and/or time from character string.");

    [TestMethod]
    public void BadStringLiteralAgainstSmallDateTimeColumn_RaisesMsg295()
        => AssertWhereError("smalldatetime", "2024-01-15", "d = 'not-a-date'",
            "Conversion failed when converting character string to smalldatetime data type.");

    [TestMethod]
    public void NVarcharStringLiteralPromotesIdentically()
        => AssertColumnEquals("datetime2(7)", "2024-01-15 12:30:45.1234567",
            "N'2024-01-15 12:30:45.1234567'", expectMatch: true);

    [TestMethod]
    [DataRow("datetime")]
    [DataRow("smalldatetime")]
    public void IntLiteralPromotesToLegacyDateTimeColumn(string columnType)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table t (id int, d {columnType});
            insert t values (1, '1900-01-01'), (2, '1900-01-02')
            """);
        using var reader = sim.ExecuteReader("select id from t where d = 0");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("date", "2024-01-15", "date")]
    [DataRow("datetime2(3)", "2024-01-15", "datetime2")]
    [DataRow("time(0)", "12:00:00", "time")]
    [DataRow("datetimeoffset(0)", "2024-01-15", "datetimeoffset")]
    public void IntLiteralAgainstNonLegacyDateColumn_RaisesMsg206(string columnType, string seed, string rootType)
        => AssertWhereError(columnType, seed, "d = 0", $"Operand type clash: {rootType} is incompatible with int");

    [TestMethod]
    [DataRow("date", "2024-01-15", "cast('2024-01-15 00:00:00' as datetime2(7))", true)]
    [DataRow("date", "2024-01-15", "cast('2024-01-15' as datetime)", true)]
    [DataRow("date", "2024-01-15", "cast('2024-01-15 00:00:00 +00:00' as datetimeoffset(7))", true)]
    [DataRow("datetime", "2024-01-15 12:00:00", "cast('2024-01-15 12:00:00' as datetime2(7))", true)]
    [DataRow("datetime", "2024-01-15 12:00:00", "cast('2024-01-15 12:00:00 +00:00' as datetimeoffset(7))", true)]
    [DataRow("datetime", "2024-01-15 12:00:00", "cast('2024-01-15 12:00:00 +07:00' as datetimeoffset(7))", false)]
    [DataRow("datetime2(3)", "2024-01-15 12:00:00.500", "cast('2024-01-15 12:00:00.500 +00:00' as datetimeoffset(7))", true)]
    [DataRow("smalldatetime", "2024-01-15 12:30:00", "cast('2024-01-15 12:30:00' as datetime)", true)]
    [DataRow("smalldatetime", "2024-01-15 00:00:00", "cast('2024-01-15' as date)", true)]
    [DataRow("smalldatetime", "2024-01-15 12:30:00", "cast('2024-01-15 12:30:00.0000000' as datetime2(7))", true)]
    [DataRow("smalldatetime", "2024-01-15 12:30:00", "cast('2024-01-15 12:30:00 +00:00' as datetimeoffset(7))", true)]
    public void CrossFamily_DateTimeEquality(string columnType, string seed, string rhs, bool expectMatch)
        => AssertColumnEquals(columnType, seed, rhs, expectMatch);

    [TestMethod]
    [DataRow("date", "date")]
    [DataRow("datetime", "datetime")]
    [DataRow("smalldatetime", "smalldatetime")]
    [DataRow("datetime2(3)", "datetime2")]
    [DataRow("datetimeoffset(3)", "datetimeoffset")]
    public void CrossFamily_TimeVsNonTime_RaisesMsg402(string nonTimeType, string rootType)
        => AssertWhereError(nonTimeType, "2024-01-15", "d = cast('00:00:00' as time)",
            $"The data types {rootType} and time are incompatible in the equal to operator.");

    [TestMethod]
    [DataRow("date", "2024-01-15", "2024-01-15")]
    [DataRow("datetime2", "2024-01-15", "2024-01-15")]
    [DataRow("time", "12:00:00", "12:00:00")]
    [DataRow("datetimeoffset", "2024-01-15", "2024-01-15")]
    public void Arithmetic_NonLegacyTypeAddedToSelf_RaisesMsg8117(string sourceType, string a, string b)
        => AssertSqlMessage($"select cast('{a}' as {sourceType}) + cast('{b}' as {sourceType})",
            $"Operand data type {sourceType} is invalid for add operator.");

    [TestMethod]
    public void Arithmetic_DateAndDateTime2_RaisesMsg8117ForLeftType()
        => AssertSqlMessage("select cast('2024-01-15' as date) + cast('2024-01-15' as datetime2)",
            "Operand data type date is invalid for add operator.");

    [TestMethod]
    [DataRow("date", "2024-01-15")]
    [DataRow("datetime2", "2024-01-15")]
    [DataRow("datetimeoffset", "2024-01-15")]
    public void Arithmetic_LegacyAndNonLegacyDateType_RaisesMsg402(string nonLegacyType, string seed)
        => AssertSqlMessage($"select cast('2024-01-15' as datetime) + cast('{seed}' as {nonLegacyType})",
            $"The data types datetime and {nonLegacyType} are incompatible in the add operator.");

    [TestMethod]
    public void Arithmetic_LegacyAndTime_RaisesMsg402()
        => AssertSqlMessage("select cast('2024-01-15' as datetime) + cast('12:00:00' as time)",
            "The data types datetime and time are incompatible in the add operator.");

    [TestMethod]
    public void Arithmetic_DateTimeMinusInt_OperatorNameIsSubtract()
        => AssertSqlMessage("select cast('2024-01-15' as datetime) - cast('2024-01-15' as date)",
            "The data types datetime and date are incompatible in the subtract operator.");

    [TestMethod]
    [DataRow("int", "5", "'5'")]
    [DataRow("int", "5", "'+5'")]
    [DataRow("int", "-5", "'-5'")]
    [DataRow("int", "5", "' 5'")]
    [DataRow("int", "5", "'5 '")]
    [DataRow("tinyint", "5", "'5'")]
    [DataRow("smallint", "5", "'5'")]
    [DataRow("bigint", "5", "'5'")]
    public void Comparison_IntegerEqualsString_ParsesAndMatches(string columnType, string seed, string literal)
        => AreEqual(1, ExecuteScalar<int>($"select case when cast({seed} as {columnType}) = {literal} then 1 else 0 end"));

    [TestMethod]
    public void Comparison_OperandOrderIndependent()
    {
        AreEqual(1, ExecuteScalar<int>("select case when '5' = 5 then 1 else 0 end"));
        AreEqual(1, ExecuteScalar<int>("select case when 5 = '5' then 1 else 0 end"));
    }

    [TestMethod]
    [DataRow("int", "5", "''")]
    [DataRow("int", "0", "''")]
    public void Comparison_EmptyString_ParsesToZero(string columnType, string seed, string literal)
    {
        var expectMatch = seed == "0";
        AreEqual(expectMatch ? 1 : 0, ExecuteScalar<int>($"select case when cast({seed} as {columnType}) = {literal} then 1 else 0 end"));
    }

    [TestMethod]
    [DataRow("'abc'")]
    [DataRow("'5.5'")]   // decimal-shaped: SQL Server does NOT route through decimal
    [DataRow("'5.0'")]
    [DataRow("'0x05'")]  // hex notation: only 0x literal accepts hex
    public void Comparison_UnparseableString_RaisesMsg245(string literal)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select case when 5 = {literal} then 1 else 0 end"));
        StartsWith("Conversion failed when converting the varchar value", ex.Message);
        Contains("to data type int", ex.Message);
    }

    [TestMethod]
    public void Comparison_NullIntegerVsString_IsUnknown()
    {
        AreEqual(-1, ExecuteScalar<int>("select case when cast(null as int) = '5' then 1 when not (cast(null as int) = '5') then 0 else -1 end"));
        AreEqual(-1, ExecuteScalar<int>("select case when 5 = cast(null as varchar(10)) then 1 when not (5 = cast(null as varchar(10))) then 0 else -1 end"));
    }

    [TestMethod]
    [DataRow("'1'", 1)]
    [DataRow("'true'", 1)]
    [DataRow("'TRUE'", 1)]
    [DataRow("'false'", 0)]
    [DataRow("''", 0)]
    public void Comparison_BitVsStringForms_AllWorkThroughCastPath(string literal, int bitValue)
        => AreEqual(1, ExecuteScalar<int>($"select case when cast({bitValue} as bit) = {literal} then 1 else 0 end"));

    [TestMethod]
    [DataRow("+", 8)]
    [DataRow("-", 2)]
    [DataRow("*", 15)]
    [DataRow("/", 1)]
    [DataRow("%", 2)]
    public void Arithmetic_IntegerWithString_ProducesIntegerResult(string op, int expected)
    {
        AreEqual(expected, ExecuteScalar<int>($"select 5 {op} '3'"));
        AreEqual(expected, ExecuteScalar<int>($"select '5' {op} 3"));
    }

    [TestMethod]
    [DataRow("tinyint", "5", "'3'", (byte)8)]
    [DataRow("bigint", "5", "'3'", 8L)]
    // Encoding the result as a wider int would mismatch the column's declared
    // schema and the row encoder would reject the write.
    public void Arithmetic_IntegerSpecificTypePreservedThroughInsert(string columnType, string a, string b, object expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t ( v {columnType} );
            insert t values (cast({a} as {columnType}) + {b});
            select v from t
            """));

    [TestMethod]
    [DataRow("+", "add")]
    [DataRow("-", "subtract")]
    [DataRow("%", "modulo")]
    public void Arithmetic_BitWithString_AdditiveAndModulo_RaiseMsg402(string op, string operatorName)
        => AssertSqlMessage($"select cast(1 as bit) {op} '1'",
            $"The data types bit and varchar are incompatible in the {operatorName} operator.");

    [TestMethod]
    [DataRow("*", "multiply")]
    [DataRow("/", "divide")]
    public void Arithmetic_BitWithString_MultiplicativeAndDivisive_RaiseMsg8117(string op, string operatorName)
    {
        // Msg 8117 names only the LEFT operand's type — operand-order matters.
        AssertSqlMessage($"select cast(1 as bit) {op} '1'", $"Operand data type bit is invalid for {operatorName} operator.");
        AssertSqlMessage($"select '1' {op} cast(1 as bit)", $"Operand data type varchar is invalid for {operatorName} operator.");
    }

    [TestMethod]
    public void Arithmetic_NullPropagation()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select cast(null as int) + '3'"));
        AreEqual(DBNull.Value, ExecuteScalar("select 5 + cast(null as varchar(10))"));
    }

    [TestMethod]
    public void WhereClause_ColumnEqualsStringParameter_ParsesAndMatches()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( id int );
            insert t values (5), (10), (15)
            """).ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where id = @p";
        var p = select.CreateParameter();
        p.ParameterName = "@p";
        p.DbType = DbType.String;
        p.Value = "10";
        _ = select.Parameters.Add(p);

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(10, reader[0]);
        IsFalse(reader.Read());
    }

    // Per probe: a single unparseable row halts the whole query — failure isn't isolated.
    [TestMethod]
    public void WhereClause_VarcharColumnComparedToInt_RaisesPerRowOnUnparseable()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( s varchar(10) );
            insert t values ('5'), ('abc'), ('15')
            """).ExecuteNonQuery();

        var ex = Throws<DbException>(() =>
        {
            using var reader = connection.CreateCommand("select s from t where s = 5").ExecuteReader();
            while (reader.Read()) { }
        });
        StartsWith("Conversion failed when converting the varchar value 'abc'", ex.Message);
    }

    [TestMethod]
    public void InList_IntegerLhsWithStringValues_Works()
    {
        AreEqual(1, ExecuteScalar<int>("select case when 5 in ('1','5','9') then 1 else 0 end"));
        AreEqual(0, ExecuteScalar<int>("select case when 5 in ('1','9') then 1 else 0 end"));
    }

    [TestMethod]
    public void Coalesce_IntegerAndString_ResultIsInteger()
    {
        AreEqual(5, ExecuteScalar<int>("select coalesce(5, '99')"));
        AreEqual(99, ExecuteScalar<int>("select coalesce(cast(null as int), '99')"));
    }

    [TestMethod]
    public void Case_ThenIntegerElseString_ResultIsInteger()
    {
        AreEqual(5, ExecuteScalar<int>("select case when 1=1 then 5 else '99' end"));
        AreEqual(99, ExecuteScalar<int>("select case when 1=0 then 5 else '99' end"));
    }

    private static void AssertColumnEquals(string columnType, string seedValue, string rhsExpression, bool expectMatch)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"""
            create table t ( id int, d {columnType} );
            insert t values (1, '{seedValue}')
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand($"select id from t where d = {rhsExpression}").ExecuteReader();
        if (expectMatch)
        {
            IsTrue(reader.Read());
            AreEqual(1, reader[0]);
            IsFalse(reader.Read());
        }
        else
        {
            IsFalse(reader.Read());
        }
    }

    private static void AssertWhereError(string columnType, string seedValue, string predicate, string expectedMessage)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"""
            create table t ( id int, d {columnType} );
            insert t values (1, '{seedValue}')
            """).ExecuteNonQuery();

        var ex = Throws<DbException>(() => connection.CreateCommand($"select id from t where {predicate}").ExecuteReader().Read());
        AreEqual(expectedMessage, ex.Message);
    }
}
