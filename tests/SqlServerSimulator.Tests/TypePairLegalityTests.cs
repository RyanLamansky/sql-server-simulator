using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Whether two operand types may meet — in a unification (CASE / COALESCE /
/// a set operation), a comparison, or an arithmetic operator — is settled from
/// the types alone while real compiles, so typed NULLs and empty rowsets raise
/// exactly as values would. The refusals pin real's number, state and message,
/// operand order included; the legal pairs pin the result type and the value.
/// Probe-confirmed against SQL Server 2025 (2026-09-23).
/// </summary>
[TestClass]
public sealed class TypePairLegalityTests
{
    private static void Refuses(string commandText, int number, byte state, string message)
    {
        var ex = new Simulation().AssertSqlError(commandText, number);
        AreEqual(state, ex.State);
        AreEqual(message, ex.Message);
    }

    // --- Unification: the lower-precedence operand converts to the higher ---

    [TestMethod]
    [DataRow("select case when 1 = 1 then cast(null as int) else cast(null as date) end", "int is incompatible with date")]
    [DataRow("select coalesce(cast(null as text), cast(null as int))", "text is incompatible with int")]
    [DataRow("select coalesce(cast(null as sql_variant), cast(null as xml))", "sql_variant is incompatible with xml")]
    [DataRow("select coalesce(cast(null as xml), cast(null as hierarchyid))", "hierarchyid is incompatible with xml")]
    [DataRow("select coalesce(cast(null as geography), cast(null as geometry))", "geometry is incompatible with geography")]
    [DataRow("select coalesce(cast(null as nvarchar(max)), cast(null as sql_variant))", "nvarchar(max) is incompatible with sql_variant")]
    public void Unify_NoConversion_Msg206(string commandText, string clash) =>
        Refuses(commandText, 206, 2, $"Operand type clash: {clash}");

    /// <summary>A conversion real only performs explicitly is Msg 257, never 260, even between columns.</summary>
    [TestMethod]
    [DataRow("select cast(null as varbinary(4)) union all select cast(null as date)", "varbinary", "date")]
    [DataRow("select coalesce(cast(null as timestamp), cast(null as varchar(10)))", "varchar", "timestamp")]
    public void Unify_ExplicitOnlyConversion_Msg257(string commandText, string source, string target) =>
        Refuses(commandText, 257, 3, $"Implicit conversion from data type {source} to {target} is not allowed. Use the CONVERT function to run this query.");

    [TestMethod]
    public void Unify_TimeWithDateTime_IsDateTime() =>
        AreEqual(new DateTime(1900, 1, 1, 12, 34, 56, 123), new Simulation().ExecuteScalar("declare @t time = '12:34:56.1234567', @d datetime = null; select coalesce(@d, @t)"));

    [TestMethod]
    public void Unify_DecimalWithDateTime_IsDateTime() =>
        AreEqual("datetime", new Simulation().ExecuteScalar("select sql_variant_property(coalesce(cast(null as decimal(5,2)), cast('2024-01-01' as datetime)), 'BaseType')"));

    /// <summary>A binary meeting a string unifies as the string family's own shape of its byte length.</summary>
    [TestMethod]
    public void Unify_CharWithVarbinary_IsVarcharOfTheBinaryLength()
    {
        using var reader = new Simulation().ExecuteReader(
            "select sql_variant_property(coalesce(cast(null as char(3)), 0x61626364), 'BaseType'), sql_variant_property(coalesce(cast(null as char(3)), 0x61626364), 'MaxLength'), coalesce(cast(null as char(3)), 0x61626364)");
        IsTrue(reader.Read());
        AreEqual("varchar", reader.GetValue(0));
        AreEqual(4, reader.GetValue(1));
        AreEqual("abcd", reader.GetValue(2));
    }

    [TestMethod]
    public void Unify_NVarcharWithText_IsText() =>
        AreEqual("text", new Simulation().ExecuteScalar(
            "select coalesce(cast(null as nvarchar(10)), cast(null as text)) c into dbo.r; select type_name(system_type_id) from sys.columns where object_id = object_id('dbo.r')"));

    [TestMethod]
    public void Unify_BinaryWithDecimal_ReadsTheNumericByteForm() =>
        AreEqual(1.50m, new Simulation().ExecuteScalar("select coalesce(0x0502000196000000, cast(2 as decimal(5,2)))"));

    [TestMethod]
    public void Unify_BinaryWithUniqueIdentifier_IsUniqueIdentifier() =>
        AreEqual(new Guid("04030201-0605-0807-090a-0b0c0d0e0f10"), new Simulation().ExecuteScalar("select coalesce(0x0102030405060708090A0B0C0D0E0F10, newid())"));

    [TestMethod]
    public void Unify_BinaryWithDateTime_ReadsTheDayTickPair() =>
        AreEqual(new DateTime(2012, 8, 18), new Simulation().ExecuteScalar("select case when 1 = 1 then 0x0000A0B100000000 else getdate() end"));

    // --- Comparison ---

    /// <summary>The clash names the pair in a fixed order, whichever side each operand is written on.</summary>
    [TestMethod]
    [DataRow("select 1 where cast(null as int) = cast(null as date)", "date is incompatible with int")]
    [DataRow("select 1 where cast(null as date) = cast(null as int)", "date is incompatible with int")]
    [DataRow("select 1 where cast(null as uniqueidentifier) < cast(null as int)", "uniqueidentifier is incompatible with int")]
    [DataRow("select 1 where cast(null as int) <> cast(null as hierarchyid)", "int is incompatible with hierarchyid")]
    [DataRow("select 1 where cast(null as text) >= cast(null as datetime)", "text is incompatible with datetime")]
    public void Compare_NoConversion_Msg206(string commandText, string clash) =>
        Refuses(commandText, 206, 2, $"Operand type clash: {clash}");

    [TestMethod]
    [DataRow("select 1 where cast(null as time) = cast(null as datetime)", "time and datetime", "equal to")]
    [DataRow("select 1 where cast(null as varbinary(4)) <> cast(null as date)", "varbinary and date", "not equal to")]
    [DataRow("select 1 where cast(null as text) = cast(null as varchar(10))", "text and varchar", "equal to")]
    [DataRow("select 1 where cast(null as date) < cast(null as xml)", "date and xml", "less than")]
    public void Compare_Incompatible_Msg402(string commandText, string pair, string operatorName) =>
        Refuses(commandText, 402, 1, $"The data types {pair} are incompatible in the {operatorName} operator.");

    [TestMethod]
    public void Compare_XmlWithXml_Msg305() =>
        Refuses("select 1 where cast(null as xml) = cast(null as xml)", 305, 1,
            "The XML data type cannot be compared or sorted, except when using the IS NULL operator.");

    /// <summary>A spatial operand is refused by name on either side.</summary>
    [TestMethod]
    [DataRow("select 1 where cast(null as int) = cast(null as geography)", "equal to")]
    [DataRow("select 1 where cast(null as geography) > cast(null as int)", "greater than")]
    public void Compare_Spatial_Msg403(string commandText, string operatorName) =>
        Refuses(commandText, 403, 1, $"Invalid operator for data type. Operator equals {operatorName}, type equals geography.");

    [TestMethod]
    public void Compare_StringWithTimestamp_Msg257() =>
        Refuses("select 1 where cast(null as varchar(10)) = cast(null as timestamp)", 257, 3,
            "Implicit conversion from data type varchar to timestamp is not allowed. Use the CONVERT function to run this query.");

    /// <summary>The converted operand is a column, so real names it and its object.</summary>
    [TestMethod]
    public void Compare_StringColumnWithTimestamp_Msg260() =>
        Refuses("create table t(v varchar(10), r rowversion); select 1 from t where v = r", 260, 3,
            "Disallowed implicit conversion from data type varchar to data type timestamp, table 't', column 'v'. Use the CONVERT function to run this query.");

    /// <summary>Every comparison-shaped construct asks the same question while compiling.</summary>
    [TestMethod]
    [DataRow("create table t(i int, d date); select 1 from t where i = d")]
    [DataRow("if cast(null as int) = cast(null as date) print 1")]
    [DataRow("declare @i int = 0; while @i = cast(null as date) set @i = 1")]
    [DataRow("select iif(cast(null as int) = cast(null as date), 1, 0)")]
    [DataRow("select nullif(cast(null as int), cast(null as date))")]
    [DataRow("select 1 where cast(null as int) in (select cast(null as date))")]
    [DataRow("select 1 where cast(null as int) = any (select cast(null as date))")]
    [DataRow("select 1 where cast(null as int) between cast(null as date) and 1")]
    [DataRow("select case cast(null as int) when cast(null as date) then 1 end")]
    public void Compare_EveryContext_Msg206(string commandText) =>
        Refuses(commandText, 206, 2, "Operand type clash: date is incompatible with int");

    [TestMethod]
    public void Compare_RefusedCondition_RunsNeitherBranch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table log(n int)");
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(
            "if cast(null as int) = cast(null as date) insert log values (1) else insert log values (2)"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    public void Compare_BinaryWithString_ComparesAsTheString()
    {
        using var reader = new Simulation().ExecuteReader(
            "select case when 0x61 = 'a' then 1 else 0 end, case when 'a' = 0x61 then 1 else 0 end, case when 0x6100 = N'a' then 1 else 0 end, case when 0x62 > 'a' then 1 else 0 end");
        IsTrue(reader.Read());
        for (var i = 0; i < 4; i++)
            AreEqual(1, reader.GetInt32(i));
    }

    [TestMethod]
    [DataRow("declare @d datetime = '1900-01-02 12:00'; select case when @d = cast(1.5 as decimal(5,2)) then 1 else 0 end")]
    [DataRow("declare @d datetime = '1900-01-02 12:00'; select case when @d < cast(1.6 as float) then 1 else 0 end")]
    [DataRow("declare @d datetime = '1900-01-02 12:00'; select case when @d = cast(1.5 as money) then 1 else 0 end")]
    [DataRow("declare @u uniqueidentifier = '01020304-0506-0708-090A-0B0C0D0E0F10'; select case when @u = 0x0403020106050807090A0B0C0D0E0F10 then 1 else 0 end")]
    [DataRow("select case when cast(1.5 as money) = 0x0000000000003A98 then 1 else 0 end")]
    [DataRow("select case when cast(1.5 as decimal(5,2)) = 0x0502000196000000 then 1 else 0 end")]
    [DataRow("declare @h hierarchyid = '/1/'; select case when @h = '/1/' then 1 else 0 end")]
    [DataRow("declare @h hierarchyid = '/1/'; select case when '/1/2/' > @h then 1 else 0 end")]
    public void Compare_LegalCrossFamilyPair_Converts(string commandText) =>
        AreEqual(1, new Simulation().ExecuteScalar(commandText));

    /// <summary>Two xml operands pass IS DISTINCT FROM while compiling, and refuse only once both carry a value.</summary>
    [TestMethod]
    public void DistinctFrom_XmlPair_RefusesOnlyTwoValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table x(a xml, b xml); insert x values (null, '<a/>')");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from x where a is distinct from b"));

        _ = simulation.ExecuteNonQuery("update x set a = '<a/>'");
        var ex = simulation.AssertSqlError("select count(*) from x where a is distinct from b", 305);
        AreEqual(3, ex.State);
    }

    [TestMethod]
    [DataRow("select greatest(cast(null as xml), cast(null as xml))", "xml", 1, "greatest")]
    [DataRow("select least(1, cast(null as geography))", "geography", 2, "least")]
    public void GreatestLeast_IncomparableArgument_Msg8116(string commandText, string type, int argument, string function) =>
        Refuses(commandText, 8116, 4, $"Argument data type {type} is invalid for argument {argument} of {function} function.");

    // --- Arithmetic ---

    [TestMethod]
    [DataRow("select cast(null as int) + cast(null as date)", "date is incompatible with int")]
    [DataRow("select cast(null as numeric(5,2)) + cast(null as date)", "date is incompatible with numeric")]
    [DataRow("select cast(null as float) * 0x01", "varbinary is incompatible with float")]
    public void Arithmetic_NoConversion_Msg206(string commandText, string clash) =>
        Refuses(commandText, 206, 2, $"Operand type clash: {clash}");

    [TestMethod]
    [DataRow("select cast(null as bit) + cast(null as varchar(10))", "bit and varchar", "add")]
    [DataRow("select cast(null as float) % 2", "float and int", "modulo")]
    [DataRow("select cast(null as text) + 'x'", "text and varchar", "add")]
    [DataRow("select cast(null as datetime) - cast(null as date)", "datetime and date", "subtract")]
    [DataRow("select cast(null as varchar(10)) - 'x'", "varchar and varchar", "subtract")]
    public void Arithmetic_Incompatible_Msg402(string commandText, string pair, string operatorName) =>
        Refuses(commandText, 402, 1, $"The data types {pair} are incompatible in the {operatorName} operator.");

    [TestMethod]
    [DataRow("select cast(null as date) * cast(null as date)", "date", "multiply")]
    [DataRow("select cast(null as uniqueidentifier) - cast(null as uniqueidentifier)", "uniqueidentifier", "subtract")]
    [DataRow("select cast(null as varchar(10)) / cast(null as varchar(10))", "varchar", "divide")]
    public void Arithmetic_OperandInvalid_Msg8117(string commandText, string type, string operatorName) =>
        Refuses(commandText, 8117, 1, $"Operand data type {type} is invalid for {operatorName} operator.");

    [TestMethod]
    [DataRow("select cast(null as hierarchyid) + 1", "add", "hierarchyid")]
    [DataRow("select 1 % cast(null as geography)", "modulo", "geography")]
    public void Arithmetic_ClrType_Msg403(string commandText, string operatorName, string type) =>
        Refuses(commandText, 403, 1, $"Invalid operator for data type. Operator equals {operatorName}, type equals {type}.");

    [TestMethod]
    [DataRow("select cast(null as datetime) * 2", "datetime", "int")]
    [DataRow("declare @v sql_variant; select @v + 1", "sql_variant", "int")]
    public void Arithmetic_ExplicitOnlyConversion_Msg257(string commandText, string source, string target) =>
        Refuses(commandText, 257, 3, $"Implicit conversion from data type {source} to {target} is not allowed. Use the CONVERT function to run this query.");

    /// <summary>A column operand names its object as the FROM clause wrote it — a derived table by its alias.</summary>
    [TestMethod]
    [DataRow("select d * 2 from t", "t", "d")]
    [DataRow("select q.e * 2 from (select d as e from t) q", "q", "e")]
    public void Arithmetic_ExplicitOnlyConversionOfColumn_Msg260(string query, string table, string column) =>
        Refuses($"create table t(d datetime); {query}", 260, 3,
            $"Disallowed implicit conversion from data type datetime to data type int, table '{table}', column '{column}'. Use the CONVERT function to run this query.");

    /// <summary>Real compiles before it runs, so the refused pair wins over the division by zero.</summary>
    [TestMethod]
    public void Arithmetic_RefusalPrecedesRuntimeError() =>
        Refuses("select 1/0 + cast(null as date)", 206, 2, "Operand type clash: date is incompatible with int");

    [TestMethod]
    [DataRow("select cast('2024-01-01' as datetime) - 1.5", "2023-12-30 12:00:00")]
    [DataRow("select cast('2024-01-01' as datetime) + 1.5", "2024-01-02 12:00:00")]
    [DataRow("select 1.5 + cast('2024-01-01' as datetime)", "2024-01-02 12:00:00")]
    [DataRow("select 1.5 - cast('2024-01-01' as datetime)", "1776-01-02 12:00:00")]
    [DataRow("declare @d datetime = '2024-01-01'; select @d + cast(0.25 as float)", "2024-01-01 06:00:00")]
    [DataRow("declare @d datetime = '2024-01-01'; select @d - cast(0.3333 as money)", "2023-12-31 16:00:02.880")]
    [DataRow("declare @d datetime = '2024-01-01'; select @d + cast(0.1 as real)", "2024-01-01 02:24:00")]
    [DataRow("declare @d datetime = '2024-01-01'; select @d + cast(0.000005 as decimal(9,6))", "2024-01-01 00:00:00.433")]
    [DataRow("declare @d datetime = '2024-01-01'; select @d + 0x0000000100000000", "2024-01-02 00:00:00")]
    [DataRow("declare @d datetime = '2024-01-01'; select @d - '1900-01-02'", "2023-12-31 00:00:00")]
    [DataRow("declare @s smalldatetime = '2024-01-01'; select @s - 1.5", "2023-12-30 12:00:00")]
    [DataRow("declare @s smalldatetime = '2024-01-01'; select @s + cast(0.0003 as float)", "2024-01-01 00:00:00")]
    [DataRow("declare @s smalldatetime = '2024-01-01'; select @s + cast(0.0004 as float)", "2024-01-01 00:01:00")]
    public void Arithmetic_LegacyDateTimeWithConvertiblePartner_AddsDayCounts(string commandText, string expected) =>
        AreEqual(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    public void Arithmetic_LegacyDateTimeOverflow_Msg8115() =>
        Refuses("declare @d datetime = '2024-01-01'; select @d + cast(1e7 as float)", 8115, 2,
            "Arithmetic overflow error converting expression to data type datetime.");

    [TestMethod]
    [DataRow("select cast(1.5 as decimal(5,2)) + 0x0502000196000000", "3.00")]
    [DataRow("select cast(1.5 as decimal(5,2)) * 0x0502000196000000", "2.2500")]
    [DataRow("select cast(1.5 as money) + 0x0000000000003A98", "3.0000")]
    public void Arithmetic_BinaryTakesTheNumericPartnersType(string commandText, string expected) =>
        AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), new Simulation().ExecuteScalar(commandText));

    /// <summary>bit adopts a decimal partner's own type, where every other integer brings its digits.</summary>
    [TestMethod]
    [DataRow("select sql_variant_property(cast(1 as bit) * cast(1 as decimal(5,2)), 'Precision')", 11)]
    [DataRow("select sql_variant_property(cast(1 as bit) * cast(1 as decimal(5,2)), 'Scale')", 4)]
    [DataRow("select sql_variant_property(cast(1 as bit) % cast(1 as decimal(5,2)), 'Precision')", 5)]
    [DataRow("select sql_variant_property(cast(1 as tinyint) * cast(1 as decimal(5,2)), 'Precision')", 9)]
    public void Arithmetic_BitWithDecimal_ResultShape(string commandText, int expected) =>
        AreEqual((byte)expected, Convert.ToByte(new Simulation().ExecuteScalar(commandText), System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    public void Arithmetic_SysnamePlusString_CountsSysnameAs128() =>
        AreEqual((short)276, Convert.ToInt16(new Simulation().ExecuteScalar(
            "declare @n sysname = N'abc'; select sql_variant_property(@n + cast('x' as varchar(10)), 'MaxLength')"), System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    public void Arithmetic_Timestamp_ConcatenatesWithBinaryAndTakesAnIntegerPartnersType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t(r rowversion, i int); insert t(i) values (1)");
        using var reader = simulation.ExecuteReader("select datalength(r + 0x01), datalength(r + r), sql_variant_property(r + 1, 'BaseType') from t");
        IsTrue(reader.Read());
        AreEqual(9, reader.GetInt32(0));
        AreEqual(16, reader.GetInt32(1));
        AreEqual("int", reader.GetValue(2));
    }

    // --- The conversions the newly legal pairs reach ---

    [TestMethod]
    [DataRow("select cast(0x0102 as datetime)", "1900-01-01 00:00:00.860")]
    [DataRow("select cast(0x0000A0B100000064 as smalldatetime)", "1900-01-01 01:40:00")]
    [DataRow("select cast('  2024-01-02  ' as datetime)", "2024-01-02 00:00:00")]
    public void Conversion_ToLegacyDateTime(string commandText, string expected) =>
        AreEqual(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    [DataRow("select cast(cast('2012-05-05 01:02:03.457' as datetime) as binary(8))", "0000A04800110B6D")]
    [DataRow("select cast(cast('2012-05-05 01:02' as smalldatetime) as varbinary(10))", "A048003E")]
    public void Conversion_LegacyDateTimeToBinary(string commandText, string expectedHex) =>
        AreEqual(expectedHex, Convert.ToHexString((byte[])new Simulation().ExecuteScalar(commandText)!));

    [TestMethod]
    public void Conversion_BinaryMinutesPastMidnight_Msg210() =>
        Refuses("select cast(0x6100 as smalldatetime)", 210, 1, "Conversion failed when converting datetime from binary/varbinary string.");

    [TestMethod]
    public void Conversion_BinaryTooShortForNumeric_Msg8114() =>
        Refuses("select cast(0x0502 as decimal(5,2))", 8114, 5, "Error converting data type varbinary to numeric.");

    [TestMethod]
    public void Conversion_PaddedStringToTime() =>
        AreEqual(new TimeSpan(12, 34, 56), new Simulation().ExecuteScalar("select cast('12:34:56   ' as time)"));
}
