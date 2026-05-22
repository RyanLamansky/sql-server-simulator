using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server implicit-casts non-string operands of the string scalars
/// (LEN / LOWER / UPPER / LTRIM / RTRIM / REVERSE / LEFT / RIGHT / REPLACE)
/// to <c>varchar</c>, and string operands of the math scalars
/// (ABS / CEILING / FLOOR / SIGN / SQRT / DEGREES / RADIANS) to
/// <c>float</c>. DATEPART / DATEADD / DATEDIFF accept string operands
/// (parsed as <c>datetime2(7)</c>) and integer operands (interpreted as
/// days-since-1900-01-01, i.e. legacy <c>datetime</c>) for the date
/// argument. Every assertion below was probe-confirmed against SQL
/// Server 2025 on 2026-05-22.
/// </summary>
[TestClass]
public sealed class ImplicitCoercionTests
{
    [TestMethod]
    [DataRow("len(12345)", 5)]
    [DataRow("len(cast(12345 as bigint))", 5)]
    [DataRow("len(cast(123.45 as decimal(5,2)))", 6)]
    [DataRow("len(cast('2024-01-15' as date))", 10)]
    [DataRow("len(cast(12.5 as float))", 4)]
    public void Len_NonString_ImplicitCoerce(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("lower(12345)", "12345")]
    [DataRow("lower(cast(12345 as bigint))", "12345")]
    [DataRow("lower(cast(123.45 as decimal(5,2)))", "123.45")]
    [DataRow("lower(cast('2024-01-15' as date))", "2024-01-15")]
    [DataRow("upper(12345)", "12345")]
    [DataRow("upper(cast('2024-01-15' as date))", "2024-01-15")]
    [DataRow("ltrim(12345)", "12345")]
    [DataRow("rtrim(12345)", "12345")]
    [DataRow("reverse(12345)", "54321")]
    [DataRow("reverse(cast('2024-01-15' as date))", "51-10-4202")]
    [DataRow("left(12345, 3)", "123")]
    [DataRow("left(cast('2024-01-15' as date), 4)", "2024")]
    [DataRow("right(12345, 3)", "345")]
    [DataRow("right(cast('2024-01-15' as date), 2)", "15")]
    [DataRow("replace(12345, 2, 9)", "19345")]
    [DataRow("replace(cast('2024-01-15' as date), '-', '/')", "2024/01/15")]
    public void StringScalar_NonString_ImplicitCoerce(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("abs('-5')", 5.0)]
    [DataRow("abs('-5.5')", 5.5)]
    [DataRow("abs(N'-5')", 5.0)]
    [DataRow("abs('  -5  ')", 5.0)]
    [DataRow("ceiling('5.5')", 6.0)]
    [DataRow("ceiling('-5.5')", -5.0)]
    [DataRow("floor('5.5')", 5.0)]
    [DataRow("floor('-5.5')", -6.0)]
    [DataRow("sign('-5')", -1.0)]
    [DataRow("sign('5')", 1.0)]
    [DataRow("sign('0')", 0.0)]
    [DataRow("sqrt('16')", 4.0)]
    [DataRow("radians('180')", 3.141592653589793)]
    public void MathScalar_String_ImplicitCoerce(string expression, double expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Degrees_String_ImplicitCoerce() =>
        AreEqual(57.29577951308232, ExecuteScalar("select degrees('1')"));

    [TestMethod]
    [DataRow("log('10')", 2.302585092994046)]
    [DataRow("log10('100')", 2.0)]
    [DataRow("exp('1')", 2.718281828459045)]
    [DataRow("square('3')", 9.0)]
    [DataRow("sin('0')", 0.0)]
    [DataRow("cos('0')", 1.0)]
    [DataRow("tan('0')", 0.0)]
    [DataRow("asin('0')", 0.0)]
    [DataRow("acos('1')", 0.0)]
    [DataRow("atan('0')", 0.0)]
    [DataRow("atn2('0', '1')", 0.0)]
    [DataRow("cot('1')", 0.6420926159343306)]
    // Choose a value that doesn't sit on the float-representation boundary
    // (5.55 → 5.5500000000000007 in IEEE 754, so ROUND-half-away-from-zero
    // diverges between engines depending on the multiply-by-10 strategy).
    [DataRow("round('5.4', 0)", 5.0)]
    public void MathSibling_String_ImplicitCoerce(string expression, double expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    /// <summary>
    /// log(value, base) with both args string — real coerces both to float.
    /// </summary>
    [TestMethod]
    public void Log_StringBase_ImplicitCoerce() =>
        AreEqual(3.0, ExecuteScalar("select log('8', '2')"));

    /// <summary>
    /// POWER's first arg drives the result type: string base widens to float.
    /// </summary>
    [TestMethod]
    public void Power_StringBase_ProjectsAsFloat() =>
        AreEqual(8.0, ExecuteScalar("select power('2', 3)"));

    /// <summary>
    /// POWER(int, string) preserves int result type (truncates toward zero).
    /// </summary>
    [TestMethod]
    public void Power_StringExponent_PreservesIntBase() =>
        AreEqual(8, ExecuteScalar("select power(2, '3')"));

    [TestMethod]
    public void Round_NonIntegerLength_RaisesMsg8116()
    {
        // ROUND's length arg stays strict-int — Msg 8116 on string.
        var ex = Throws<DbException>(() => ExecuteScalar("select round(5.55, '1')"));
        Assert.Contains("Argument data type", ex.Message);
        Assert.Contains("argument 2 of round", ex.Message);
    }

    [TestMethod]
    public void MathScalar_BadString_RaisesConversionError()
    {
        // ABS('abc') / CEILING('abc') route through the string-to-float
        // parser; bad text produces Msg 8114 ("Error converting data type
        // varchar to float.") — same code real SQL Server raises.
        var ex = Throws<DbException>(() => ExecuteScalar("select abs('abc')"));
        Assert.Contains("Error converting data type", ex.Message);
    }

    [TestMethod]
    [DataRow("datepart(year, '2024-01-15')", 2024)]
    [DataRow("datepart(year, N'2024-01-15')", 2024)]
    [DataRow("datepart(year, 0)", 1900)]
    [DataRow("datepart(year, 100)", 1900)]
    [DataRow("datepart(year, cast(45000 as bigint))", 2023)]
    [DataRow("datediff(day, '2024-01-01', '2024-01-31')", 30)]
    [DataRow("datediff(day, 0, '2024-01-31')", 45320)]
    [DataRow("datediff(day, '2024-01-01', 100)", -45190)]
    public void DateFunction_StringAndIntegerOperands_ImplicitCoerce(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void DateAdd_String_ImplicitCoerce() =>
        AreEqual(new DateTime(2024, 1, 16), ExecuteScalar("select dateadd(day, 1, '2024-01-15')"));

    [TestMethod]
    public void DateAdd_Integer_ImplicitCoerce() =>
        AreEqual(new DateTime(1900, 1, 2), ExecuteScalar("select dateadd(day, 1, 0)"));

    [TestMethod]
    public void DateAdd_LargeInteger_ImplicitCoerce() =>
        AreEqual(new DateTime(1900, 4, 12), ExecuteScalar("select dateadd(day, 1, 100)"));

    [TestMethod]
    [DataRow("charindex('2', 12345)", 2)]
    [DataRow("charindex('5', cast(12345 as bigint))", 5)]
    [DataRow("charindex('-', cast('2024-01-15' as date))", 5)]
    public void CharIndex_Haystack_ImplicitCoerce(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void CharIndex_NonStringNeedle_StaysStrict()
    {
        // Real SQL Server rejects non-string needle (arg 1); simulator matches.
        var ex = Throws<DbException>(() => ExecuteScalar("select charindex(2, 12345)"));
        Assert.Contains("argument 1 of charindex", ex.Message);
    }

    [TestMethod]
    [DataRow("stuff('abcde', 2, 1, 99)", "a99cde")]
    [DataRow("stuff(99, 2, 1, 99)", "999")]
    [DataRow("stuff('abcde', 2, 1, cast('2024-01-15' as date))", "a2024-01-15cde")]
    public void Stuff_NonString_ImplicitCoerce(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("len(0x4142202020)", 2)]                              // CP1252-space bytes trim
    [DataRow("len(0x00)", 1)]                                       // null byte isn't a space
    [DataRow("len(0x20)", 0)]                                       // single space byte trims away
    [DataRow("len(0x4100)", 2)]                                     // 'A' + trailing null stays at 2
    [DataRow("len(cast(0x4142202020 as varbinary(10)))", 2)]
    [DataRow("len(cast(0x4142202020 as binary(10)))", 10)]          // binary zero-padding isn't trimmed
    [DataRow("len(cast('abc' as varbinary(10)))", 3)]               // string→varbinary via the new CoerceTo path
    public void Len_Binary_ImplicitCoerce(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("lower(0x414243)", "abc")]                              // 'ABC' bytes → 'abc'
    [DataRow("upper(0x616263)", "ABC")]                              // 'abc' bytes → 'ABC'
    [DataRow("ltrim(0x202041)", "A")]
    [DataRow("rtrim(0x412020)", "A")]
    [DataRow("reverse(0x414243)", "CBA")]
    public void StringScalar_Binary_ImplicitCoerce(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Len_Image_StaysRejected()
    {
        // Image (legacy LOB form of varbinary) stays rejected — real SQL
        // Server raises Msg 8116 on the implicit-coerce path; the
        // simulator's StringScalars.IsCoerceableToVarchar deliberately
        // excludes image to match.
        var ex = Throws<DbException>(() => ExecuteScalar("select len(cast(0x010203 as image))"));
        Assert.Contains("argument 1 of len", ex.Message);
    }

    [TestMethod]
    [DataRow("replicate(12345, 2)", "1234512345")]
    [DataRow("replicate(cast(12 as bigint), 2)", "1212")]
    [DataRow("replicate(cast('2024-01-15' as date), 2)", "2024-01-152024-01-15")]
    public void Replicate_NonString_ImplicitCoerce(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void StringScalar_NullThroughNonStringSource()
    {
        // NULL of a non-string type still passes through the implicit-cast
        // path — the function returns NULL of the post-coerce type without
        // raising on the unsupported input.
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select len(cast(null as int))"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select lower(cast(null as date))"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select abs(cast(null as varchar(10)))"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select datepart(year, cast(null as varchar(20)))"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select dateadd(day, 1, cast(null as int))"));
    }
}
