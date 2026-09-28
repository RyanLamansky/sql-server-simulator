using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ClrUserDefinedTypeTests
{
    private static Simulation Types(params ReadOnlySpan<string> batches) => ClrFrameworkFixture.Simulation([
        "create type dbo.Point external name simclr.Point",
        "create type dbo.Unordered external name simclr.Unordered",
        "create type dbo.Label external name simclr.Label",
        .. batches]);

    private static string Hex(object? value) => Convert.ToHexString((byte[])value!);

    [TestMethod]
    [Description("CREATE TYPE … EXTERNAL NAME registers the class as a type the catalog reports as an assembly type.")]
    public void CreateType_CatalogRows()
    {
        var sim = Types();
        AreEqual("240|257|9|1|1|1", sim.ExecuteScalar(
            "select concat_ws('|', system_type_id, user_type_id, max_length, is_nullable, is_user_defined, is_assembly_type) from sys.types where name = 'Point'"));
        AreEqual("Point|1|1|Point, simclr, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", sim.ExecuteScalar(
            "select concat_ws('|', assembly_class, is_binary_ordered, is_fixed_length, assembly_qualified_name) from sys.assembly_types where name = 'Point'"));
        AreEqual("Label|0|0|20", sim.ExecuteScalar(
            "select concat_ws('|', assembly_class, is_binary_ordered, is_fixed_length, max_length) from sys.assembly_types where name = 'Label'"));
        AreEqual("257|Point", sim.ExecuteScalar("select concat_ws('|', type_id('dbo.Point'), type_name(type_id('Point')))"));
        AreEqual(65536, sim.ExecuteScalar("select assembly_id from sys.type_assembly_usages where user_type_id = type_id('Point')"));
        AreEqual(9, sim.ExecuteScalar("select typeproperty('Point', 'Precision')"));
    }

    [TestMethod]
    [Description("Format.Native serializes each field in declaration order, byte-ordered, as real's CAST(… AS varbinary) shows.")]
    [DataRow("1,2", "800000018000000200")]
    [DataRow("-1,-2", "7FFFFFFF7FFFFFFE00")]
    [DataRow("2147483647,-2147483648", "FFFFFFFF0000000000")]
    public void Native_Point_Bytes(string text, string hex)
        => AreEqual(hex, Hex(Types().ExecuteScalar($"declare @p Point = '{text}'; select cast(@p as varbinary(100))")));

    [TestMethod]
    [Description("Every primitive field kind: signed integers and floats flip the sign bit (floats invert when negative), unsigned ones are plain big-endian.")]
    [DataRow("1|2|3|4|True|1.5|2.5|-1|5|6|7", "01800280000003800000000000000401BFC00000C0040000000000007F000500000006000000000000000700")]
    [DataRow("255|-2|-3|-4|False|-1.5|-2.5|-128|65535|4294967295|18446744073709551615", "FF7FFE7FFFFFFD7FFFFFFFFFFFFFFC00403FFFFF3FFBFFFFFFFFFFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFF00")]
    public void Native_PrimitiveFields_Bytes(string text, string hex)
        => AreEqual(hex, Hex(ClrFrameworkFixture.Simulation("create type dbo.Wide external name simclr.Wide")
            .ExecuteScalar($"declare @w Wide = '{text}'; select cast(@w as varbinary(200))")));

    [TestMethod]
    [Description("A SqlTypes field carries its not-null byte, a SqlBoolean one byte, and a nested struct its own fields in place.")]
    [DataRow("x", "0180000001" + "01C004000000000000" + "02" + "800000057FFD" + "01800000000001E848" + "018000AB3680328F5C" + "0107" + "017FFE" + "018000000000000009" + "01BFC00000" + "00")]
    [DataRow("nulls", "0080000000" + "008000000000000000" + "00" + "800000008000" + "008000000000000000" + "008000000080000000" + "0000" + "008000" + "008000000000000000" + "0080000000" + "00")]
    public void Native_SqlTypeAndNestedFields_Bytes(string text, string hex)
    {
        var sim = ClrFrameworkFixture.Simulation("create type dbo.SqlFields external name simclr.SqlFields");
        AreEqual(hex, Hex(sim.ExecuteScalar($"declare @s SqlFields = '{text}'; select cast(@s as varbinary(200))")));
        AreEqual((short)(hex.Length / 2), sim.ExecuteScalar("select max_length from sys.types where name = 'SqlFields'"));
    }

    [TestMethod]
    [Description("Format.UserDefined stores what IBinarySerialize.Write writes, bounded by MaxByteSize.")]
    public void UserDefined_Label()
    {
        var sim = Types();
        AreEqual("0568656C6C6F|6|5|HELLO", sim.ExecuteScalar(
            "declare @l Label = 'hello'; select concat_ws('|', convert(varchar(20), cast(@l as varbinary(50)), 2), datalength(@l), @l.Length, @l.Upper())"));
        var ex = sim.AssertSqlError("declare @l Label = 'this string is quite long indeed'", 6522);
        AreEqual(2, ex.State);
        Contains("System.Data.SqlTypes.SqlTypeException: The buffer is insufficient. Read or write operation failed.", ex.Message);
        Contains("at Label.Write(BinaryWriter w)", ex.Message);
    }

    [TestMethod]
    [Description("An unlimited MaxByteSize is max_length -1, and ToString() is nvarchar(4000): longer is the server's truncation error at state 1.")]
    public void UserDefined_Unlimited_ToStringTruncates()
    {
        var sim = ClrFrameworkFixture.Simulation("create type dbo.BigBlob external name simclr.BigBlob");
        AreEqual((short)-1, sim.ExecuteScalar("select max_length from sys.types where name = 'BigBlob'"));
        AreEqual(9002, sim.ExecuteScalar("declare @b BigBlob = replicate(cast('x' as varchar(max)), 9000); select datalength(@b)"));
        var ex = sim.AssertSqlError("declare @b BigBlob = replicate(cast('x' as varchar(max)), 9000); select len(@b.ToString())", 6522);
        AreEqual(1, ex.State);
        Contains("TruncationException", ex.Message);
    }

    [TestMethod]
    [Description("Properties, instance and static methods, and a static property through ::, typed as the CLR members map.")]
    public void Members_ReadAndTyped()
    {
        var sim = Types();
        AreEqual("3|4|5|3,4|1,2|points|points|5|10", sim.ExecuteScalar("""
            declare @p Point = '3,4'
            select concat_ws('|', @p.X, @p.Y, @p.Distance(), @p.ToString(), Point::Make(1, 2).ToString(),
                Point::Describe(), dbo.Point::Describe(), Point::Parse('5,6').X, @p.DistanceTo(Point::Make(-3, -4)))
            """));
        AreEqual("x|int|4;ts|nvarchar|8000;d|float|8;mk|Point|9", sim.ExecuteScalar("""
            declare @p Point = '3,4'
            select @p.X as x, @p.ToString() as ts, @p.Distance() as d, Point::Make(1, 2) as mk into t2
            select string_agg(concat_ws('|', name, type_name(user_type_id), max_length), ';') within group (order by column_id) from sys.columns where object_id = object_id('t2')
            """));
    }

    [TestMethod]
    [Description("A NULL receiver reads NULL without calling in; a NULL argument of the type passes its Null instance.")]
    public void Members_NullReceiverAndArgument()
    {
        var sim = Types();
        AreEqual(1, sim.ExecuteScalar("declare @n Point; select case when @n.X is null and @n.ToString() is null and @n.Distance() is null then 1 end"));
        AreEqual(1, sim.ExecuteScalar("select case when Point::[Null] is null then 1 end"));
        AreEqual(0d, sim.ExecuteScalar("declare @p Point = '0,0'; select @p.DistanceTo(null)"));
    }

    [TestMethod]
    [Description("Member binding errors raise at compile time as real's do.")]
    [DataRow("declare @p Point = '1,2'; select @p.Nope()", 6506)]
    [DataRow("select Point::Nope()", 6506)]
    [DataRow("declare @p Point = '1,2'; select @p.Nope", 6592)]
    [DataRow("declare @p Point = '1,2'; select @p.Distance", 6592)]
    [DataRow("select Point::X", 6584)]
    [DataRow("select Point::Make(1)", 174)]
    [DataRow("declare @p Point = '1,2'; select @p.Scale(2)", 6200)]
    [DataRow("declare @p Point = '1,2'; select @p.X.ToString()", 258)]
    [DataRow("select nosuch::Make(1, 2)", 243)]
    public void Members_BindingErrors(string sql, int number) => _ = Types().AssertSqlError(sql, number);

    [TestMethod]
    [Description("A throw inside a member is Msg 6522 state 2 naming the type.")]
    public void Members_Throw_IsMsg6522()
    {
        var ex = Types().AssertSqlError("declare @p Point = '1,2'; select @p.Boom()", 6522);
        AreEqual(2, ex.State);
        Contains("routine or aggregate \"Point\"", ex.Message);
        Contains("System.InvalidOperationException: point boom", ex.Message);
        Contains("at Point.Boom()", ex.Message);
    }

    [TestMethod]
    [Description("A string reaches the type through Parse and leaves through ToString; a binary is its serialized bytes.")]
    public void Conversions_Legal()
    {
        var sim = Types();
        AreEqual("1,2|1,2|1,2       |4", sim.ExecuteScalar(
            "declare @p Point = N'1,2'; select concat_ws('|', cast(@p as nvarchar(50)), convert(varchar(20), @p), cast(@p as nchar(10)), convert(Point, '3,4').Y)"));
        AreEqual("-1,0", sim.ExecuteScalar("select cast(cast(0x7FFFFFFF8000000000 as Point) as nvarchar(40))"));
        AreEqual(1, sim.ExecuteScalar("select case when try_cast('zz' as Point) is null and try_convert(Point, 'zz') is null then 1 end"));
        AreEqual(2, sim.ExecuteScalar("select cast(cast('<Point><X>1</X><Y>2</Y></Point>' as xml) as Point).Y"));
        Contains("<X>1</X><Y>2</Y>", (string)sim.ExecuteScalar("select cast(cast(cast('1,2' as Point) as xml) as nvarchar(max))")!);
    }

    [TestMethod]
    [Description("The conversions and operators real refuses, with its naming: explicit errors take the three-part name, a clash between two CLR types the two-part names.")]
    [DataRow("declare @p Point = '1,2'; select cast(@p as int)", 529, "Explicit conversion from data type simulated.dbo.Point to int is not allowed.")]
    [DataRow("select cast(1 as Point)", 529, "Explicit conversion from data type int to simulated.dbo.Point is not allowed.")]
    [DataRow("declare @p Point = '1,2'; declare @s nvarchar(20) = @p", 257, "Implicit conversion from data type Point to nvarchar is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("declare @p Point = '1,2', @u Unordered = '1'; select case when @p = @u then 1 end", 206, "Operand type clash: dbo.Unordered is incompatible with dbo.Point")]
    [DataRow("declare @p Point = '1,2'; declare @h hierarchyid = @p", 206, "Operand type clash: Point is incompatible with hierarchyid")]
    [DataRow("declare @p Point = '1,2'; select @p + 1", 403, "Invalid operator for data type. Operator equals add, type equals Point.")]
    [DataRow("declare @p Point = '1,2'; select concat(@p, 'x')", 257, "Implicit conversion from data type Point to varchar is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("select cast(cast('1,2' as Point) as binary(12))", 6207, "Error converting dbo.Point to fixed length binary type. The result would be padded and cannot be converted back.")]
    [DataRow("declare @p Point = cast(0x01 as varbinary(5))", 6235, "Data serialization error. Length (1) is less than fixed length (9) for type 'Point'.")]
    public void Conversions_Refused(string sql, int number, string message) => Types().AssertSqlError(sql, number, message);

    [TestMethod]
    [Description("A byte-ordered type's column compares, sorts, groups and keys by its bytes.")]
    public void Column_ByteOrdered()
    {
        var sim = Types("""
            create table t (id int primary key, p Point, q dbo.Point not null default ('9,9'))
            insert t (id, p) values (1, '1,2'), (2, null), (3, '-5,5')
            insert t (id, p, q) values (4, Point::Make(7, 8), '1,1')
            """);
        AreEqual("1,2|9,9|1|8", sim.ExecuteScalar("select concat_ws('|', p.ToString(), q.ToString(), t.p.X, (select p.Y from t where id = 4)) from t where p = '1,2'"));
        AreEqual("2,3,1,4", sim.ExecuteScalar("select string_agg(id, ',') within group (order by p) from t"));
        AreEqual("7,8", sim.ExecuteScalar("select max(p).ToString() from t"));
        AreEqual("240|257|9", sim.ExecuteScalar("select concat_ws('|', system_type_id, user_type_id, max_length) from sys.columns where object_id = object_id('t') and name = 'p'"));
        AreEqual("Point|9|Point", sim.ExecuteScalar("select concat_ws('|', data_type, character_maximum_length, domain_name) from information_schema.columns where table_name = 't' and column_name = 'p'"));
        var ex = sim.AssertSqlError("create table k (p Point primary key); insert k values ('1,1'), ('1,1')", 2627);
        Contains("The duplicate key value is (0x800000018000000100).", ex.Message);
    }

    [TestMethod]
    [Description("A type not marked IsByteOrdered can't be compared, sorted, grouped, made DISTINCT or keyed.")]
    [DataRow("select * from u where a = cast('1' as Unordered)", 403)]
    [DataRow("select * from u order by a", 249)]
    [DataRow("select count(*) from u group by a", 249)]
    [DataRow("select distinct a from u", 421)]
    [DataRow("create index ix on u(a)", 1978)]
    [DataRow("create table pk (u Unordered primary key)", 1919)]
    public void Column_Unordered_Refused(string sql, int number)
        => _ = Types("create table u (a Unordered); insert u values ('1'), ('2')").AssertSqlError(sql, number);

    [TestMethod]
    [Description("SET and UPDATE assign a property or call a mutator, storing the changed value.")]
    public void Mutators()
    {
        var sim = Types();
        AreEqual("30,6", sim.ExecuteScalar("declare @p Point = '1,2'; set @p.X = 10; set @p.Scale(3); select @p.ToString()"));
        _ = sim.ExecuteNonQuery("create table t (id int, p Point); insert t values (1, '1,1'), (2, null)");
        _ = sim.ExecuteNonQuery("update t set p.Y = 5 where id = 1; update t set p.Scale(2) where id = 1");
        AreEqual("2,10", sim.ExecuteScalar("select p.ToString() from t where id = 1"));
        sim.AssertSqlError("update t set p.X = 1", 5302, "Mutator 'X' on 'p' cannot be called on a null value.");
        sim.AssertSqlError("declare @p Point; set @p.X = 1", 5302, "Mutator 'X' on '@p' cannot be called on a null value.");
        _ = sim.AssertSqlError("declare @p Point = '1,2'; set @p.Distance()", 6201);
        _ = sim.AssertSqlError("declare @p Point = '1,2'; set @p.Nope = 1", 6592);
    }

    [TestMethod]
    [Description("A class that doesn't conform to the UDT specification is refused as real refuses it.")]
    [DataRow("UdtNoAttr", 6255)]
    [DataRow("UdtNativeRef", 6225)]
    [DataRow("UdtNativeDecimal", 6222)]
    [DataRow("UdtNativeClass", 6229)]
    [DataRow("UdtNoSerialize", 6226)]
    [DataRow("UdtNoMax", 6244)]
    [DataRow("UdtNotNullable", 6577)]
    [DataRow("UdtNoNull", 6557)]
    [DataRow("UdtNoParse", 6558)]
    [DataRow("Missing", 6556)]
    public void CreateType_NonConforming(string className, int number)
        => _ = ClrFrameworkFixture.Simulation().AssertSqlError($"create type a external name simclr.{className}", number);

    [TestMethod]
    [Description("The name and mapping collisions, a missing assembly and a method segment.")]
    public void CreateType_Collisions()
    {
        var sim = Types();
        _ = sim.AssertSqlError("create type Point external name simclr.Point", 219);
        _ = sim.AssertSqlError("create type Point2 external name simclr.Point", 8188);
        _ = sim.AssertSqlError("create type a external name nosuch.Point", 6267);
        _ = sim.AssertSqlError("create type a external name simclr.Point.Parse", 102);
    }

    [TestMethod]
    [Description("DROP TYPE refuses while a table or module uses the type, and DROP ASSEMBLY while a type maps its class.")]
    public void Drop_Dependencies()
    {
        var sim = Types("create table t (p Point)", "create procedure pr @p Point as select @p.ToString()");
        _ = sim.AssertSqlError("drop assembly simclr", 6598);
        _ = sim.AssertSqlError("drop type Point", 3732);
        _ = sim.ExecuteNonQuery("drop table t; drop procedure pr; drop type Point");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.types where name = 'Point'"));
    }

    [TestMethod]
    [Description("CLR and T-SQL routines take and return the type.")]
    public void Routines_TakeAndReturnType()
    {
        var sim = Types(
            "create function dbo.PointSum(@p Point) returns int as external name simclr.UdtFuncs.PointSum",
            "create function dbo.MakePoint(@x int, @y int) returns Point as external name simclr.UdtFuncs.MakePoint",
            "create function dbo.tsql(@p Point) returns nvarchar(40) as begin return @p.ToString() + '!' end",
            "create procedure pr @p Point, @o Point output as begin set @o = @p; set @o.X = 100 end");
        AreEqual("7|5,6|1,1!", sim.ExecuteScalar("select concat_ws('|', dbo.PointSum('3,4'), dbo.MakePoint(5, 6).ToString(), dbo.tsql('1,1'), dbo.PointSum(null))"));
        AreEqual("100,3", sim.ExecuteScalar("declare @o Point; exec pr '2,3', @o output; select @o.ToString()"));
        AreEqual("240|257|9", sim.ExecuteScalar("select concat_ws('|', system_type_id, user_type_id, max_length) from sys.parameters where object_id = object_id('pr') and name = '@o'"));
        AreEqual("5|6", sim.ExecuteScalar("create table #r (v nvarchar(10)); insert #r exec sp_executesql N'select concat_ws(''|'', @p.X, @p.Y)', N'@p Point', '5,6'; select v from #r"));
    }

    [TestMethod]
    [Description("A table variable takes the type; a temp table can't, the type living in the user database.")]
    public void TableVariableAndTempTable()
    {
        var sim = Types();
        AreEqual("1,2", sim.ExecuteScalar("declare @t table (p Point); insert @t values ('1,2'); select p.ToString() from @t"));
        _ = sim.AssertSqlError("create table #t (p Point)", 2715);
    }

    [TestMethod]
    [Description("A computed column, a CHECK, a view and a join read the type's members and bytes.")]
    public void ComputedCheckViewJoin()
    {
        var sim = Types("create table c (p Point, x as p.X, check (p.Y > 0))");
        AreEqual(1, sim.ExecuteScalar("insert c values ('1,2'); select x from c"));
        _ = sim.AssertSqlError("insert c values ('1,-2')", 547);
        sim.ExecuteBatches(
            "create table j1 (p Point); create table j2 (p Point); insert j1 values ('1,1'), ('2,2'); insert j2 values ('2,2'), ('3,3')",
            "create view v as select p.X as px, p from j1");
        AreEqual("2,2", sim.ExecuteScalar("select j1.p.ToString() from j1 join j2 on j1.p = j2.p"));
        AreEqual("int|Point", sim.ExecuteScalar("select string_agg(type_name(user_type_id), '|') within group (order by column_id) from sys.columns where object_id = object_id('v')"));
    }

    [TestMethod]
    [Description("A client reads the serialized bytes, the type's own name, and sp_help describes the type.")]
    public void ClientAndHelpSurfaces()
    {
        var sim = Types();
        using (var reader = sim.ExecuteReader("select cast('1,2' as Point)"))
        {
            IsTrue(reader.Read());
            AreEqual("800000018000000200", Hex(reader.GetValue(0)));
            AreEqual("Point", reader.GetDataTypeName(0));
        }

        using var help = sim.ExecuteReader("exec sp_help 'Point'");
        IsTrue(help.Read());
        AreEqual("Point|True|9|9", $"{help["Type_name"]}|{help["Storage_type"] is DBNull}|{help["Length"]}|{help["Prec"]}");
    }
}
