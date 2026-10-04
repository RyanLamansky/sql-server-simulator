using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's <c>vector(n)</c> type: its text form in both directions,
/// storage, conversions, the refusals of a non-comparable type, the four
/// vector built-ins and the catalog surfaces. Every expectation was probed
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class VectorTests
{
    [TestMethod]
    [DataRow("[1, 2, 3]", "[1.0000000e+000,2.0000000e+000,3.0000000e+000]")]
    [DataRow("[0.1, 123456789.123]", "[1.0000000e-001,1.2345679e+008]")]
    [DataRow("[0, -0, -1.5]", "[0.0000000e+000,0.0000000e+000,-1.5000000e+000]")]
    [DataRow("[1e-50, 2]", "[0.0000000e+000,2.0000000e+000]")]
    [DataRow("[1e-40, 1e-45]", "[9.9999461e-041,1.4012985e-045]")]
    [DataRow("[3.4028235e38, -3.4028235e38]", "[3.4028235e+038,-3.4028235e+038]")]
    [DataRow("[1e2, -0.5E-1, 1.5e+3]", "[1.0000000e+002,-5.0000001e-002,1.5000000e+003]")]
    [DataRow("[0.3, 9.99999999, 16777217]", "[3.0000001e-001,1.0000000e+001,1.6777216e+007]")]
    [DataRow(" [ 1 ,\t2\r\n] ", "[1.0000000e+000,2.0000000e+000]")]
    [DataRow("[1,-0.0e-0]", "[1.0000000e+000,0.0000000e+000]")]
    [DataRow("[1,1e-400]", "[1.0000000e+000,0.0000000e+000]")]
    public void TextForm_RoundTrips(string input, string expected)
    {
        var dimensions = input.Count(c => c == ',') + 1;
        AreEqual(expected, new Simulation().ExecuteScalar($"declare @v vector({dimensions}) = '{input}'; select cast(@v as varchar(max))"));
    }

    [TestMethod]
    [DataRow("1, 2", 13670, 1, "Input JSON is not a valid Vector : 'Malformed JSON'.")]
    [DataRow("[[1],2]", 13670, 1, "Input JSON is not a valid Vector : 'Malformed JSON'.")]
    [DataRow("[[1", 13670, 1, "Input JSON is not a valid Vector : 'Malformed JSON'.")]
    [DataRow("1", 13670, 1, "Input JSON is not a valid Vector : 'Malformed JSON'.")]
    [DataRow("null", 13670, 4, "Input JSON is not a valid Vector : 'Null Not Supported'.")]
    [DataRow("[1,null", 13670, 4, "Input JSON is not a valid Vector : 'Null Not Supported'.")]
    [DataRow("[1, \"a\"]", 13670, 5, "Input JSON is not a valid Vector : 'String not Supported'.")]
    [DataRow("[1,2,3,\"a\"]", 13670, 5, "Input JSON is not a valid Vector : 'String not Supported'.")]
    [DataRow("[{}]", 13670, 7, "Input JSON is not a valid Vector : 'Empty object Not Suppoted'.")]
    [DataRow("[]", 13670, 8, "Input JSON is not a valid Vector : 'Empty Array not Supported'.")]
    [DataRow("[]x", 13670, 8, "Input JSON is not a valid Vector : 'Empty Array not Supported'.")]
    [DataRow("[[]]", 13670, 8, "Input JSON is not a valid Vector : 'Empty Array not Supported'.")]
    [DataRow("[true,2]", 13670, 9, "Input JSON is not a valid Vector : 'Boolean not supported'.")]
    [DataRow("[1,false]", 13670, 9, "Input JSON is not a valid Vector : 'Boolean Not Supported'.")]
    [DataRow("{\"a\":1}", 13670, 10, "Input JSON is not a valid Vector : 'Object Not Supported'.")]
    [DataRow("[1,{\"a\":", 13670, 10, "Input JSON is not a valid Vector : 'Object Not Supported'.")]
    [DataRow("[1,2] x", 13609, 9, "JSON text is not properly formatted. Unexpected character 'x' is found at position 6.")]
    [DataRow("[01, 2]", 13609, 9, "JSON text is not properly formatted. Unexpected character '0' is found at position 1.")]
    [DataRow("[1.5x, 2]", 13609, 9, "JSON text is not properly formatted. Unexpected character '1' is found at position 1.")]
    [DataRow("[1,,2]", 13609, 9, "JSON text is not properly formatted. Unexpected character ',' is found at position 3.")]
    [DataRow("[1,2,]", 13609, 9, "JSON text is not properly formatted. Unexpected character ']' is found at position 5.")]
    [DataRow("[1 2]", 13609, 9, "JSON text is not properly formatted. Unexpected character '2' is found at position 3.")]
    [DataRow("[1,2", 13609, 9, "JSON text is not properly formatted. Unexpected character '.' is found at position 4.")]
    [DataRow("", 13609, 9, "JSON text is not properly formatted. Unexpected character '.' is found at position 0.")]
    [DataRow("[{", 13609, 9, "JSON text is not properly formatted. Unexpected character '.' is found at position 2.")]
    [DataRow("[1,\"abc", 13609, 9, "JSON text is not properly formatted. Unexpected character '\"' is found at position 3.")]
    [DataRow("[1,nul]", 13609, 9, "JSON text is not properly formatted. Unexpected character 'n' is found at position 3.")]
    [DataRow("[1,2,é]", 13609, 9, "JSON text is not properly formatted. Unexpected character 'Ã' is found at position 5.")]
    [DataRow("[1e39, \"a\"]", 42241, 1, "Input JSON contains out-of-range values for float32.")]
    [DataRow("[3.4028236e38, 1]", 42241, 1, "Input JSON contains out-of-range values for float32.")]
    [DataRow("[1,2,3]", 42204, 4, "The vector dimensions 2 and 3 do not match.")]
    public void TextForm_Refusals(string input, int number, int state, string message) =>
        AssertSqlError($"select cast(N'{input}' as vector(2))", number, (byte)state, message);

    [TestMethod]
    [DataRow("declare @v vector(3, float16)", 195, 1, "'float16' is not a recognized vector base type.")]
    [DataRow("create table t (v vector(2, bogus))", 195, 1, "'bogus' is not a recognized vector base type.")]
    [DataRow("declare @v vector(0)", 1001, 1, "Line 1: Length or precision specification 0 is invalid.")]
    [DataRow("declare @v vector(1999)", 2717, 3, "The size (1999) given to the type 'vector' exceeds the maximum allowed (1998).")]
    [DataRow("create table t (v vector(1999))", 2717, 4, "The size (1999) given to the column 'v' exceeds the maximum allowed (1998).")]
    [DataRow("select cast('[1]' as vector(1999))", 2717, 3, "The size (1999) given to the type 'vector' exceeds the maximum allowed (1998).")]
    [DataRow("select cast('[1]' as vector)", 243, 1, "Type vector is not a defined system type.")]
    [DataRow("declare @v vector(2, 5)", 192, 1, "The scale must be less than or equal to the precision.")]
    [DataRow("declare @v vector(2, 'float32')", 102, 1, "Incorrect syntax near 'float32'.")]
    [DataRow("declare @d decimal(5, float32)", 102, 1, "Incorrect syntax near 'decimal'.")]
    [DataRow("create type vt from vector(3)", 42212, 1, "Cannot create alias types from a vector datatype.")]
    public void TypeSpec_Refusals(string commandText, int number, int state, string message) =>
        AssertSqlError(commandText, number, (byte)state, message);

    [TestMethod]
    [DataRow("declare @v vector")]
    [DataRow("declare @v vector(max)")]
    [DataRow("declare @v vector(5, 2)")]
    public void TypeSpec_WithoutUsableDimensions_IsUnknownType(string commandText) =>
        _ = new Simulation().AssertSqlError(commandText, 2715);

    [TestMethod]
    public void TypeSpec_Float32BaseType_IsAccepted() =>
        AreEqual("[1.0000000e+000,2.0000000e+000]", new Simulation().ExecuteScalar("""
            declare @v vector(2, FLOAT32) = '[1,2]';
            select cast(cast(@v as vector(2, float32)) as varchar(50))
            """));

    [TestMethod]
    public void Storage_TableRoundTrip()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity, v vector(3));
            insert t (v) values ('[1,2,3]'), ('[0.5, -1e2, 3.25]'), (null);
            update t set v = '[4,5,6]' where id = 1
            """);
        AreEqual("[4.0000000e+000,5.0000000e+000,6.0000000e+000]|[5.0000000e-001,-1.0000000e+002,3.2500000e+000]",
            sim.ExecuteScalar("select string_agg(cast(v as varchar(max)), '|') within group (order by id) from t"));
        AreEqual(20, sim.ExecuteScalar("select datalength(v) from t where id = 1"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where v is null"));
    }

    [TestMethod]
    public void Storage_WidestVectorShareARowWithOtherColumns() =>
        AreEqual(8000, new Simulation().ExecuteScalar("""
            create table t (id int, v vector(1998), s varchar(100));
            declare @text varchar(max) = '[' + replicate(cast('1,' as varchar(max)), 1997) + '1]';
            insert t values (1, @text, replicate('x', 100));
            select datalength(v) from t where s like 'x%'
            """));

    [TestMethod]
    public void Storage_TempTableTableVariableAndSelectInto()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (v vector(2));
            insert t values ('[1,2]');
            declare @tv table (v vector(2));
            insert @tv select v from t;
            create table #tt (v vector(2));
            insert #tt select v from @tv;
            select * into u from #tt
            """).ExecuteNonQuery();
        AreEqual("[1.0000000e+000,2.0000000e+000]", connection.CreateCommand("select cast(v as varchar(50)) from u").ExecuteScalar());
        AreEqual("vector|16", connection.CreateCommand("select type_name(user_type_id) + '|' + cast(max_length as varchar) from sys.columns where object_id = object_id('u')").ExecuteScalar());
    }

    [TestMethod]
    public void Storage_ProcedureParameters()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p @v vector(2), @o vector(2) output as set @o = vector_normalize(@v, 'norm1')");
        AreEqual("[2.5000000e-001,7.5000000e-001]", sim.ExecuteScalar("declare @r vector(2); exec p '[1,3]', @r output; select cast(@r as varchar(50))"));
    }

    [TestMethod]
    public void Storage_OutputClause() =>
        AreEqual("[1.0000000e+000,2.0000000e+000]", new Simulation().ExecuteScalar("""
            create table t (v vector(2));
            insert t output cast(inserted.v as varchar(50)) values ('[1,2]')
            """));

    [TestMethod]
    [DataRow("declare @v vector(2) = '[1,2]'; declare @s varchar(100) = @v; select @s", "[1.0000000e+000,2.0000000e+000]")]
    [DataRow("declare @v vector(2) = '[1,2]'; select convert(nvarchar(31), @v)", "[1.0000000e+000,2.0000000e+000]")]
    [DataRow("declare @v vector(2) = '[1,2]'; select cast(@v as char(40))", "[1.0000000e+000,2.0000000e+000]         ")]
    [DataRow("declare @v vector(2) = '[1,2]'; select json_array(@v)", "[[1.0000000e+000,2.0000000e+000]]")]
    [DataRow("declare @v vector(2) = '[1,2]'; select json_object('a': @v)", "{\"a\":[1.0000000e+000,2.0000000e+000]}")]
    [DataRow("declare @v vector(2) = '[1,2]'; select cast(isnull(cast(null as vector(2)), @v) as varchar(50))", "[1.0000000e+000,2.0000000e+000]")]
    [DataRow("declare @v vector(2) = '[1,2]'; select cast(iif(1 = 1, @v, null) as varchar(50))", "[1.0000000e+000,2.0000000e+000]")]
    [DataRow("select cast(cast(json_array(1, 2) as vector(2)) as varchar(50))", "[1.0000000e+000,2.0000000e+000]")]
    public void Conversion_ToAndFromText(string commandText, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    [DataRow("select try_cast('[1,x]' as vector(2))")]
    [DataRow("select try_convert(vector(2), '[1,2,3]')")]
    [DataRow("declare @v vector(2) = '[1,2]'; select try_cast(@v as varchar(5))")]
    public void Conversion_TryFormsAnswerNull(string commandText) =>
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    [DataRow("select cast(1 as vector(1))", 529, "Explicit conversion from data type int to vector is not allowed.")]
    [DataRow("declare @v vector(1) = '[1]'; select cast(@v as int)", 529, "Explicit conversion from data type vector to int is not allowed.")]
    [DataRow("declare @v vector(1) = '[1]'; select cast(@v as varbinary(100))", 529, "Explicit conversion from data type vector to varbinary is not allowed.")]
    [DataRow("declare @v vector(1) = '[1]'; select cast(@v as sql_variant)", 529, "Explicit conversion from data type vector to sql_variant is not allowed.")]
    [DataRow("declare @v vector(1) = '[1]'; select cast(@v as text)", 529, "Explicit conversion from data type vector to text is not allowed.")]
    [DataRow("select cast(0x01 as vector(2))", 529, "Explicit conversion from data type varbinary to vector is not allowed.")]
    [DataRow("declare @v vector(2) = '[1,2]'; select cast(@v as varchar)", 42211, "Truncation of vector is not allowed during the conversion. Ensure the vector size is appropriate before conversion.")]
    [DataRow("declare @v vector(2) = '[1,2]'; select cast(@v as nvarchar(10))", 42211, "Truncation of vector is not allowed during the conversion. Ensure the vector size is appropriate before conversion.")]
    [DataRow("declare @v vector(2) = '[1,2]'; declare @s varchar(20) = @v", 42211, "Truncation of vector is not allowed during the conversion. Ensure the vector size is appropriate before conversion.")]
    [DataRow("create table t (v vector(3)); insert t values ('[1,2,3]'); select coalesce(v, '[1,2,3]') from t", 42211, "Truncation of vector is not allowed during the conversion. Ensure the vector size is appropriate before conversion.")]
    [DataRow("declare @v vector(2) = '[1,2]'; select cast(@v as vector(3))", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("declare @v vector(2) = '[1,2]', @w vector(3) = '[1,2,3]'; select case when 1 = 1 then @v else @w end", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("declare @v vector(2) = '[1,2]', @w vector(3) = '[1,2,3]'; select coalesce(@v, @w)", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("declare @v vector(2) = '[1,2]', @w vector(3) = '[1,2,3]'; select isnull(@v, @w)", 42204, "The vector dimensions 3 and 2 do not match.")]
    [DataRow("declare @v vector(2) = '[1,2]', @w vector(3) = '[1,2,3]'; select @v union all select @w", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("create table t (v vector(2)); create table u (v vector(3)); insert u select v from t", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("create table t (v vector(2)); alter table t alter column v vector(3)", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("create table t (v int); alter table t alter column v vector(3)", 206, "Operand type clash: int is incompatible with vector")]
    [DataRow("create table t (v vector(2)); insert t values (1)", 206, "Operand type clash: int is incompatible with vector")]
    public void Conversion_Refusals(string commandText, int number, string message) =>
        new Simulation().AssertSqlError(commandText, number, message);

    [TestMethod]
    public void Conversion_AlterColumnBetweenTextAndVector()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v varchar(max)); insert t values ('[1,2]')");
        _ = sim.ExecuteNonQuery("alter table t alter column v vector(2)");
        AreEqual(16, sim.ExecuteScalar("select datalength(v) from t"));
        _ = sim.ExecuteNonQuery("alter table t alter column v nvarchar(max)");
        AreEqual("[1.0000000e+000,2.0000000e+000]", sim.ExecuteScalar("select v from t"));
    }

    // The type-pair grids' vector row and column, one representative per
    // shape of refusal.
    [TestMethod]
    [DataRow("select 1 from t where v = v", 8117, "Operand data type vector is invalid for equal to operator.")]
    [DataRow("select 1 from t where v = null", 8117, "Operand data type vector is invalid for equal to operator.")]
    [DataRow("select 1 from t where 1 = v", 402, "The data types tinyint and vector are incompatible in the equal to operator.")]
    [DataRow("select 1 from t where v = h", 206, "Operand type clash: vector is incompatible with hierarchyid")]
    [DataRow("select 1 from t where g = v", 403, "Invalid operator for data type. Operator equals equal to, type equals geography.")]
    [DataRow("select v + v from t", 8117, "Operand data type vector is invalid for add operator.")]
    [DataRow("select 'x' + v from t", 8117, "Operand data type vector is invalid for add operator.")]
    [DataRow("select v + ts from t", 402, "The data types vector and timestamp are incompatible in the add operator.")]
    [DataRow("select null * v from t", 402, "The data types NULL and vector are incompatible in the multiply operator.")]
    [DataRow("select v & null from t", 8117, "Operand data type vector is invalid for '&' operator.")]
    [DataRow("select case when 1 = 0 then v else 1 end from t", 206, "Operand type clash: vector is incompatible with int")]
    [DataRow("select case when 1 = 0 then v else 0x01 end from t", 206, "Operand type clash: varbinary is incompatible with vector")]
    [DataRow("select 1 from t where v in (select v from t)", 8117, "Operand data type vector is invalid for equal to operator.")]
    [DataRow("select nullif(v, v) from t", 8117, "Operand data type vector is invalid for equal to operator.")]
    public void Comparison_Refusals(string query, int number, string message) =>
        new Simulation().AssertSqlError($"create table t (v vector(2), h hierarchyid, g geography, ts timestamp); {query}", number, message);

    [TestMethod]
    [DataRow("select v from t group by v", 42213, 1, "The vector data types cannot be compared or sorted, except when using the IS NULL operator.")]
    [DataRow("select v from t order by v", 42213, 1, "The vector data types cannot be compared or sorted, except when using the IS NULL operator.")]
    [DataRow("select row_number() over (partition by v order by (select 1)) from t", 42213, 1, "The vector data types cannot be compared or sorted, except when using the IS NULL operator.")]
    [DataRow("select distinct v from t", 421, 1, "The vector data type cannot be selected as DISTINCT because it is not comparable.")]
    [DataRow("select v from t union select v from t", 5335, 1, "The data type vector cannot be used as an operand to the UNION, INTERSECT or EXCEPT operators because it is not comparable.")]
    [DataRow("select min(v) from t", 8117, 1, "Operand data type vector is invalid for min operator.")]
    [DataRow("select count(distinct v) from t", 8117, 2, "Operand data type vector is invalid for count operator.")]
    [DataRow("select approx_count_distinct(v) from t", 8117, 2, "Operand data type vector is invalid for approx_count_distinct operator.")]
    [DataRow("select checksum(v) from t", 8116, 4, "Argument data type vector(2) is invalid for argument 1 of checksum function.")]
    [DataRow("select binary_checksum(v) from t", 8184, 1, "Error in binarychecksum. There are no comparable columns in the binarychecksum input.")]
    [DataRow("select greatest(v, v) from t", 8116, 4, "Argument data type vector is invalid for argument 1 of greatest function.")]
    [DataRow("select concat('x', v) from t", 8116, 9, "Argument data type vector is invalid for argument 2 of concat function.")]
    [DataRow("select len(v) from t", 8116, 1, "Argument data type vector is invalid for argument 1 of len function.")]
    [DataRow("select format(v, 'g') from t", 8116, 1, "Argument data type vector is invalid for argument 1 of format function.")]
    [DataRow("select replace(v, '1', '2') from t", 8116, 6, "Argument data type vector is invalid for argument 1 of replace function.")]
    [DataRow("select trim(v) from t", 8116, 6, "Argument data type vector is invalid for argument 1 of Trim function.")]
    [DataRow("select hashbytes('md5', v) from t", 8116, 6, "Argument data type vector is invalid for argument 2 of hashbytes function.")]
    [DataRow("select 1 from t where v like 'x'", 8116, 6, "Argument data type vector is invalid for argument 1 of like function.")]
    [DataRow("select sql_variant_property(v, 'BaseType') from t", 8116, 6, "Argument data type vector is invalid for argument 1 of sql_variant_property function.")]
    [DataRow("select string_agg(v, ',') from t", 8116, 1, "Argument data type vector is invalid for argument 1 of string_agg function.")]
    public void Comparability_Refusals(string query, int number, int state, string message) =>
        AssertSqlError($"create table t (v vector(2)); {query}", number, (byte)state, message);

    [TestMethod]
    public void Comparability_CountAndIsNullStillWork() =>
        AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (v vector(2));
            insert t values ('[1,2]'), (null);
            select count(v) from t where v is not null
            """));

    [TestMethod]
    [DataRow("create table t (v vector(2) primary key)", 1919, 1, "Column 'v' in table 't' is of a type that is invalid for use as a key column in an index.")]
    [DataRow("create table t (id int, v vector(3), unique (id, v))", 1919, 1, "Column 'v' in table 't' is of a type that is invalid for use as a key column in an index.")]
    [DataRow("create table t (id int, v vector(2)); create index ix on t (v)", 1978, 4, "Column 'v' in table 't' is of a type that is invalid for use as a key column in an index or statistics.")]
    [DataRow("create table t (id int, v vector(2)); create statistics s on t (v)", 1978, 4, "Column 'v' in table 't' is of a type that is invalid for use as a key column in an index or statistics.")]
    [DataRow("create table t (v vector(2) check (vectorproperty(v, 'Dimensions') = 2))", 1760, 0, "Constraints of type CHECK cannot be created on columns of type vector.")]
    [DataRow("create table t (v vector(3)); alter table t add constraint ck check (v is not null)", 1760, 0, "Constraints of type CHECK cannot be created on columns of type vector.")]
    [DataRow("create table t (id int, v vector(2) default null)", 1752, 1, "Column 'v' in table 't' is invalid for creating a default constraint.")]
    [DataRow("create table t (v vector(2)); alter table t add constraint d default '[1,2]' for v", 1752, 1, "Column 'v' in table 't' is invalid for creating a default constraint.")]
    [DataRow("create table t (v vector(2) collate Latin1_General_CI_AS)", 447, 1, "Expression type vector is invalid for COLLATE clause.")]
    [DataRow("create table t (id int, v vector(2), c as vector_norm(v, 'norm2') persisted)", 4936, 1, "Computed column 'c' in table 't' cannot be persisted because the column is non-deterministic.")]
    public void Ddl_Refusals(string commandText, int number, int state, string message) =>
        AssertSqlError(commandText, number, (byte)state, message);

    [TestMethod]
    public void Ddl_KeyRefusalIsFollowedByMsg1750()
    {
        var ex = new Simulation().AssertSqlError("create table t (v vector(2) primary key)", 1919);
        AreEqual(1750, ex.Errors[1].Number);
    }

    [TestMethod]
    public void Ddl_IncludedColumnIsAccepted() =>
        AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int, v vector(2));
            create index ix on t (id) include (v);
            select count(*) from sys.indexes where name = 'ix'
            """));

    // A vector generated in SQL, so the expected values stay short: element i
    // of @a is sin(1.7 i) * 100 and of @b cos(0.3 i) * 10.
    [TestMethod]
    [DataRow(5, 0.836764395236969, 165.2098388671875, -396.49871826171875, 346.56209564208984, 166.9599694396697, 9.553359985351562)]
    [DataRow(9, 1.0300229787826538, 214.89102172851562, 124.6475830078125, 603.9535980224609, 213.42549783365817, 9.553359985351562)]
    [DataRow(17, 0.9489137530326843, 294.86956787109375, -412.8763732910156, 1105.4127922058105, 294.9969976676718, 9.899920463562012)]
    [DataRow(40, 1.00795578956604, 453.8735046386719, 157.053466796875, 2574.396728515625, 451.4140478298443, 9.998590469360352)]
    [DataRow(100, 0.9906471371650696, 710.4994506835938, -464.57244873046875, 6369.537179946899, 707.6807781782296, 9.998590469360352)]
    [DataRow(300, 0.9982957243919373, 1232.26416015625, -255.0894775390625, 19156.415267944336, 1226.4134999223913, 9.999750137329102)]
    [DataRow(1536, 0.9994217753410339, 2785.210205078125, -443.80450439453125, 97799.4091796875, 2771.5684075086438, 10.0)]
    public void Functions_MatchRealToTheLastBit(int dimensions, double cosine, double euclidean, double dot, double norm1, double norm2, double normInf)
    {
        using var reader = new Simulation().ExecuteReader($"""
            declare @a vector({dimensions}) = (select '[' + string_agg(cast(cast(sin(value * 1.7) * 100 as varchar(30)) as varchar(max)), ',') within group (order by value) + ']' from generate_series(1, {dimensions}));
            declare @b vector({dimensions}) = (select '[' + string_agg(cast(cast(cos(value * 0.3) * 10 as varchar(30)) as varchar(max)), ',') within group (order by value) + ']' from generate_series(1, {dimensions}));
            select vector_distance('cosine', @a, @b), vector_distance('euclidean', @a, @b), vector_distance('dot', @a, @b),
                vector_norm(@a, 'norm1'), vector_norm(@a, 'norm2'), vector_norm(@b, 'norminf')
            """);
        IsTrue(reader.Read());
        double[] expected = [cosine, euclidean, dot, norm1, norm2, normInf];
        for (var i = 0; i < expected.Length; i++)
            AreEqual(expected[i], reader.GetDouble(i), $"column {i}");
    }

    [TestMethod]
    [DataRow("vector_distance('cosine', @a, @b)", 0.025368213653564453)]
    [DataRow("vector_distance('COSINE', @a, @a)", 5.960464477539063E-08)]
    [DataRow("vector_distance('euclidean', @a, @b)", 5.196152210235596)]
    [DataRow("vector_distance(cast('dot' as char(10)), @a, @b)", -32.0)]
    [DataRow("vector_distance('cosine', @z, @a)", 1.0)]
    [DataRow("vector_distance('cosine', @big, @neg)", 0.0)]
    [DataRow("vector_norm(@c, 'norm1')", 6.0)]
    [DataRow("vector_norm(@c, N'NORM2')", 3.7416573867739413)]
    [DataRow("vector_norm(@c, 'norminf')", 3.0)]
    [DataRow("vector_norm(@big, 'norm1')", 6.0000000109955115E+38)]
    public void Functions_Values(string call, double expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"""
            declare @a vector(3) = '[1, 2, 3]', @b vector(3) = '[4, 5, 6]', @c vector(3) = '[1, -2, 3]', @z vector(3) = '[0, 0, 0]',
                @big vector(2) = '[3e38, 3e38]', @neg vector(2) = '[-3e38, -3e38]';
            select {call}
            """));

    [TestMethod]
    public void Functions_DotOfZerosIsNegativeZero() =>
        IsTrue(double.IsNegative((double)new Simulation().ExecuteScalar("declare @z vector(2) = '[0,0]'; select vector_distance('dot', @z, @z)")!));

    [TestMethod]
    [DataRow("vector_normalize(@c, 'norm2')", "[2.6726124e-001,-5.3452247e-001,8.0178368e-001]")]
    [DataRow("vector_normalize(@z, 'norm2')", "[0.0000000e+000,0.0000000e+000,0.0000000e+000]")]
    [DataRow("vector_normalize(@big, 'norm2')", "[0.0000000e+000,0.0000000e+000,0.0000000e+000]")]
    [DataRow("vector_normalize(@big, 'norminf')", "[1.0000000e+000,1.0000000e+000,1.0000000e+000]")]
    public void Functions_Normalize(string call, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"""
            declare @c vector(3) = '[1, -2, 3]', @z vector(3) = '[0, 0, 0]', @big vector(3) = '[3e38, 3e38, 3e38]';
            select cast({call} as varchar(max))
            """));

    [TestMethod]
    public void Functions_VectorProperty()
    {
        using var reader = new Simulation().ExecuteReader("""
            declare @v vector(3) = '[1,2,3]';
            select vectorproperty(@v, 'Dimensions'), vectorproperty(@v, 'basetype'), vectorproperty(@v, 'bogus'),
                sql_variant_property(vectorproperty(@v, 'Dimensions'), 'BaseType'), sql_variant_property(vectorproperty(@v, 'BaseType'), 'MaxLength')
            """);
        IsTrue(reader.Read());
        AreEqual((short)3, reader.GetValue(0));
        AreEqual("float32", reader.GetValue(1));
        AreEqual(DBNull.Value, reader.GetValue(2));
        AreEqual("smallint", reader.GetValue(3));
        AreEqual(256, reader.GetValue(4));
    }

    [TestMethod]
    [DataRow("vector_distance('cosine', @v, @n)")]
    [DataRow("vector_distance(null, @v, @v)")]
    [DataRow("vector_norm(@n, 'norm2')")]
    [DataRow("vector_norm(@v, @m)")]
    [DataRow("vector_normalize(@n, 'norm2')")]
    [DataRow("vector_normalize(null, 'norm2')")]
    [DataRow("vectorproperty(@n, 'Dimensions')")]
    public void Functions_NullArgumentAnswersNull(string call) =>
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"declare @v vector(2) = '[1,2]', @n vector(2), @m varchar(10); select {call}"));

    [TestMethod]
    [DataRow("vector_distance('bogus', @v, @v)", 42201, 2, "The requested distance metric 'bogus' is not supported by vector_distance. Provide a valid distance metric.")]
    [DataRow("vector_distance(' cosine', @v, @v)", 42201, 2, "The requested distance metric ' cosine' is not supported by vector_distance. Provide a valid distance metric.")]
    [DataRow("vector_norm(@v, 'dot')", 42210, 1, "The requested norm function 'dot' is not supported by vector_norm/vector_normalize. Please provide a valid norm function.")]
    [DataRow("vector_distance('cosine', @v, @w)", 42204, 3, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("vector_distance('cosine', '[1,2]', '[3,4]')", 8116, 1, "Argument data type varchar is invalid for argument 2 of vector_distance function.")]
    [DataRow("vector_distance(1, @v, @v)", 8116, 1, "Argument data type int is invalid for argument 1 of vector_distance function.")]
    [DataRow("vector_norm('[1,2]', 'norm2')", 8116, 1, "Argument data type varchar is invalid for argument 1 of vector_norm function.")]
    [DataRow("vector_norm(@v, 5)", 8116, 1, "Argument data type int is invalid for argument 2 of vector_norm function.")]
    [DataRow("vectorproperty(null, 'Dimensions')", 8116, 36, "Argument data type varbinary is invalid for argument 1 of vectorproperty function.")]
    [DataRow("vector_distance('dot', @big, @big)", 8115, 2, "Arithmetic overflow error converting expression to data type float.")]
    [DataRow("vector_distance('euclidean', @big, @v)", 8115, 2, "Arithmetic overflow error converting expression to data type float.")]
    [DataRow("vector_distance('cosine', @v, @v, 1)", 174, 6, "The vector_distance function requires 3 argument(s).")]
    [DataRow("vector_distance()", 189, 1, "The vector_distance function requires 3 to 4 arguments.")]
    [DataRow("vector_norm(@v)", 174, 1, "The vector_norm function requires 2 argument(s).")]
    [DataRow("vectorproperty(@v)", 174, 1, "The vectorproperty function requires 2 argument(s).")]
    public void Functions_Refusals(string call, int number, int state, string message) =>
        AssertSqlError($"declare @v vector(2) = '[1,2]', @w vector(3) = '[1,2,3]', @big vector(2) = '[3e38,-3e38]'; select {call}", number, (byte)state, message);

    [TestMethod]
    public void Functions_TwoArgumentDistanceReportsBothArityErrors()
    {
        var ex = new Simulation().AssertSqlError("declare @v vector(2) = '[1,2]'; select vector_distance('cosine', @v)", 174);
        AreEqual(189, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("f1", "vector_norm(@v, 'norm2')", "float", 0)]
    [DataRow("f2", "vectorproperty(@v, 'Dimensions')", "sql_variant", 0)]
    [DataRow("f3", "cast(@v as varchar(100))", "varchar(100)", 1)]
    public void Functions_Determinism(string name, string body, string returns, int deterministic)
    {
        var sim = new Simulation();
        sim.ExecuteBatches($"create function dbo.{name}(@v vector(2)) returns {returns} with schemabinding as begin return {body}; end");
        AreEqual(deterministic, sim.ExecuteScalar($"select objectproperty(object_id('dbo.{name}'), 'IsDeterministic')"));
    }

    [TestMethod]
    public void Catalog_SysTypes()
    {
        using var reader = new Simulation().ExecuteReader("select system_type_id, user_type_id, max_length, precision, scale, is_nullable, is_user_defined, schema_id from sys.types where name = 'vector'");
        IsTrue(reader.Read());
        AreEqual("165|255|8000|0|0|True|False|4", string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
    }

    [TestMethod]
    [DataRow("select type_name(255)", "vector")]
    [DataRow("select type_name(165)", "varbinary")]
    [DataRow("select type_id('sys.vector')", 255)]
    [DataRow("select type_id('vector')", null)]
    [DataRow("select typeproperty('vector', 'Precision')", null)]
    [DataRow("select columnproperty(object_id('t'), 'v', 'Precision')", 28)]
    [DataRow("select columnproperty(object_id('t'), 'v', 'Scale')", null)]
    [DataRow("select columnproperty(object_id('t'), 'v', 'UsesAnsiTrim')", 1)]
    [DataRow("select columnproperty(object_id('t'), 'v', 'IsIndexable')", 0)]
    [DataRow("select col_length('t', 'v')", (short)28)]
    [DataRow("select data_type + '|' + cast(character_maximum_length as varchar) + '|' + cast(character_octet_length as varchar) from information_schema.columns where table_name = 't' and column_name = 'v' and numeric_precision is null", "vector|28|28")]
    [DataRow("select cast(system_type_id as varchar) + '|' + cast(user_type_id as varchar) + '|' + cast(max_length as varchar) + '|' + cast(vector_dimensions as varchar) + '|' + cast(vector_base_type as varchar) + '|' + vector_base_type_desc from sys.columns where object_id = object_id('t') and name = 'v'", "165|255|28|5|0|float32")]
    [DataRow("select count(*) from sys.all_columns where object_id = object_id('t') and vector_dimensions is null and vector_base_type is null and vector_base_type_desc is null", 1)]
    [DataRow("select is_ansi_padded from sys.columns where object_id = object_id('t') and name = 'v'", true)]
    public void Catalog_ColumnSurfaces(string query, object? expected) =>
        AreEqual(expected ?? DBNull.Value, new Simulation().ExecuteScalar($"create table t (id int, v vector(5)); {query}"));

    [TestMethod]
    public void Catalog_ParameterVectorColumns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p @v vector(3) as select 1");
        AreEqual("20|3|float32", sim.ExecuteScalar("select cast(max_length as varchar) + '|' + cast(vector_dimensions as varchar) + '|' + vector_base_type_desc from sys.parameters where object_id = object_id('p')"));
    }

    [TestMethod]
    public void Catalog_SpHelpColumnRow()
    {
        using var reader = new Simulation().ExecuteBatchesReader("create table t (v vector(5))", "exec sp_help 't'");
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("v|vector|no|28|     |     |yes|no|yes|", string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i))));
    }

    [TestMethod]
    public void Catalog_SpColumnsListsNoVectorColumn() =>
        AreEqual(1, new Simulation().ExecuteBatchesReader("create table t (id int, v vector(3))", "exec sp_columns 't'").EnumerateRecords().Count());

    [TestMethod]
    public void Catalog_DescribeFirstResultSet()
    {
        using var reader = new Simulation().ExecuteBatchesReader(
            "create table t (v vector(3), w vector(1998))",
            """
            select name + '|' + system_type_name + '|' + cast(max_length as varchar) + '|' + isnull(cast(user_type_id as varchar), '-')
            from sys.dm_exec_describe_first_result_set(N'select v, w, vector_normalize(v, ''norm2'') n, coalesce(v, ''[1,2,3]'') c, vectorproperty(v, ''Dimensions'') p, vector_distance(''dot'', v, v) d from t', null, 0)
            """);
        string[] expected = ["v|vector(3)|20|255", "w|vector(1998)|8000|255", "n|vector(3)|20|255", "c|varchar(20)|20|-", "p|sql_variant|8016|-", "d|float|8|-"];
        CollectionAssert.AreEqual(expected, reader.EnumerateRecords().Select(r => r.GetString(0)).ToArray());
    }

    [TestMethod]
    public void Catalog_DescribeNamesTheVectorTdsType()
    {
        using var reader = new Simulation().ExecuteReader("exec sp_describe_first_result_set N'declare @v vector(3); select @v'");
        IsTrue(reader.Read());
        AreEqual(245, reader.GetInt32(reader.GetOrdinal("tds_type_id")));
        AreEqual(20, reader.GetInt32(reader.GetOrdinal("tds_length")));
    }

    [TestMethod]
    public void Reader_SurfacesTheTextForm()
    {
        using var reader = new Simulation().ExecuteReader("declare @v vector(2) = '[1,2]'; select @v");
        AreEqual("vector", reader.GetDataTypeName(0));
        AreEqual(typeof(string), reader.GetFieldType(0));
        IsTrue(reader.Read());
        AreEqual("[1.0000000e+000,2.0000000e+000]", reader.GetValue(0));
        AreEqual("[1.0000000e+000,2.0000000e+000]", reader.GetString(0));
    }

    [TestMethod]
    public void Reader_TextParameterBindsToVectorColumn()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (v vector(2))").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (@p)", ("@p", "[5,6]")).ExecuteNonQuery();
        AreEqual("[5.0000000e+000,6.0000000e+000]", connection.CreateCommand("select v from t").ExecuteScalar());
    }
}
