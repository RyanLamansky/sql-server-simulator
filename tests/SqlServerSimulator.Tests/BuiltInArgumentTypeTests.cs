using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A built-in's argument converts to the type its parameter is declared as
/// the way an assignment does, settled while compiling; probed 2026-09-25
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BuiltInArgumentTypeTests
{
    [TestMethod]
    [DataRow("abs(cast('2020-01-01' as date))", "date")]
    [DataRow("ceiling(0x01)", "varbinary")]
    [DataRow("floor(cast('<a/>' as xml))", "xml")]
    [DataRow("sign(newid())", "uniqueidentifier")]
    [DataRow("sqrt(cast('10:00' as time))", "time")]
    [DataRow("sin(0x01)", "varbinary")]
    [DataRow("square(0x01)", "varbinary")]
    [DataRow("atn2(1, 0x01)", "varbinary")]
    [DataRow("power(2, 0x01)", "varbinary")]
    [DataRow("power(0x01, 2)", "varbinary")]
    [DataRow("log(2, 0x01)", "varbinary")]
    [DataRow("round(0x01, 0)", "varbinary")]
    [DataRow("degrees(0x01)", "varbinary")]
    [DataRow("str(0x01)", "varbinary")]
    public void MathArgument_WithNoConversionToFloat_RaisesMsg206(string call, string type)
        => new Simulation().AssertSqlError($"select {call}", 206, $"Operand type clash: {type} is incompatible with float");

    [TestMethod]
    [DataRow("char(cast('2020-01-01' as date))")]
    [DataRow("nchar(cast('2020-01-01' as date))")]
    [DataRow("space(cast('2020-01-01' as date))")]
    [DataRow("replicate('a', cast('2020-01-01' as date))")]
    [DataRow("left('a', cast('2020-01-01' as date))")]
    [DataRow("right('a', cast('2020-01-01' as date))")]
    [DataRow("eomonth('2020-01-01', cast('2020-01-01' as date))")]
    public void CountArgument_WithNoConversionToInt_RaisesMsg206(string call)
        => new Simulation().AssertSqlError($"select {call}", 206, "Operand type clash: date is incompatible with int");

    [TestMethod]
    [DataRow("year(newid())")]
    [DataRow("datepart(month, newid())")]
    [DataRow("datename(month, newid())")]
    [DataRow("dateadd(day, 1, newid())")]
    [DataRow("datediff(day, newid(), '2020-01-01')")]
    public void DateArgument_WithNoConversionToDatetime_RaisesMsg206(string call)
        => new Simulation().AssertSqlError($"select {call}", 206, "Operand type clash: uniqueidentifier is incompatible with datetime");

    [TestMethod]
    [DataRow("abs(cast(1 as sql_variant))", "float")]
    [DataRow("char(cast(1 as sql_variant))", "int")]
    [DataRow("year(cast(1 as sql_variant))", "datetime")]
    public void SqlVariantArgument_RaisesMsg257(string call, string target)
        => new Simulation().AssertSqlError($"select {call}", 257, $"Implicit conversion from data type sql_variant to {target} is not allowed. Use the CONVERT function to run this query.");

    [TestMethod]
    public void SqlVariantColumnArgument_OverAnEmptyTable_RaisesMsg260()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table e (v sql_variant, d date)");
        simulation.AssertSqlError("select sqrt(v) from e", 260, "Disallowed implicit conversion from data type sql_variant to data type float, table 'e', column 'v'. Use the CONVERT function to run this query.");
        simulation.AssertSqlError("select char(d) from e", 206, "Operand type clash: date is incompatible with int");
    }

    [TestMethod]
    public void AcceptedArguments_StillAnswer()
        => AreEqual("1|2.00|0.5|A|", new Simulation().ExecuteScalar("select concat(abs(cast(1 as bit)), '|', ceiling(cast(1.5 as money)), '|', sqrt('0.25'), '|', char(0x41), '|', year(null))"));

    [TestMethod]
    [DataRow("year(1.5)", 1900)]
    [DataRow("year(0x01)", 1900)]
    [DataRow("day(1.5)", 2)]
    [DataRow("day(cast(1 as money))", 2)]
    [DataRow("day(1.5e0)", 2)]
    [DataRow("datepart(hour, 1.5)", 12)]
    [DataRow("datediff(day, 1.5, '2020-01-01')", 43828)]
    [DataRow("datediff(day, 0x01, '2020-01-01')", 43829)]
    public void DateArgument_FromANumberOrBinary_ReadsAsDatetime(string call, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {call}"));

    [TestMethod]
    public void DateAdd_OverANumber_AnswersDatetime()
        => AreEqual(new DateTime(1900, 1, 3, 12, 0, 0), new Simulation().ExecuteScalar("select dateadd(day, 1, 1.5)"));

    [TestMethod]
    public void DateName_OverANumber_ReadsAsDatetime()
        => AreEqual("January", new Simulation().ExecuteScalar("select datename(month, cast(1 as money))"));

    [TestMethod]
    [DataRow("datetrunc(day, 1.5)", "numeric", 2, "datetrunc")]
    [DataRow("datetrunc(day, 0x01)", "varbinary", 2, "datetrunc")]
    [DataRow("datetrunc(day, cast('<a/>' as xml))", "xml", 2, "datetrunc")]
    [DataRow("datetrunc(day, cast(null as int))", "int", 2, "datetrunc")]
    [DataRow("date_bucket(day, 1, 1)", "int", 3, "Date_Bucket")]
    [DataRow("date_bucket(day, 1, cast(1 as sql_variant))", "sql_variant", 3, "Date_Bucket")]
    [DataRow("eomonth(1e0)", "float", 1, "eomonth")]
    [DataRow("eomonth(cast('10:00' as time))", "time", 1, "eomonth")]
    [DataRow("eomonth(cast('<a/>' as xml))", "xml", 1, "eomonth")]
    public void DateOnlyFunction_OverANonDate_RaisesMsg8116(string call, string type, int index, string function)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument {index} of {function} function.");

    [TestMethod]
    public void DateOnlyFunction_OverAnEmptyTablesIntColumn_RaisesMsg8116()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table e (i int)");
        simulation.AssertSqlError("select datetrunc(day, i) from e", 8116, "Argument data type int is invalid for argument 2 of datetrunc function.");
    }

    [TestMethod]
    public void DateOnlyFunction_OverAStringOrNull_StillAnswers()
        => AreEqual("2020-01-31|2020-01-05 00:00:00.0000000||", new Simulation().ExecuteScalar("select concat(eomonth('2020-01-05'), '|', datetrunc(day, '2020-01-05 10:00'), '|', datetrunc(day, null), '|', eomonth(null))"));

    // ---- the string scalars ----

    [TestMethod]
    [DataRow("len(cast('<a/>' as xml))", "xml", 1, "len")]
    [DataRow("upper(cast('<a/>' as xml))", "xml", 1, "upper")]
    [DataRow("left(cast('<a/>' as xml), 1)", "xml", 1, "left")]
    [DataRow("substring(cast('<a/>' as xml), 1, 1)", "xml", 1, "substring")]
    [DataRow("replace('a', 'a', cast('<a/>' as xml))", "xml", 3, "replace")]
    [DataRow("stuff('a', 1, 1, cast('<a/>' as xml))", "xml", 4, "stuff")]
    [DataRow("patindex('%a%', cast('<a/>' as xml))", "xml", 2, "patindex")]
    [DataRow("ascii(cast(1 as sql_variant))", "sql_variant", 1, "ascii")]
    [DataRow("soundex(cast(1 as sql_variant))", "sql_variant", 1, "soundex")]
    [DataRow("translate('a', 'a', cast(1 as sql_variant))", "sql_variant", 3, "translate")]
    [DataRow("ltrim('a', cast(1 as sql_variant))", "sql_variant", 2, "ltrim")]
    [DataRow("trim(1.5)", "numeric", 1, "Trim")]
    [DataRow("trim(cast(1 as bit) from 'a')", "bit", 1, "Trim")]
    [DataRow("substring(1.5, 1, 1)", "numeric", 1, "substring")]
    [DataRow("charindex(1.5, 'a')", "numeric", 1, "charindex")]
    [DataRow("string_escape(1.5, 'json')", "numeric", 1, "string_escape")]
    [DataRow("isjson(1)", "int", 1, "isjson")]
    [DataRow("isjson(cast('a' as text))", "text", 1, "isjson")]
    [DataRow("json_value(0x41, '$.a')", "varbinary", 1, "json_value")]
    [DataRow("compress(1.5)", "numeric", 1, "Compress")]
    [DataRow("compress(cast('a' as text))", "text", 1, "Compress")]
    [DataRow("decompress('a')", "varchar", 1, "Decompress")]
    [DataRow("isnumeric(cast('2020-01-01' as date))", "date", 1, "isnumeric")]
    [DataRow("isdate(cast(1 as sql_variant))", "sql_variant", 1, "isdate")]
    [DataRow("bit_count(1.5)", "numeric", 1, "bit_count")]
    [DataRow("bit_count('a')", "varchar", 1, "bit_count")]
    [DataRow("format(cast(1 as sql_variant), 'd')", "sql_variant", 1, "format")]
    public void StringArgument_OfARefusedType_RaisesMsg8116(string call, string type, int index, string function)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument {index} of {function} function.");

    [TestMethod]
    public void StringArgument_OverAnEmptyTablesXmlColumn_RaisesMsg8116()
        => new Simulation().AssertSqlError("create table e (x xml); select left(x, 1) from e", 8116, "Argument data type xml is invalid for argument 1 of left function.");

    [TestMethod]
    public void CharIndex_OverAnXmlHaystack_RaisesMsg257()
        => new Simulation().AssertSqlError("select charindex('a', cast('<a/>' as xml))", 257, "Implicit conversion from data type xml to varchar is not allowed. Use the CONVERT function to run this query.");

    [TestMethod]
    public void TrimCharacters_OfANonString_ReadAsVarchar()
        => AreEqual("a|", new Simulation().ExecuteScalar("select concat(ltrim('a', 1.5), '|', ltrim('a', 0x41))"));

    [TestMethod]
    [DataRow("concat(cast('<a/>' as xml), 'a')", "xml", "varchar")]
    [DataRow("concat(N'a', cast(1 as sql_variant))", "sql_variant", "nvarchar")]
    [DataRow("concat_ws(',', cast('<a/>' as xml), 'b')", "xml", "varchar")]
    [DataRow("quotename(cast(1 as sql_variant))", "sql_variant", "nvarchar")]
    public void ConcatenatedArgument_ThatCantBecomeAString_RaisesMsg257(string call, string type, string target)
        => new Simulation().AssertSqlError($"select {call}", 257, $"Implicit conversion from data type {type} to {target} is not allowed. Use the CONVERT function to run this query.");

    [TestMethod]
    public void Concat_OfAnImage_RaisesMsg206()
        => new Simulation().AssertSqlError("select concat(cast(0x41 as image), 'a')", 206, "Operand type clash: image is incompatible with varchar");

    // ---- CAST to and from the CLR types ----

    [TestMethod]
    [DataRow("cast(1 as hierarchyid)", "int", "simulated.sys.hierarchyid")]
    [DataRow("cast(1.5 as geography)", "numeric", "simulated.sys.geography")]
    [DataRow("convert(geometry, cast('<a/>' as xml))", "xml", "simulated.sys.geometry")]
    [DataRow("cast(hierarchyid::GetRoot() as sql_variant)", "simulated.sys.hierarchyid", "sql_variant")]
    [DataRow("cast(geography::Point(1, 2, 4326) as float)", "simulated.sys.geography", "float")]
    public void ClrTypeConversion_WithANonStringNonBinary_RaisesMsg529(string call, string source, string target)
        => new Simulation().AssertSqlError($"select {call}", 529, $"Explicit conversion from data type {source} to {target} is not allowed.");

    [TestMethod]
    public void ClrTypeConversion_WithAStringOrBinary_StillAnswers()
        => AreEqual("/1/|POINT (2 1)", new Simulation().ExecuteScalar("select concat(cast(cast('/1/' as hierarchyid) as varchar(10)), '|', cast(geography::Point(1, 2, 4326) as nvarchar(30)))"));

    // ---- the metadata functions ----

    [TestMethod]
    [DataRow("object_name(cast('<a/>' as xml))", "xml", "int")]
    [DataRow("db_name(newid())", "uniqueidentifier", "int")]
    [DataRow("col_name(1, cast('2020-01-01' as date))", "date", "int")]
    [DataRow("filegroup_name(cast('<a/>' as xml))", "xml", "smallint")]
    [DataRow("parsename('a.b', cast('<a/>' as xml))", "xml", "int")]
    public void MetadataIdArgument_WithNoConversion_RaisesMsg206(string call, string type, string target)
        => new Simulation().AssertSqlError($"select {call}", 206, $"Operand type clash: {type} is incompatible with {target}");

    [TestMethod]
    [DataRow("object_id(cast('<a/>' as xml))", "xml", "nvarchar")]
    [DataRow("schema_name(cast(1 as sql_variant))", "sql_variant", "int")]
    [DataRow("objectproperty(1, cast(1 as sql_variant))", "sql_variant", "varchar")]
    [DataRow("is_member(cast('<a/>' as xml))", "xml", "nvarchar")]
    [DataRow("serverproperty(cast(1 as sql_variant))", "sql_variant", "varchar")]
    [DataRow("suser_sname('x')", "varchar", "varbinary")]
    public void MetadataArgument_ConvertibleOnlyExplicitly_RaisesMsg257(string call, string type, string target)
        => new Simulation().AssertSqlError($"select {call}", 257, $"Implicit conversion from data type {type} to {target} is not allowed. Use the CONVERT function to run this query.");

    [TestMethod]
    public void ParseName_OfAnXmlName_RaisesMsg8116()
        => new Simulation().AssertSqlError("select parsename(cast('<a/>' as xml), 1)", 8116, "Argument data type xml is invalid for argument 1 of parsename function.");

    [TestMethod]
    public void SUserName_ResolvesItsArgument()
        => AreEqual("sa|public|sa|public", new Simulation().ExecuteScalar("select concat_ws('|', suser_name(1), suser_name(2.5), suser_name(0), suser_sname(0x01), suser_sname(0x02))"));

    [TestMethod]
    public void TypeName_OfTheTableIds_AnswersAsReal()
        => AreEqual("table|table type|void type", new Simulation().ExecuteScalar("select concat_ws('|', type_name(1), type_name(243), type_name(0))"));

    // ---- position / length / count slots ----

    [TestMethod]
    [DataRow("substring('abcdef', cast(2 as money), 2)", "money", 2, "substring")]
    [DataRow("substring('abcdef', 2, '2')", "varchar", 3, "substring")]
    [DataRow("stuff('abcdef', 1e0, 1, 'X')", "float", 2, "stuff")]
    [DataRow("stuff('abcdef', 2, cast(1 as bit), 'X')", "bit", 3, "stuff")]
    [DataRow("charindex('b', 'abcb', getdate())", "datetime", 3, "charindex")]
    [DataRow("str(12.345, 2.5)", "numeric", 2, "str")]
    [DataRow("str(12.345, 8, cast(2 as money))", "money", 3, "str")]
    [DataRow("round(12.345, cast(1 as bit))", "bit", 2, "round")]
    [DataRow("round(12.345, 1, cast(null as varchar(5)))", "varchar", 3, "round")]
    [DataRow("dateadd(day, 0x02, '2020-01-01')", "varbinary", 2, "dateadd")]
    [DataRow("get_bit(5, 2.5)", "numeric", 2, "get_bit")]
    public void NumericSlot_OfARefusedType_RaisesMsg8116(string call, string type, int index, string function)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument {index} of {function} function.");

    [TestMethod]
    public void NumericSlot_AcceptedNumbers_StillAnswer()
        => AreEqual("bc|aXcdef|4|12.350|12.300|2020-01-03", new Simulation().ExecuteScalar("""
            select concat_ws('|', substring('abcdef', 2.5, 2), stuff('abcdef', cast(2 as bigint), 1, 'X'), charindex('b', 'abcb', 3.0),
                round(12.345, 2.7), round(12.345, 1, 2e0), convert(varchar(10), dateadd(day, 2.5, cast('2020-01-01' as date)), 23))
            """));

    [TestMethod]
    [DataRow("datefromparts(getdate(), 1, 1)", "datetime")]
    [DataRow("choose(getdate(), 'a', 'b')", "datetime")]
    public void IntPart_FromADatetime_RaisesMsg257(string call, string type)
        => new Simulation().AssertSqlError($"select {call}", 257, $"Implicit conversion from data type {type} to int is not allowed. Use the CONVERT function to run this query.");

    // ---- which error wins ----

    [TestMethod]
    public void DateAdd_BindsItsDateBeforeCheckingItsNumber()
        => _ = new Simulation().AssertSqlError("select dateadd(day, cast(1e0 as bit), cast(1.5 as uniqueidentifier))", 529);

    [TestMethod]
    public void DateAdd_EvaluatesItsNumberFirst()
        => new Simulation().AssertSqlError("select dateadd(day, round(N'x', 1), 'abc')", 8114, "Error converting data type nvarchar to float.");

    [TestMethod]
    public void Left_BindsItsSourceBeforeItsCount()
        => new Simulation().AssertSqlError("select left(compress(getdate()), null & null)", 8116, "Argument data type datetime is invalid for argument 1 of Compress function.");

    [TestMethod]
    [DataRow("hashbytes('MD5', cast('10:00' as time))", "time")]
    [DataRow("hashbytes('MD5', cast(1.5 as decimal(10,4)))", "decimal")]
    [DataRow("hashbytes('MD5', 1.5)", "numeric")]
    public void HashBytes_NamesTheInputTypeWithoutItsLength(string call, string type)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument 2 of hashbytes function.");

    [TestMethod]
    [DataRow("choose(2, null, 'x')", "x")]
    [DataRow("least(null, 'b')", "b")]
    [DataRow("greatest('a', null, 'c')", "c")]
    public void BareNullArm_TakesNoPartInTheType(string call, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {call}"));

    [TestMethod]
    public void DateAdd_OfABareNullNumber_RaisesMsg8116()
        => new Simulation().AssertSqlError("select dateadd(day, null, getdate())", 8116, "Argument data type NULL is invalid for argument 2 of dateadd function.");

    [TestMethod]
    [DataRow("dy", "dayofyear")]
    [DataRow("yy", "year")]
    [DataRow("w", "weekday")]
    [DataRow("isowk", "iso_week")]
    public void DatePart_RefusalNamesTheCanonicalPart(string abbreviation, string name)
        => new Simulation().AssertSqlError($"select datepart({abbreviation}, cast('10:00' as time))", 9810, $"The datepart {name} is not supported by date function datepart for data type time.");

    [TestMethod]
    public void DatePart_AcceptsWForWeekday()
        => AreEqual(4, new Simulation().ExecuteScalar("set datefirst 7; select datepart(w, cast('2020-01-01' as date))"));

    [TestMethod]
    public void Unicode_ReadsABinaryAsUtf16()
        => AreEqual(35615, new Simulation().ExecuteScalar("select unicode(0x1F8B)"));

    [TestMethod]
    [DataRow("0x01")]
    [DataRow("0xFF0A0B0C")]
    public void BinaryToDate_ThatTheLayoutCannotRead_RaisesMsg241(string bytes)
        => _ = new Simulation().AssertSqlError($"select cast({bytes} as date)", 241);

    /// <summary>
    /// TRANSLATE and REPLACE project the container width, Unicode when any
    /// argument is and MAX only from the input (probed 2026-09-25 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("translate(c, 'a', 'b')", "varchar", 8000)]
    [DataRow("translate(c, N'a', 'b')", "nvarchar", 8000)]
    [DataRow("translate(ch, 'a', 'b')", "varchar", 8000)]
    [DataRow("translate(m, N'a', 'b')", "nvarchar", -1)]
    [DataRow("translate(c, cast('a' as varchar(max)), 'b')", "varchar", 8000)]
    [DataRow("replace(c, 'a', 'b')", "varchar", 8000)]
    [DataRow("replace(c, 'a', N'b')", "nvarchar", 8000)]
    [DataRow("replace(m, 'a', 'b')", "varchar", -1)]
    public void GrowableStringResult_TakesTheContainerWidth(string expression, string type, int maxLength)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (c varchar(10), ch char(3), m varchar(max)); select {expression} as r into t2 from t");
        AreEqual($"{type}|{maxLength}", sim.ExecuteScalar("select concat(type_name(system_type_id), '|', max_length) from sys.columns where object_id = object_id('t2')"));
    }

    [TestMethod]
    [DataRow("translate('a', 0x61, 'b')", "varbinary", 2)]
    [DataRow("translate('a', 'a', 1)", "int", 3)]
    public void TranslateCharacterLists_MustBeStrings(string expression, string type, int argument)
        => new Simulation().AssertSqlError($"select {expression}", 8116, $"Argument data type {type} is invalid for argument {argument} of translate function.");

    [TestMethod]
    [DataRow("unistr(1)", "int", 1, "unistr")]
    [DataRow("unistr(N'a', cast('x' as text))", "text", 2, "unistr")]
    [DataRow("session_context('k')", "varchar", 1, "session_context")]
    [DataRow("session_context(cast(N'k' as nchar(1)))", "nchar", 1, "session_context")]
    [DataRow("sid_binary(null)", "NULL", 1, "sid_binary")]
    [DataRow("sid_binary(1)", "int", 1, "sid_binary")]
    [DataRow("hashbytes(1, 1)", "int", 1, "hashbytes")]
    [DataRow("hashbytes(null, 'a')", "NULL", 1, "hashbytes")]
    [DataRow("xml_schema_namespace(1, N'c')", "int", 1, "XML_SCHEMA_NAMESPACE")]
    [DataRow("xml_schema_namespace(N'dbo', N'c', 1)", "int", 3, "XML_SCHEMA_NAMESPACE")]
    public void StringOnlyArgument_RaisesMsg8116(string call, string type, int argument, string function)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument {argument} of {function} function.");

    [TestMethod]
    public void SessionContext_TakesATypedNullKey()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select session_context(cast(null as nvarchar(5)))"));

    [TestMethod]
    [DataRow("sid_binary('x')")]
    [DataRow("sid_binary(0x41)")]
    public void SidBinary_TakesAStringOrABinary(string call)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select {call}"));

    [TestMethod]
    [DataRow("cursor_status(1, 'c')", 16902)]
    [DataRow("cursor_status('global', cast('<a/>' as xml))", 257)]
    [DataRow("switchoffset(1, '+00:00')", 206)]
    [DataRow("switchoffset(cast('x' as text), '+00:00')", 206)]
    [DataRow("todatetimeoffset(0x01, '+00:00')", 257)]
    public void AssignmentConvertedArgument_RaisesRealsError(string call, int number)
        => new Simulation().AssertSqlError($"select {call}", number);

    [TestMethod]
    public void CursorStatus_ReadsANumberAsItsDigits()
        => AreEqual((short)-3, new Simulation().ExecuteScalar("select cursor_status('global', 1)"));

    [TestMethod]
    [DataRow("object_definition(cast('2020-01-02' as datetime))", 257)]
    [DataRow("object_definition(1, 'x')", 245)]
    [DataRow("objectpropertyex(newid(), 'IsTable')", 206)]
    [DataRow("permissions(cast('2020-01-02' as datetime))", 257)]
    [DataRow("stats_date(1, newid())", 206)]
    [DataRow("index_col('nosuch', 'x', 1)", 245)]
    [DataRow("textvalid('t.c', 'x')", 257)]
    [DataRow("datetime2fromparts(2020, 1, 1, 0, 0, 0, 0, cast(7 as bit))", 10760)]
    [DataRow("timefromparts(cast('2020-01-02' as datetime), 0, 0, 0, 0)", 257)]
    [DataRow("switchoffset(sysdatetimeoffset(), cast('2020-01-02' as datetime))", 257)]
    [DataRow("todatetimeoffset('x', 'x')", 241)]
    [DataRow("trigger_nestlevel('x', 'x')", 245)]
    public void IdAndPartArguments_ConvertAsAssignments(string call, int number)
        => new Simulation().AssertSqlError($"select {call}", number);

    [TestMethod]
    [DataRow("object_definition(object_id('nosuch'), 1)")]
    [DataRow("trigger_nestlevel(1, null)")]
    public void TolerantForms_AnswerNull(string call)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select {call}"));
}
