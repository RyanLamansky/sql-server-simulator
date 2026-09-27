using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's native <c>json</c> type: the canonical text real hands
/// back, the length of its binary form, the refusals of text that isn't a
/// document, conversions, the refusals of a type with no ordering, the JSON
/// functions over it and the catalog surfaces. Every expectation was probed
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class JsonTypeTests
{
    private static void AssertError(string commandText, int number, byte state, string message)
    {
        var ex = new Simulation().AssertSqlError(commandText, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual(state, ex.Errors[0].State);
    }

    [TestMethod]
    [DataRow(@"{""a"" : 1,  ""b"":[1, 2 ,3]}", @"{""a"":1,""b"":[1,2,3]}")]
    [DataRow("  {  \"a\"  :  1 , \"b\" : [ 1 , 2 ] }\t\r\n", @"{""a"":1,""b"":[1,2]}")]
    [DataRow(@"{""z"":1,""a"":2,""m"":3}", @"{""z"":1,""a"":2,""m"":3}")]
    [DataRow(@"{""a"":1,""a"":2}", @"{""a"":1}")]
    [DataRow(@"{""a"":{""b"":1,""b"":2},""c"":[{""d"":1,""d"":{""e"":3}}],""a"":5}", @"{""a"":{""b"":1},""c"":[{""d"":1}]}")]
    [DataRow(@"{""\/"":1,""/"":2}", @"{""\/"":1,""/"":2}")]
    [DataRow(@"{""\u0041"":1,""\u0001"":2,""\n"":3}", @"{""\u0041"":1,""\u0001"":2,""\n"":3}")]
    [DataRow(@"[""\u0041\u00e9\u0022\u005c\u002f\/\u000a\u0008\u001f\ud83d\ude00""]", @"[""Aé\""\\//\n\b\u001F😀""]")]
    [DataRow(@"[""\f\r\t\"""", ""<>&""]", @"[""\f\r\t\"""",""<>&""]")]
    [DataRow("[true,false,null,[],{},\"\"]", "[true,false,null,[],{},\"\"]")]
    [DataRow("[1.0, 1e2, 1E+2, -0, 0.10, 12345678901234567890, 1.5e-10, 3.14159265358979323846]", "[1.0,100.0000000000,100.0000000000,0,0.10,12345678901234567890,0.0000000001,3.14159265358979323846]")]
    [DataRow("[1e0, 1e1, 1e-1, 1.5e3, 2E-3, 1e20, 1e-20, 123.456e2, 1.25e-1]", "[1.0000000000,10.0000000000,0.1000000000,1500.0000000000,0.0020000000,100000000000000000000.0000000000,0.0000000000,12345.6000000000,0.1250000000]")]
    [DataRow("[2.5e-10, 1.6e-10, -1.5e-10, 5e-11, 9.99999999995e-1, -1e-20, -0.0, 1e-400]", "[0.0000000003,0.0000000002,-0.0000000001,0.0000000001,1.0000000000,0.0000000000,0.0,0.0000000000]")]
    [DataRow("[1e27, 1e28]", "[1000000000000000013287555072.0000000000,9999999999999999583119736832.0000000000]")]
    [DataRow("[0.00000000000000000000000000000000000001, 0.000000000000000000000000000000000000001]", "[0.00000000000000000000000000000000000001,0.0000000000]")]
    [DataRow("[123456789012345678901234567890.12345678, 1.23456789012345e5]", "[123456789012345678901234567890.12345678,123456.7890123450]")]
    public void CanonicalForm(string input, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"declare @j json = N'{input}'; select cast(@j as nvarchar(max))"));

    [TestMethod]
    [DataRow("[]", 18)]
    [DataRow("{}", 18)]
    [DataRow("[1]", 26)]
    [DataRow("[1,2,3]", 34)]
    [DataRow("[536870911, -536870912, true, null, [], {}, \"\"]", 50)]
    [DataRow("[536870912]", 35)]
    [DataRow("[-9223372036854775808]", 35)]
    [DataRow("[9223372036854775808]", 39)]
    [DataRow("[99999999999999999999]", 43)]
    [DataRow("[1.5]", 35)]
    [DataRow("[1.0000000000]", 39)]
    [DataRow("[1e0]", 39)]
    [DataRow("[0.123456789012345678901234567890123456]", 47)]
    [DataRow("[\"a\"]", 29)]
    [DataRow("[\"é\"]", 30)]
    [DataRow("[\"😀\"]", 32)]
    [DataRow(@"[""\u0001""]", 29)]
    [DataRow("[[1]]", 34)]
    [DataRow("{\"a\":1}", 43)]
    [DataRow("{\"\":1}", 40)]
    [DataRow(@"{""\n"":1}", 44)]
    [DataRow("{\"a\":1,\"b\":2}", 60)]
    [DataRow("{\"a\":1,\"a\":2}", 43)]
    [DataRow("[{\"a\":1},{\"a\":2}]", 65)]
    [DataRow("[{\"a\":1},{\"b\":2}]", 76)]
    [DataRow("{\"a\":{\"b\":1},\"c\":[1,2]}", 93)]
    [DataRow("[{\"a\":[{\"b\":[{\"c\":1}]}]}]", 109)]
    public void DataLength_IsTheBinaryFormLength(string document, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"select datalength(cast(N'{document}' as json))"));

    [TestMethod]
    [DataRow(126, 154)]
    [DataRow(127, 155)]
    [DataRow(128, 157)]
    [DataRow(16383, 16412)]
    [DataRow(16384, 16414)]
    public void DataLength_StringLengthIsAVarint(int length, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"select datalength(cast(N'[\"' + replicate(cast('x' as nvarchar(max)), {length}) + '\"]' as json))"));

    [TestMethod]
    public void DataLength_LargeArrayAndDeepNesting()
    {
        var sim = new Simulation();
        AreEqual(80022, sim.ExecuteScalar("select datalength(cast(N'[' + (select string_agg(cast('1' as nvarchar(max)), ',') from generate_series(1, 20000)) + ']' as json))"));
        AreEqual(1034, sim.ExecuteScalar("select datalength(cast(replicate(cast('[' as varchar(max)), 128) + replicate(cast(']' as varchar(max)), 128) as json))"));
    }

    [TestMethod]
    [DataRow("abc", 13609, 9, "JSON text is not properly formatted. Unexpected character 'a' is found at position 0.")]
    [DataRow("", 13609, 9, "JSON text is not properly formatted. Unexpected character '.' is found at position 0.")]
    [DataRow("1", 13609, 9, "JSON text is not properly formatted. Unexpected character '1' is found at position 0.")]
    [DataRow("null", 13609, 9, "JSON text is not properly formatted. Unexpected character 'n' is found at position 0.")]
    [DataRow("\"abc\"", 13609, 9, "JSON text is not properly formatted. Unexpected character '\"' is found at position 0.")]
    [DataRow("{\"a\":1", 13609, 9, "JSON text is not properly formatted. Unexpected character '.' is found at position 6.")]
    [DataRow("{\"a\":1}x", 13609, 9, "JSON text is not properly formatted. Unexpected character 'x' is found at position 7.")]
    [DataRow(@"[""\x""]", 13609, 9, "JSON text is not properly formatted. Unexpected character '\"' is found at position 1.")]
    [DataRow(@"[""a\u00""]", 13609, 9, "JSON text is not properly formatted. Unexpected character '\"' is found at position 1.")]
    [DataRow("[00]", 13609, 9, "JSON text is not properly formatted. Unexpected character '0' is found at position 1.")]
    [DataRow("[1.]", 13609, 9, "JSON text is not properly formatted. Unexpected character '1' is found at position 1.")]
    [DataRow("[+1]", 13609, 9, "JSON text is not properly formatted. Unexpected character '+' is found at position 1.")]
    [DataRow("[TRUE]", 13609, 9, "JSON text is not properly formatted. Unexpected character 'T' is found at position 1.")]
    [DataRow("{\"a\":1,}", 13609, 9, "JSON text is not properly formatted. Unexpected character '}' is found at position 7.")]
    [DataRow("[1,]", 13609, 9, "JSON text is not properly formatted. Unexpected character ']' is found at position 3.")]
    [DataRow("{a:1}", 13609, 9, "JSON text is not properly formatted. Unexpected character 'a' is found at position 1.")]
    [DataRow("[1 2]", 13609, 9, "JSON text is not properly formatted. Unexpected character '2' is found at position 3.")]
    [DataRow("{\"a\" 1}", 13609, 9, "JSON text is not properly formatted. Unexpected character '1' is found at position 5.")]
    [DataRow("[\"é€😀\", x]", 13609, 9, "JSON text is not properly formatted. Unexpected character 'x' is found at position 14.")]
    [DataRow("[é]", 13609, 9, "JSON text is not properly formatted. Unexpected character 'Ã' is found at position 1.")]
    [DataRow("[\"é", 13609, 9, "JSON text is not properly formatted. Unexpected character '\"' is found at position 1.")]
    [DataRow("[1e308]", 1007, 5, "The number '1e308' is out of the range for numeric representation (maximum precision 38).")]
    [DataRow("[1e29]", 1007, 5, "The number '1e29' is out of the range for numeric representation (maximum precision 38).")]
    [DataRow("[12345678901234567890123456789012345678901234567890]", 1007, 3, "The number '12345678901234567890123456789012345678901234567890' is out of the range for numeric representation (maximum precision 38).")]
    public void Text_Refusals(string input, int number, int state, string message) =>
        AssertError($"select cast(N'{input}' as json)", number, (byte)state, message);

    [TestMethod]
    public void Text_ControlCharacterAndFormFeedAreRefused()
    {
        AssertError("select cast('[\"a' + char(9) + 'b\"]' as json)", 13609, 9, "JSON text is not properly formatted. Unexpected character '\"' is found at position 1.");
        AssertError("select cast('[1,' + char(12) + '2]' as json)", 13609, 9, "JSON text is not properly formatted. Unexpected character '\f' is found at position 3.");
    }

    [TestMethod]
    public void Text_Limits()
    {
        AssertError("select cast(replicate(cast('[' as varchar(max)), 129) + replicate(cast(']' as varchar(max)), 129) as json)", 13645, 1, "Nested level of JSON document exceeds limit 128.");
        AssertError("select cast(N'[' + (select string_agg(cast('1' as nvarchar(max)), ',') from generate_series(1, 65536)) + ']' as json)", 13647, 1, "Number of items in one object/array exceeds limit 65535 in JSON type.");
        AssertError("select cast(N'{' + (select string_agg(cast(concat('\"k', value, '\":1') as nvarchar(max)), ',') from generate_series(1, 32769)) + '}' as json)", 13649, 1, "Number of unique keys exceeds limit 32768 in JSON type.");
    }

    [TestMethod]
    [DataRow("declare @j json(100)", 2716, 1, "Column, parameter, or variable #1: Cannot specify a column width on data type json.")]
    [DataRow("declare @j json(max)", 2716, 1, "Column, parameter, or variable #1: Cannot specify a column width on data type json.")]
    [DataRow("create table t (j json(50))", 2716, 1, "Column, parameter, or variable #1: Cannot specify a column width on data type json.")]
    [DataRow("select cast('[1]' as json(10))", 291, 1, "CAST or CONVERT: invalid attributes specified for type 'json'")]
    [DataRow("create type jt from json", 13657, 1, "Cannot create alias types from a JSON data type.")]
    [DataRow("create table t (j json collate Latin1_General_CI_AS)", 447, 1, "Expression type json is invalid for COLLATE clause.")]
    public void Declaration_Refusals(string commandText, int number, int state, string message) =>
        AssertError(commandText, number, (byte)state, message);

    [TestMethod]
    public void Storage_EverySiteCanonicalizes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, j json, d json default '{ }');
            insert t (id, j) values (1, '{ "a" : 1 }'), (2, null);
            insert t (id, j) select 3, N'[ 1 , 2 ]';
            update t set j = '{ "b" : [ 1 ] }' where id = 2;
            """);
        AreEqual("{\"a\":1}|{\"b\":[1]}|[1,2]|{}", sim.ExecuteScalar("select string_agg(cast(j as nvarchar(max)), '|') within group (order by id) + '|' + max(cast(d as nvarchar(max))) from t"));
        AreEqual("[1,2]", sim.ExecuteScalar("declare @t table (j json); insert @t values ('[ 1, 2 ]'); select cast(j as nvarchar(max)) from @t"));
        AreEqual("[1,2]", sim.ExecuteScalar("create table #t (j json); insert #t values ('[ 1, 2 ]'); select cast(j as nvarchar(max)) from #t"));
        AreEqual("json", sim.ExecuteScalar("select * into t2 from t; select type_name(system_type_id) from sys.columns where object_id = object_id('t2') and name = 'j'"));
        _ = sim.AssertSqlError("update t set j = 'x'", 13609);
    }

    [TestMethod]
    public void Storage_OutputParametersAndModules()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (j json)",
            "create procedure p @j json as select cast(@j as nvarchar(max))",
            "create function f() returns json as begin return '{ \"x\" : 1 }' end");
        using var reader = sim.ExecuteReader("insert t output inserted.j values ('[ 1 , 2 ]')");
        IsTrue(reader.Read());
        AreEqual("[1,2]", reader.GetValue(0));
        AreEqual("json", reader.GetDataTypeName(0));
        AreEqual(typeof(string), reader.GetFieldType(0));
        AreEqual("[1]", sim.ExecuteScalar("exec p N' [ 1 ] '"));
        AreEqual("{\"x\":1}", sim.ExecuteScalar("select cast(dbo.f() as nvarchar(max))"));
        AreEqual("{\"a\":1}|43", sim.ExecuteScalar("exec sp_executesql N'select cast(@p as nvarchar(max)) + ''|'' + cast(datalength(@p) as varchar(10))', N'@p json', N'{\"a\" : 1}'"));
    }

    [TestMethod]
    public void Conversions_ToStrings()
    {
        var sim = new Simulation();
        AreEqual("<[1]       >|<[1]       >|[1]|[1]", sim.ExecuteScalar("""
            declare @j json = '[1]';
            select '<' + cast(@j as char(10)) + '>|<' + cast(@j as nchar(10)) + '>|' + cast(@j as varchar(3)) + '|' + cast(@j as sysname)
            """));
        AreEqual("{\"a\":1}", sim.ExecuteScalar("select convert(nvarchar(max), cast('{\"a\":1}' as json), 1)"));
        AssertError("declare @j json = '[1,2,3]'; select cast(@j as nvarchar(5))", 13639, 1, "Target string size is too small to represent the JSON instance.");
        AreEqual(1, sim.ExecuteScalar("select iif(try_cast(cast('[1,2,3]' as json) as varchar(3)) is null and try_cast('abc' as json) is null, 1, 0)"));
        _ = sim.AssertSqlError("select try_cast('[1e400]' as json)", 1007);
    }

    [TestMethod]
    [DataRow("select cast(cast('[1]' as json) as varbinary(max))", 529, "Explicit conversion from data type json to varbinary(max) is not allowed.")]
    [DataRow("select cast(cast('[1]' as json) as int)", 529, "Explicit conversion from data type json to int is not allowed.")]
    [DataRow("select cast(cast('[1]' as json) as xml)", 529, "Explicit conversion from data type json to xml is not allowed.")]
    [DataRow("select cast(cast('[1]' as json) as sql_variant)", 529, "Explicit conversion from data type json to sql_variant is not allowed.")]
    [DataRow("select cast(0x7B7D as json)", 529, "Explicit conversion from data type varbinary to json is not allowed.")]
    [DataRow("select cast(1 as json)", 529, "Explicit conversion from data type int to json is not allowed.")]
    [DataRow("select cast(cast('<a/>' as xml) as json)", 529, "Explicit conversion from data type xml to json is not allowed.")]
    [DataRow("create table t (b ntext); select cast(b as json) from t", 529, "Explicit conversion from data type ntext to json is not allowed.")]
    [DataRow("declare @j json = '[1]'; declare @s nvarchar(100) = @j", 257, "Implicit conversion from data type json to nvarchar is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("declare @j json = '[1]'; declare @i int = @j", 206, "Operand type clash: json is incompatible with int")]
    [DataRow("declare @j json = 5", 206, "Operand type clash: int is incompatible with json")]
    [DataRow("declare @j json = '[1]'; select concat(@j, N'x')", 257, "Implicit conversion from data type json to nvarchar is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("declare @j json = '[1]'; select concat(@j, 'x')", 257, "Implicit conversion from data type json to varchar is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("declare @j json = '[1]'; select sql_variant_property(@j, 'BaseType')", 206, "Operand type clash: json is incompatible with sql_variant")]
    public void Conversions_Refusals(string commandText, int number, string message) =>
        new Simulation().AssertSqlError(commandText, number, message);

    [TestMethod]
    public void Conversions_Vector()
    {
        var sim = new Simulation();
        AreEqual("[1.0000000e+000,2.0000000e+000]", sim.ExecuteScalar("select cast(cast(cast('[1, 2]' as json) as vector(2)) as varchar(max))"));
        AreEqual("[1.0000000000,2.5000000000,-3.0000000000]|73", sim.ExecuteScalar("declare @v vector(3) = '[1, 2.5, -3]'; declare @j json = @v; select cast(@j as nvarchar(max)) + '|' + cast(datalength(@j) as varchar(10))"));
        AssertError("declare @j json = '[1,2]'; declare @v vector(3) = @j", 42204, 2, "The vector dimensions 3 and 2 do not match.");
        AssertError("declare @j json = '{\"a\":1}'; declare @v vector(2) = @j", 13670, 20, "Input JSON is not a valid Vector : 'Key-Value Not Supported'.");
    }

    [TestMethod]
    public void Unification_StringArmsBecomeJson()
    {
        var sim = new Simulation();
        AreEqual("[1]|[1]|[2]", sim.ExecuteScalar("declare @j json = '[1]'; select cast(coalesce(@j, N'[2]') as nvarchar(max)) + '|' + cast(isnull(@j, '[3]') as nvarchar(max)) + '|' + cast(case when 1=0 then @j else N'[ 2 ]' end as nvarchar(max))"));
        AreEqual("json", sim.ExecuteScalar("select system_type_name from sys.dm_exec_describe_first_result_set(N'declare @j json = ''[1]''; select coalesce(@j, N''x'')', null, 0)"));
        _ = sim.AssertSqlError("declare @j json = '[1]'; select case when 1=0 then @j else N'bad' end", 13609);
        AreEqual(2, sim.ExecuteScalar("create table t (j json); insert t values ('[1]'); select count(*) from (select j from t union all select N'[2]') u"));
    }

    [TestMethod]
    [DataRow("declare @j json = '[1]'; select case when @j = @j then 1 end", 13636, 1)]
    [DataRow("declare @j json = '[1]'; select case when @j in (@j) then 1 end", 13636, 1)]
    [DataRow("create table t (j json); select 1 from t where j = null", 13636, 1)]
    [DataRow("create table t (j json); select * from t order by j", 13636, 2)]
    [DataRow("create table t (j json); select j from t group by j", 13636, 2)]
    [DataRow("create table t (j json, i int); select row_number() over (partition by j order by i) from t", 13636, 2)]
    [DataRow("create table t (j json, i int); select row_number() over (order by j) from t", 13636, 2)]
    [DataRow("declare @j json = '[1]'; select case when @j = '[1]' then 1 end", 402, 1)]
    [DataRow("declare @j json = '[1]'; select @j + N'x'", 402, 1)]
    [DataRow("create table t (j json); select null + j from t", 402, 1)]
    [DataRow("create table t (j json); select distinct j from t", 421, 1)]
    [DataRow("create table t (j json); select j from t union select j from t", 5335, 1)]
    [DataRow("create table t (j json); select max(j) from t", 8117, 1)]
    [DataRow("create table t (j json); insert t values ('[1]'); select count(j) from t", 8117, 2)]
    [DataRow("create table t (j json); insert t values ('[1]'); select count(distinct j) from t", 8117, 2)]
    [DataRow("create table t (j json primary key)", 1919, 1)]
    [DataRow("create table t (j json); create index ix on t (j)", 1978, 3)]
    [DataRow("create table t (j json); create statistics s on t (j)", 1978, 3)]
    [DataRow("declare @j json = '[1]'; select len(@j)", 8116, 1)]
    [DataRow("declare @j json = '[1]'; select upper(@j)", 8116, 1)]
    [DataRow("declare @j json = '[1]'; select checksum(@j)", 8116, 4)]
    [DataRow("declare @j json = '[1]'; select greatest(@j, @j)", 8116, 4)]
    [DataRow("declare @j json = '[1]'; select hashbytes('SHA2_256', @j)", 8116, 1)]
    public void Comparability_Refusals(string commandText, int number, int state)
    {
        var ex = new Simulation().AssertSqlError(commandText, number);
        AreEqual((byte)state, ex.Errors[0].State);
    }

    [TestMethod]
    public void Comparability_WhatStillWorks()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (i int, j json check (json_value(j, '$.a') is not null));
            create index ix on t (i) include (j);
            insert t values (1, '{"a":1}'), (2, '{ "a" : 2 }');
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t where j is not null"));
        _ = sim.AssertSqlError("insert t values (3, '{\"b\":1}')", 547);
        _ = sim.ExecuteNonQuery("create table c (j json); create clustered columnstore index cci on c");
    }

    [TestMethod]
    public void Functions_ReadJsonDocuments()
    {
        var sim = new Simulation();
        AreEqual("1|x|true|100.0000000000|{\"b\":[1,\"x\",true,null,100.0000000000]}|1|1|1", sim.ExecuteScalar("""
            declare @j json = '{"a":{"b":[1,"x",true,null,1e2]}}';
            select concat_ws('|', json_value(@j, '$.a.b[0]'), json_value(@j, '$.a.b[1]'), json_value(@j, '$.a.b[2]'), json_value(@j, '$.a.b[4]'),
                cast(json_query(@j, '$.a') as nvarchar(max)), json_path_exists(@j, '$.a.b'), isjson(@j), isjson(@j, object))
            """));
        AreEqual("a:1:2|b:[1,2]:4|c:s:1", sim.ExecuteScalar("""
            declare @j json = '{"a":1,"b":[1, 2],"c":"s"}';
            select string_agg(concat([key], ':', value, ':', type), '|') from openjson(@j)
            """));
        AreEqual("[1,2]", sim.ExecuteScalar("""select cast(b as nvarchar(max)) from openjson(N'{"b":[1, 2]}') with (b json as json)"""));
        AreEqual(1, sim.ExecuteScalar("""
            declare @j json = '{"a":1,"b":[1, 2]}';
            select count(*) from openjson(@j) with (b json, a json) where a is null and b is null
            """));
        _ = sim.AssertSqlError("""select * from openjson(N'{"a":1}') with (a json)""", 13609);
        AreEqual("json|json|json", sim.ExecuteScalar("""
            select string_agg(system_type_name, '|') from sys.dm_exec_describe_first_result_set(N'declare @j json = ''{}''; select @j j, json_modify(@j, ''$.a'', 1) m, json_query(@j) q', null, 0)
            """));
    }

    [TestMethod]
    [DataRow("declare @j json = '{\"a\":1}'; select json_value(@j, 'strict $.b')", 5)]
    [DataRow("declare @j json = '{\"a\":1}'; select json_query(@j, 'strict $.b')", 5)]
    [DataRow("declare @j json = '{\"a\":1}'; select json_modify(@j, 'strict $.b', 5)", 5)]
    [DataRow("declare @j json = '{\"a\":1}'; select * from openjson(@j, 'strict $.x')", 7)]
    [DataRow("declare @j json = '{\"a\":1}'; select * from openjson(@j) with (b int 'strict $.b')", 8)]
    public void Functions_StrictMissStates(string commandText, int state)
    {
        var ex = new Simulation().AssertSqlError(commandText, 13608);
        AreEqual((byte)state, ex.Errors[0].State);
    }

    [TestMethod]
    public void Functions_ModifyReturnsCanonicalJson()
    {
        var sim = new Simulation();
        // Real refuses an append path beside another JSON_MODIFY over json
        // in one select list (Msg 13656), so each edit runs on its own.
        AreEqual("{\"a\":1,\"b\":\"x\"}", sim.ExecuteScalar("declare @j json = '{\"a\":1}'; select cast(json_modify(@j, '$.b', 'x') as nvarchar(max))"));
        AreEqual("{}", sim.ExecuteScalar("declare @j json = '{\"a\":1}'; select cast(json_modify(@j, '$.a', null) as nvarchar(max))"));
        AreEqual("{\"a\":[1,2]}", sim.ExecuteScalar("declare @k json = '{\"a\":[1]}'; select cast(json_modify(@k, 'append $.a', 2) as nvarchar(max))"));
        AreEqual("{\"a\":1.5000000000}", sim.ExecuteScalar("declare @j json = '{\"a\":1}'; select cast(json_modify(@j, '$.a', 1.5e0) as nvarchar(max))"));
        AreEqual("{\"a\":{\"b\":[1,\"z\"]}}", sim.ExecuteScalar("declare @n json = '{\"a\":{\"b\":[1,2]}}'; select cast(json_modify(@n, '$.a.b[1]', 'z') as nvarchar(max))"));
        sim.AssertSqlError("declare @j json = '{\"a\":1}'; select json_modify(N'{\"x\":0}', '$.x', @j)", 8116, "Argument data type json is invalid for argument 3 of json_modify function.");
    }

    [TestMethod]
    public void Functions_BuildersReturnJsonForJsonInput()
    {
        var sim = new Simulation();
        AreEqual("{\"a\":{\"x\":[1,2]}}|[{\"x\":[1,2]}]|{\"a\":1}|[1,2]|{\"a\":1.5000000000}", sim.ExecuteScalar("""
            declare @j json = '{"x":[1, 2]}';
            select concat_ws('|', cast(json_object('a': @j) as nvarchar(max)), cast(json_array(@j) as nvarchar(max)),
                cast(json_object('a':1 returning json) as nvarchar(max)), cast(json_array(1, 2 returning json) as nvarchar(max)),
                cast(json_object('a':1.5e0 returning json) as nvarchar(max)))
            """));
        AreEqual("json|json|nvarchar(max)|nvarchar(max)", sim.ExecuteScalar("""
            select string_agg(system_type_name, '|') from sys.dm_exec_describe_first_result_set(N'declare @j json = ''{}''; select json_object(''a'': @j) o, json_array(1, @j) a, json_object(''a'':json_query(N''{}'')) t, json_array(1) n', null, 0)
            """));
        sim.AssertSqlError("select json_object('a':1 returning nvarchar(max))", 102, "Incorrect syntax near 'RETURNING. Supported Syntax is RETURNING JSON'.");
        sim.AssertSqlError("select json_array(1 returning int)", 102, "Incorrect syntax near 'RETURNING'.");
    }

    [TestMethod]
    public void Functions_AggregatesAndForJson()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, j json); insert t values (1, '{\"a\":[1, 2]}'), (2, '[2]'), (3, null)");
        AreEqual("[{\"a\":[1,2]},[2]]|[{\"a\":[1,2]},[2],null]|{\"1\":{\"a\":[1,2]},\"2\":[2],\"3\":null}|[1,2,3]", sim.ExecuteScalar("""
            select concat_ws('|', cast(json_arrayagg(j) as nvarchar(max)), cast(json_arrayagg(j null on null) as nvarchar(max)),
                cast(json_objectagg(cast(id as varchar(5)) : j) as nvarchar(max)), cast(json_arrayagg(id returning json) as nvarchar(max)))
            from t
            """));
        AreEqual("[{\"id\":1,\"j\":{\"a\":[1,2]}},{\"id\":2,\"j\":[2]},{\"id\":3,\"j\":null}]", sim.ExecuteScalar("select (select id, j from t for json path, include_null_values)"));
        AreEqual("<row><id>2</id><j>[2]</j></row>", sim.ExecuteScalar("select (select id, j from t where id = 2 for xml path)"));
    }

    [TestMethod]
    public void AlterColumn()
    {
        var sim = new Simulation();
        AreEqual("{\"a\":1}", sim.ExecuteScalar("create table t (s nvarchar(max)); insert t values ('{ \"a\" : 1 }'); alter table t alter column s json; select cast(s as nvarchar(max)) from t"));
        sim.AssertSqlError("create table u (j json); alter table u alter column j nvarchar(max)", 257, "Implicit conversion from data type json to nvarchar(max) is not allowed. Use the CONVERT function to run this query.");
        sim.AssertSqlError("create table v (i int); alter table v alter column i json", 206, "Operand type clash: int is incompatible with json");
        _ = sim.AssertSqlError("create table w (s nvarchar(max)); insert w values ('x'); alter table w alter column s json", 13609);
    }

    [TestMethod]
    public void Catalog()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int, j json)");
        AreEqual("json|244|244|-1|0|0|1", sim.ExecuteScalar("select concat_ws('|', name, system_type_id, user_type_id, max_length, precision, scale, is_nullable) from sys.types where name = 'json'"));
        AreEqual("244|244|-1|0|0|1", sim.ExecuteScalar("select concat_ws('|', system_type_id, user_type_id, max_length, precision, scale, isnull(collation_name, '1')) from sys.columns where object_id = object_id('t') and name = 'j'"));
        AreEqual("json|244|244|json", sim.ExecuteScalar("select concat_ws('|', type_name(244), type_id('json'), type_id('sys.json'), type_name(type_id('json')))"));
        AreEqual("-1|-1", sim.ExecuteScalar("select concat_ws('|', col_length('t', 'j'), columnproperty(object_id('t'), 'j', 'Precision'))"));
        AreEqual("json|-1|-1", sim.ExecuteScalar("select concat_ws('|', data_type, character_maximum_length, character_octet_length) from information_schema.columns where table_name = 't' and column_name = 'j'"));
        AreEqual("json|244|-1", sim.ExecuteScalar("select concat_ws('|', system_type_name, system_type_id, max_length) from sys.dm_exec_describe_first_result_set(N'select j from t', null, 0)"));
        using var columns = sim.ExecuteReader("exec sp_columns 't'");
        IsTrue(columns.Read());
        AreEqual("i", columns["COLUMN_NAME"]);
        IsFalse(columns.Read());
    }
}
