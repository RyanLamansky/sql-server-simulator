using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the rules governing how different types interact in expressions:
/// cross-family promotion in <c>SqlType.Promote</c>, implicit string→date/time
/// and integer→datetime/smalldatetime conversions in comparisons, and the
/// rejection paths (Msg 206 / 402 / 529 / 8117) that fire when types can't
/// reconcile. Per-type behavior (its own CAST round-trips, parameter binding,
/// equality at the same type) lives in the type's own test file; this file
/// hosts only the multi-type interaction surface.
/// </summary>
[TestClass]
public class TypePromotionTests
{
    // ─── String literal → date/time column ─────────────────────────────────

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
    {
        // SQL Server's data-type precedence puts every date/time type above
        // varchar/nvarchar, so a bare string literal on the equality's RHS
        // implicitly parses-and-coerces to the column's type — no cast needed.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( id int, d {columnType} )").ExecuteNonQuery();
        _ = connection.CreateCommand($"insert t values (1, '{seed}')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = $"select id from t where d = {literal}";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void StringLiteralPromotesToDateColumn_OrderingAlsoPromotes()
    {
        // The promotion rule is in Promote, not just equality, so it works
        // for ordering operators too.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-14'), (2, '2024-01-15'), (3, '2024-01-16')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d < '2024-01-16'";
        using var reader = select.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void BadStringLiteralAgainstDateColumn_RaisesMsg241()
    {
        // Bad-format strings surface from the existing parser path (Msg 241
        // for date / datetime / datetime2 / time / datetimeoffset; Msg 295
        // for smalldatetime — see neighboring test).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = 'not-a-date'";
        var ex = Throws<System.Data.Common.DbException>(() => select.ExecuteReader().Read());
        AreEqual("Conversion failed when converting date and/or time from character string.", ex.Message);
    }

    [TestMethod]
    public void BadStringLiteralAgainstSmallDateTimeColumn_RaisesMsg295()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d smalldatetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = 'not-a-date'";
        var ex = Throws<System.Data.Common.DbException>(() => select.ExecuteReader().Read());
        AreEqual("Conversion failed when converting character string to smalldatetime data type.", ex.Message);
    }

    [TestMethod]
    public void NVarcharStringLiteralPromotesIdentically()
    {
        // The N-prefix nvarchar literal goes through the same promotion path.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime2(7) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:30:45.1234567')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = N'2024-01-15 12:30:45.1234567'";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    // ─── Integer literal → datetime / smalldatetime ────────────────────────

    [TestMethod]
    public void IntLiteralPromotesToDateTimeColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d datetime)");
        _ = sim.ExecuteNonQuery("insert into t values (1, '1900-01-01'), (2, '1900-01-02')");
        using var reader = sim.ExecuteReader("select id from t where d = 0");
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void IntLiteralPromotesToSmallDateTimeColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d smalldatetime)");
        _ = sim.ExecuteNonQuery("insert into t values (1, '1900-01-01'), (2, '1900-01-02')");
        using var reader = sim.ExecuteReader("select id from t where d = 0");
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("date", "2024-01-15")]
    [DataRow("datetime2(3)", "2024-01-15")]
    [DataRow("time(0)", "12:00:00")]
    [DataRow("datetimeoffset(0)", "2024-01-15")]
    public void IntLiteralAgainstNonLegacyDateColumn_RaisesMsg206(string columnType, string seed)
    {
        // Only legacy datetime / smalldatetime accept implicit integer
        // promotion. Every other date/time type rejects the pair with
        // Msg 206 "Operand type clash" — matching SQL Server's behavior
        // for `where date = 0` etc.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( id int, d {columnType} )").ExecuteNonQuery();
        _ = connection.CreateCommand($"insert t values (1, '{seed}')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = 0";
        var ex = Throws<System.Data.Common.DbException>(() => select.ExecuteReader().Read());
        var rootType = columnType switch
        {
            "datetime2(3)" => "datetime2",
            "datetimeoffset(0)" => "datetimeoffset",
            "time(0)" => "time",
            _ => columnType,
        };
        AreEqual($"Operand type clash: {rootType} is incompatible with int", ex.Message);
    }

    // ─── Cross-family date/time promotion (within the date/time category) ──

    [TestMethod]
    public void CrossFamily_DateAndDateTime2_Equal()
    {
        // date promotes to datetime2(N); midnight on the date matches.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15'), (2, '2024-01-16')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 00:00:00' as datetime2(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void CrossFamily_DateAndDateTime_Equal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15' as datetime)";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_DateTimeAndDateTime2_Equal()
    {
        // datetime widens to datetime2(max(N, 3)); same instant matches.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:00:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:00:00' as datetime2(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_DateTimeAndDateTimeOffset_SameInstant_Equal()
    {
        // datetime is treated as +00:00 when promoted to datetimeoffset;
        // a UTC-equal value matches.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:00:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:00:00 +00:00' as datetimeoffset(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_DateTimeAndDateTimeOffset_DifferentOffset_NotEqual()
    {
        // dt is +00:00; dto is +07:00 with the same wall-clock — different
        // UTC instants, so the equality fails.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:00:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:00:00 +07:00' as datetimeoffset(7))";
        using var reader = select.ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void CrossFamily_DateAndDateTimeOffset_Equal()
    {
        // date promotes to dto with midnight +00:00.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 00:00:00 +00:00' as datetimeoffset(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_DateTime2AndDateTimeOffset_Equal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime2(3) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:00:00.500')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:00:00.500 +00:00' as datetimeoffset(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_SmallDateTimeAndDateTime_SameMinute_Equal()
    {
        // smalldatetime promotes to datetime; same minute (with no sub-minute
        // delta) matches.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d smalldatetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:30:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:30:00' as datetime)";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_SmallDateTimeAndDate_Equal()
    {
        // sd > date in the family precedence table, so the result is sd
        // (date widened to midnight).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d smalldatetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 00:00:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15' as date)";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_SmallDateTimeAndDateTime2_Equal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d smalldatetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:30:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:30:00.0000000' as datetime2(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    public void CrossFamily_SmallDateTimeAndDateTimeOffset_SameInstant_Equal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d smalldatetime )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15 12:30:00')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('2024-01-15 12:30:00 +00:00' as datetimeoffset(7))";
        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
    }

    [TestMethod]
    [DataRow("date")]
    [DataRow("datetime")]
    [DataRow("smalldatetime")]
    [DataRow("datetime2(3)")]
    [DataRow("datetimeoffset(3)")]
    public void CrossFamily_TimeVsNonTime_RaisesMsg402(string nonTimeType)
    {
        // SQL Server forbids time vs date/datetime/smalldatetime/datetime2/
        // datetimeoffset pairs in equality and ordering operators.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( id int, d {nonTimeType} )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, '2024-01-15')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = cast('00:00:00' as time)";
        var ex = Throws<System.Data.Common.DbException>(() => select.ExecuteReader().Read());
        var rootType = nonTimeType switch
        {
            "datetime2(3)" => "datetime2",
            "datetimeoffset(3)" => "datetimeoffset",
            _ => nonTimeType,
        };
        AreEqual($"The data types {rootType} and time are incompatible in the equal to operator.", ex.Message);
    }

    // ─── Date arithmetic rejection across non-legacy types ─────────────────

    [TestMethod]
    [DataRow("date", "2024-01-15", "2024-01-15")]
    [DataRow("datetime2", "2024-01-15", "2024-01-15")]
    [DataRow("time", "12:00:00", "12:00:00")]
    [DataRow("datetimeoffset", "2024-01-15", "2024-01-15")]
    public void Arithmetic_NonLegacyTypeAddedToSelf_RaisesMsg8117(string sourceType, string a, string b)
    {
        // Both operands non-legacy date types (including different non-
        // legacy types like `date + dt2`) → Msg 8117 with the LEFT operand's
        // type name only.
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select cast('{a}' as {sourceType}) + cast('{b}' as {sourceType})"));
        AreEqual($"Operand data type {sourceType} is invalid for add operator.", ex.Message);
    }

    [TestMethod]
    public void Arithmetic_DateAndDateTime2_RaisesMsg8117ForLeftType()
    {
        // Different non-legacy types: still Msg 8117, naming the LEFT side.
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar("select cast('2024-01-15' as date) + cast('2024-01-15' as datetime2)"));
        AreEqual("Operand data type date is invalid for add operator.", ex.Message);
    }

    [TestMethod]
    [DataRow("date", "2024-01-15")]
    [DataRow("datetime2", "2024-01-15")]
    [DataRow("datetimeoffset", "2024-01-15")]
    public void Arithmetic_LegacyAndNonLegacyDateType_RaisesMsg402(string nonLegacyType, string seed)
    {
        // Legacy datetime + non-legacy date type → Msg 402 with both names
        // and the operator embedded in the message.
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select cast('2024-01-15' as datetime) + cast('{seed}' as {nonLegacyType})"));
        AreEqual($"The data types datetime and {nonLegacyType} are incompatible in the add operator.", ex.Message);
    }

    [TestMethod]
    public void Arithmetic_LegacyAndTime_RaisesMsg402()
    {
        // Time + legacy datetime is Msg 402 (the comparison-side rule already
        // covered this for `=`; the add rule reuses the same Msg with a
        // different operator-name suffix).
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar("select cast('2024-01-15' as datetime) + cast('12:00:00' as time)"));
        AreEqual("The data types datetime and time are incompatible in the add operator.", ex.Message);
    }

    [TestMethod]
    public void Arithmetic_DateTimeMinusInt_OperatorNameIsSubtract()
    {
        // The Msg-402 wording embeds the operator name; verify the
        // subtraction path emits "subtract operator" rather than "add".
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar("select cast('2024-01-15' as datetime) - cast('2024-01-15' as date)"));
        AreEqual("The data types datetime and date are incompatible in the subtract operator.", ex.Message);
    }

    // ─── Integer ↔ string Promote (probe-confirmed against SQL Server 2025) ───

    [TestMethod]
    [DataRow("int", "5", "'5'")]
    [DataRow("int", "5", "'+5'")]      // sign prefix
    [DataRow("int", "-5", "'-5'")]
    [DataRow("int", "5", "' 5'")]      // leading whitespace
    [DataRow("int", "5", "'5 '")]      // trailing whitespace
    [DataRow("tinyint", "5", "'5'")]
    [DataRow("smallint", "5", "'5'")]
    [DataRow("bigint", "5", "'5'")]
    public void Comparison_IntegerEqualsString_ParsesAndMatches(string columnType, string seed, string literal)
    {
        // Per probe (SQL Server 2025): integer wins, string parses to the
        // integer's specific type. Whitespace trims; sign prefixes work.
        AreEqual(1, ExecuteScalar<int>($"select case when cast({seed} as {columnType}) = {literal} then 1 else 0 end"));
    }

    [TestMethod]
    public void Comparison_OperandOrderIndependent()
    {
        // 'lhs op rhs' must produce the same result as 'rhs op lhs'.
        AreEqual(1, ExecuteScalar<int>("select case when '5' = 5 then 1 else 0 end"));
        AreEqual(1, ExecuteScalar<int>("select case when 5 = '5' then 1 else 0 end"));
    }

    [TestMethod]
    [DataRow("int", "5", "''")]        // empty string parses to 0
    [DataRow("int", "0", "''")]        // 0 = '' is true (empty → 0)
    public void Comparison_EmptyString_ParsesToZero(string columnType, string seed, string literal)
    {
        // SQL Server's string→int CAST treats empty / whitespace-only as 0.
        var expectMatch = seed == "0";
        AreEqual(expectMatch ? 1 : 0, ExecuteScalar<int>($"select case when cast({seed} as {columnType}) = {literal} then 1 else 0 end"));
    }

    [TestMethod]
    [DataRow("'abc'")]
    [DataRow("'5.5'")]   // decimal-shaped: SQL Server does NOT route through decimal
    [DataRow("'5.0'")]   // decimal-shaped with trailing zero — still rejected
    [DataRow("'0x05'")]  // hex notation: rejected (only `0x` literal accepts hex)
    public void Comparison_UnparseableString_RaisesMsg245(string literal)
    {
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select case when 5 = {literal} then 1 else 0 end"));
        StartsWith("Conversion failed when converting the varchar value", ex.Message);
        Contains("to data type int", ex.Message);
    }

    [TestMethod]
    public void Comparison_NullIntegerVsString_IsUnknown()
    {
        // NULL on either side → UNKNOWN (the WHEN excludes; ELSE arm wins).
        AreEqual(-1, ExecuteScalar<int>("select case when cast(null as int) = '5' then 1 when not (cast(null as int) = '5') then 0 else -1 end"));
        AreEqual(-1, ExecuteScalar<int>("select case when 5 = cast(null as varchar(10)) then 1 when not (5 = cast(null as varchar(10))) then 0 else -1 end"));
    }

    [TestMethod]
    public void Comparison_BitVsStringForms_AllWorkThroughCastPath()
    {
        // bit ↔ string COMPARISON works through string→bit CAST: '1', 'true'/'TRUE'
        // map to true; '0', 'false', and empty map to false.
        AreEqual(1, ExecuteScalar<int>("select case when cast(1 as bit) = '1' then 1 else 0 end"));
        AreEqual(1, ExecuteScalar<int>("select case when cast(1 as bit) = 'true' then 1 else 0 end"));
        AreEqual(1, ExecuteScalar<int>("select case when cast(1 as bit) = 'TRUE' then 1 else 0 end"));
        AreEqual(1, ExecuteScalar<int>("select case when cast(0 as bit) = 'false' then 1 else 0 end"));
        AreEqual(1, ExecuteScalar<int>("select case when cast(0 as bit) = '' then 1 else 0 end"));
    }

    [TestMethod]
    [DataRow("+", 8)]
    [DataRow("-", 2)]
    [DataRow("*", 15)]
    [DataRow("/", 1)]    // 5 / 3 = 1 (integer division)
    [DataRow("%", 2)]    // 5 % 3 = 2
    public void Arithmetic_IntegerWithString_ProducesIntegerResult(string op, int expected)
    {
        AreEqual(expected, ExecuteScalar<int>($"select 5 {op} '3'"));
        AreEqual(expected, ExecuteScalar<int>($"select '5' {op} 3"));
    }

    [TestMethod]
    public void Arithmetic_TinyintPlusString_StaysTinyint()
    {
        // The integer's specific type is preserved. Encoding the result as
        // a wider int would mismatch the column's tinyint schema and the
        // row encoder would reject the write.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (cast(5 as tinyint) + '3')").ExecuteNonQuery();
        using var select = connection.CreateCommand();
        select.CommandText = "select v from t";
        AreEqual((byte)8, select.ExecuteScalar());
    }

    [TestMethod]
    public void Arithmetic_BigintWithStringRhs_StaysBigint()
    {
        // Mirror of the tinyint case for the wider integer end: bigint + str
        // returns bigint (verified against SQL Server 2025), not widened
        // through int. Insert into a bigint column to confirm the runtime
        // value's type matches the declared schema.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v bigint )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (cast(5 as bigint) + '3')").ExecuteNonQuery();
        using var select = connection.CreateCommand();
        select.CommandText = "select v from t";
        AreEqual(8L, select.ExecuteScalar());
    }

    [TestMethod]
    [DataRow("+", "add")]
    [DataRow("-", "subtract")]
    [DataRow("%", "modulo")]
    public void Arithmetic_BitWithString_AdditiveAndModulo_RaiseMsg402(string op, string operatorName)
    {
        // Bit comparison with string works (string→bit CAST), but bit
        // arithmetic with string is rejected: + / - / % use Msg 402 with
        // both type names and the operator word.
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select cast(1 as bit) {op} '1'"));
        AreEqual($"The data types bit and varchar are incompatible in the {operatorName} operator.", ex.Message);
    }

    [TestMethod]
    [DataRow("*", "multiply")]
    [DataRow("/", "divide")]
    public void Arithmetic_BitWithString_MultiplicativeAndDivisive_RaiseMsg8117(string op, string operatorName)
    {
        // Multiplicative ops use the date-style Msg 8117 instead of Msg 402,
        // naming only the LEFT operand's type.
        var leftEx = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select cast(1 as bit) {op} '1'"));
        AreEqual($"Operand data type bit is invalid for {operatorName} operator.", leftEx.Message);

        // Operand-order matters for Msg 8117: the message names the LEFT side.
        var rightEx = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select '1' {op} cast(1 as bit)"));
        AreEqual($"Operand data type varchar is invalid for {operatorName} operator.", rightEx.Message);
    }

    [TestMethod]
    public void Arithmetic_NullPropagation()
    {
        AreEqual(System.DBNull.Value, ExecuteScalar("select cast(null as int) + '3'"));
        AreEqual(System.DBNull.Value, ExecuteScalar("select 5 + cast(null as varchar(10))"));
    }

    [TestMethod]
    public void WhereClause_ColumnEqualsStringParameter_ParsesAndMatches()
    {
        // The EF Core / SqlClient pattern: bind a string parameter against
        // an int column. With Promote landing int, the comparison runs
        // through string→int CAST per row.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (5), (10), (15)").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where id = @p";
        var p = select.CreateParameter();
        p.ParameterName = "@p";
        p.DbType = System.Data.DbType.String;
        p.Value = "10";
        _ = select.Parameters.Add(p);

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(10, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void WhereClause_VarcharColumnComparedToInt_RaisesPerRowOnUnparseable()
    {
        // Reverse direction: varchar column compared to int literal. Per
        // probe (SQL Server 2025): a single unparseable row halts the whole
        // query — the failure isn't isolated to one UNKNOWN row.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( s varchar(10) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ('5'), ('abc'), ('15')").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select s from t where s = 5";
        var ex = Throws<System.Data.Common.DbException>(() =>
        {
            using var reader = select.ExecuteReader();
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
        // coalesce(int, varchar) returns int — the first arg's type drives
        // when it's non-null; the second is parsed to int if needed.
        AreEqual(5, ExecuteScalar<int>("select coalesce(5, '99')"));
        AreEqual(99, ExecuteScalar<int>("select coalesce(cast(null as int), '99')"));
    }

    [TestMethod]
    public void Case_ThenIntegerElseString_ResultIsInteger()
    {
        AreEqual(5, ExecuteScalar<int>("select case when 1=1 then 5 else '99' end"));
        AreEqual(99, ExecuteScalar<int>("select case when 1=0 then 5 else '99' end"));
    }

}
