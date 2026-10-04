using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Dynamic Data Masking: the <c>MASKED WITH</c> DDL and its refusals, the
/// catalog, the <c>UNMASK</c> permission at every scope, and what a principal
/// without it reads and writes. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DataMaskingTests
{
    private const string MaskedTable = """
        create table t (
            id int,
            s varchar(20) masked with (function = 'default()'),
            e varchar(40) masked with (function = 'email()'),
            p varchar(30) masked with (function = 'partial(2, "-XX-", 1)'),
            i int masked with (function = 'default()'),
            d datetime masked with (function = 'default()'),
            dt datetime2(3) masked with (function = 'datetime("M")'),
            plain varchar(10));
        insert t values (1, 'hello', 'john@x.com', 'abcdefghij', 42, '2020-05-06 07:08:09', '2021-07-15 13:14:15.678', 'p1'),
                        (2, 'ab', '', 'ab', 7, '1999-01-01', '2020-02-29 01:02:03', 'p2');
        insert t (id) values (3);
        create user u without login;
        grant select on t to u;
        """;

    private static Simulation Seeded(params ReadOnlySpan<string> more)
    {
        var sim = new Simulation();
        sim.ExecuteBatches([MaskedTable, .. more]);
        return sim;
    }

    private static object? AsUser(Simulation sim, string query) => sim.ExecuteScalar($"execute as user = 'u'; {query}");

    [TestMethod]
    [DataRow("select s from t where id = 1", "xxxx")]
    [DataRow("select e from t where id = 1", "jXXX@XXXX.com")]
    [DataRow("select e from t where id = 2", "xxxx")]
    [DataRow("select p from t where id = 1", "ab-XX-j")]
    [DataRow("select p from t where id = 2", "-XX-")]
    [DataRow("select i from t where id = 1", 0)]
    [DataRow("select plain from t where id = 1", "p1")]
    [DataRow("select convert(varchar(30), d, 120) from t where id = 1", "xxxx")]
    [DataRow("select convert(varchar(30), dt, 121) from t where id = 1", "xxxx")]
    [DataRow("select year(d) from t where id = 1", 0)]
    public void Read_MasksEachFunction(string query, object expected) => AreEqual(expected, AsUser(Seeded(), query));

    [TestMethod]
    public void Read_DefaultDateAndDatePart()
    {
        using var reader = Seeded().ExecuteReader("execute as user = 'u'; select d, dt from t where id = 1");
        IsTrue(reader.Read());
        AreEqual(new DateTime(1900, 1, 1), reader.GetDateTime(0));
        AreEqual(new DateTime(2021, 1, 15, 13, 14, 15), reader.GetDateTime(1));
    }

    [TestMethod]
    public void Read_NullStaysNull() => IsInstanceOfType<DBNull>(AsUser(Seeded(), "select s from t where id = 3"));

    [TestMethod]
    [DataRow("select s + 'x' from t where id = 1", "xxxx")]
    [DataRow("select left(s, 2) from t where id = 1", "xx")]
    [DataRow("select len(s) from t where id = 1", 0)]
    [DataRow("select i + 1 from t where id = 1", 0)]
    [DataRow("select upper(e) from t where id = 1", "xxxx")]
    [DataRow("select isnull(s, 'q') from t where id = 3", "xxxx")]
    [DataRow("select max(s) from t", "xxxx")]
    [DataRow("select sum(i) from t", 0)]
    [DataRow("select count(s) from t", 0)]
    [DataRow("select e collate Latin1_General_BIN from t where id = 1", "xxxx")]
    [DataRow("select cast(e as varchar(10)) from t where id = 1", "xxxx")]
    public void Expression_MasksAsDefaultOfItsType(string query, object expected) => AreEqual(expected, AsUser(Seeded(), query));

    [TestMethod]
    [DataRow("select cast(e as varchar(40)) from t where id = 1")]
    [DataRow("select convert(varchar(40), e) from t where id = 1")]
    [DataRow("select (e) from t where id = 1")]
    [DataRow("select nullif(e, 'q') from t where id = 1")]
    [DataRow("select x.e from (select e from t) x where x.e like 'john%'")]
    [DataRow("with c as (select e, id from t) select e from c where id = 1")]
    [DataRow("select (select e from t where id = 1)")]
    [DataRow("select case when id = 1 then e else 'nobody' end from t where id = 1")]
    public void BareReference_KeepsColumnFunction(string query) => AreEqual("jXXX@XXXX.com", AsUser(Seeded(), query));

    [TestMethod]
    public void UnmaskedArm_TakesTheMaskedArmsFunction() =>
        AreEqual("nXXX@XXXX.com", AsUser(Seeded(), "select case when id = 1 then e else 'nobody' end from t where id = 2"));

    [TestMethod]
    [DataRow("select case when id = 1 then p else e end from t where id = 1", "xxxx")]
    [DataRow("select coalesce(e, s) from t where id = 1", "xxxx")]
    [DataRow("select case s when 'hello' then 'h' end from t where id = 1", "h")]
    [DataRow("select case when s = 'hello' then 'yes' else 'no' end from t where id = 1", "yes")]
    [DataRow("select count(*) from t where s = 'hello'", 1)]
    [DataRow("select top 1 id from t order by s desc", 1)]
    public void Arms_PredicatesAndOrdering(string query, object expected) => AreEqual(expected, AsUser(Seeded(), query));

    [TestMethod]
    public void SetOperation_CarriesTheMaskToEveryRow()
    {
        var sim = Seeded();
        AreEqual("pXXX@XXXX.com", AsUser(sim, "select top 1 x from (select plain as x from t where id = 1 union all select e from t where id = 1) u order by x desc"));
        AreEqual("xxxx", AsUser(sim, "select top 1 x from (select p as x from t where id = 1 union all select e from t where id = 1) u"));
    }

    [TestMethod]
    public void ForJson_SerializesMaskedValues()
    {
        var sim = Seeded();
        AreEqual("[{\"id\":1,\"s\":\"xxxx\"}]", AsUser(sim, "select id, s from t where id = 1 for json path"));
        AreEqual("xxxx", AsUser(sim, "select (select s from t where id = 1 for json path)"));
    }

    [TestMethod]
    public void View_MasksThroughItsBaseTable()
    {
        var sim = Seeded("create view v as select id, e, e + '' as e2 from t", "grant select on v to u");
        AreEqual("jXXX@XXXX.com", AsUser(sim, "select e from v where id = 1"));
        AreEqual("xxxx", AsUser(sim, "select e2 from v where id = 1"));
    }

    [TestMethod]
    public void InlineFunctionAndPivot_PassTheMaskThrough()
    {
        var sim = Seeded("create function f() returns table as return select id, e from t", "grant select on f to u");
        AreEqual("jXXX@XXXX.com", AsUser(sim, "select e from f() where id = 1"));
        AreEqual("jXXX@XXXX.com", AsUser(sim, "select p.e from (select e, id from t where id = 1) src pivot (max(id) for id in ([1])) p"));
        const string Unpivot = "(select e, cast(plain as varchar(40)) as q from t where id = 1) src unpivot (v for c in (e, q)) x where c = 'q'";
        AreEqual("xxxx", AsUser(sim, $"select c from {Unpivot}"));
        AreEqual("xxxx", AsUser(sim, $"select v from {Unpivot}"));
    }

    [TestMethod]
    public void Procedure_OwnershipChainDoesNotUnmask()
    {
        var sim = Seeded("create procedure pr as select s from t where id = 1", "grant execute on pr to u");
        AreEqual("xxxx", sim.ExecuteScalar("execute as user = 'u'; exec pr"));
    }

    [TestMethod]
    public void Dbo_ReadsRealValues() => AreEqual("hello", Seeded().ExecuteScalar("select s from t where id = 1"));

    [TestMethod]
    [DataRow("grant unmask to u", "hello", "john@x.com")]
    [DataRow("grant unmask on schema::dbo to u", "hello", "john@x.com")]
    [DataRow("grant unmask on t to u", "hello", "john@x.com")]
    [DataRow("grant unmask on t(s) to u", "hello", "jXXX@XXXX.com")]
    [DataRow("grant unmask to u; deny unmask on t(e) to u", "hello", "jXXX@XXXX.com")]
    [DataRow("grant unmask to u; revoke unmask to u", "xxxx", "jXXX@XXXX.com")]
    [DataRow("create role r; alter role r add member u; grant unmask to r", "hello", "john@x.com")]
    [DataRow("alter role db_owner add member u", "hello", "john@x.com")]
    [DataRow("grant control on t to u", "hello", "john@x.com")]
    public void Unmask_AtEachScope(string grants, string s, string e)
    {
        var sim = Seeded(grants);
        AreEqual(s, AsUser(sim, "select s from t where id = 1"));
        AreEqual(e, AsUser(sim, "select e from t where id = 1"));
    }

    [TestMethod]
    public void Unmask_ExpressionNeedsEveryColumnUnmasked()
    {
        var sim = Seeded("grant unmask on t(s) to u");
        AreEqual("hello", AsUser(sim, "select s + '' from t where id = 1"));
        AreEqual("xxxx", AsUser(sim, "select s + e from t where id = 1"));
    }

    [TestMethod]
    public void Unmask_Catalog()
    {
        var sim = Seeded("grant unmask on t(e) to u with grant option");
        AreEqual("1|1|3|UMSK|UNMASK|W", sim.ExecuteScalar(
            "select concat(class, '|', major_id - object_id('t') + 1, '|', minor_id, '|', type, '|', permission_name, '|', state) from sys.database_permissions where permission_name = 'UNMASK'"));
    }

    [TestMethod]
    public void Unmask_OnView_Raises4606() => _ = Seeded("create view v as select s from t").AssertSqlError("grant unmask on v to u", 4606);

    [TestMethod]
    public void HasPermsByName_Unmask() =>
        AreEqual(0, AsUser(Seeded(), "select has_perms_by_name(db_name(), 'DATABASE', 'UNMASK')"));

    [TestMethod]
    public void SelectInto_CopiesMaskedValuesWithoutTheMask()
    {
        var sim = Seeded("grant create table to u", "grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; select id, s, e, i into x from t");
        AreEqual("xxxx|jXXX@XXXX.com|0", sim.ExecuteScalar("select concat(s, '|', e, '|', i) from x where id = 1"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.masked_columns where object_id = object_id('x')"));
    }

    [TestMethod]
    public void Writes_StoreWhatThePrincipalReads()
    {
        var sim = Seeded(
            "create table dst (id int, s varchar(20), e varchar(40))",
            "grant select, insert, update on dst to u",
            "grant update on t to u");
        _ = sim.ExecuteNonQuery("""
            execute as user = 'u';
            insert dst select id, s, e from t where id = 1;
            insert dst (id, e) values (2, (select e from t where id = 1));
            insert dst (id) values (3);
            update dst set e = t.e from t where t.id = 1 and dst.id = 3;
            update t set plain = left(s, 10) where id = 2;
            """);
        AreEqual("xxxx|jXXX@XXXX.com", sim.ExecuteScalar("select concat(s, '|', e) from dst where id = 1"));
        AreEqual("xxxx", sim.ExecuteScalar("select e from dst where id = 2"));
        AreEqual("jXXX@XXXX.com", sim.ExecuteScalar("select e from dst where id = 3"));
        AreEqual("xxxx", sim.ExecuteScalar("select plain from t where id = 2"));
    }

    [TestMethod]
    public void Output_ReadsMaskedValues()
    {
        var sim = Seeded("grant update on t to u");
        AreEqual("xxxx", sim.ExecuteScalar("execute as user = 'u'; update t set id = id output deleted.s where id = 1"));
    }

    [TestMethod]
    [DataRow("declare @v varchar(40); select @v = e from t where id = 1; select @v", "jXXX@XXXX.com")]
    [DataRow("declare @v varchar(20); select @v = e from t where id = 1; select @v", "xxxx")]
    [DataRow("declare @v varchar(40); set @v = (select e from t where id = 1); select @v", "jXXX@XXXX.com")]
    [DataRow("declare @v varchar(20) = (select top 1 i from t); select @v", "xxxx")]
    public void Assignment_MasksPerVariableType(string batch, string expected) => AreEqual(expected, AsUser(Seeded(), batch));

    [TestMethod]
    public void Cursor_FetchesMaskedValues() =>
        AreEqual("xxxx", AsUser(Seeded(), "declare c cursor for select s from t where id = 1; open c; fetch next from c"));

    [TestMethod]
    public void TableVariable_MasksForItsReader() =>
        AreEqual("xxxx", new Simulation().ExecuteScalar("create user u without login; execute as user = 'u'; declare @t table (a varchar(10) masked with (function = 'default()')); insert @t values ('abc'); select a from @t"));

    [TestMethod]
    public void TempTable_ReadsUnmaskedForItsCreator() =>
        AreEqual("abc", new Simulation().ExecuteScalar("create user u without login; execute as user = 'u'; create table #t (a varchar(10) masked with (function = 'default()')); insert #t values ('abc'); select a from #t"));

    [TestMethod]
    public void ComputedColumn_OverMaskedColumnReadsDefault()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table c (a varchar(10) masked with (function = 'email()'), b as a + '!', n as len(a) persisted)",
            "insert c (a) values ('abc')",
            "create user u without login",
            "grant select on c to u");
        AreEqual("aXXX@XXXX.", AsUser(sim, "select a from c"));
        AreEqual("xxxx", AsUser(sim, "select b from c"));
        AreEqual(0, AsUser(sim, "select n from c"));
    }

    [TestMethod]
    [DataRow("char(6)", "default()", "abc", "xxxx")]
    [DataRow("nchar(3)", "default()", "ab", "xxx")]
    [DataRow("varchar(1)", "default()", "a", "x")]
    [DataRow("varchar(5)", "email()", "abc", "aXXX@")]
    [DataRow("char(8)", "partial(1, \"-\", 1)", "ab", "a- ")]
    [DataRow("varchar(10)", "partial(2, \"*\", 2)", "abcd", "*")]
    [DataRow("varchar(10)", "partial(2, \"*\", 2)", "abcde", "ab*de")]
    [DataRow("varchar(5)", "partial(0, \"abcdefgh\", 0)", "q", "abcde")]
    [DataRow("nvarchar(10)", "partial(3, \"ÄÖ\", 0)", "", "ÄÖ")]
    [DataRow("varchar(max)", "default()", "long", "xxxx")]
    [DataRow("xml", "default()", "<a/>", "<masked />")]
    [DataRow("sql_variant", "default()", "ab", "xxxx")]
    public void StringMask(string type, string function, string value, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"create table m (v {type} masked with (function = '{function}'))",
            $"insert m values (N'{value}')",
            "create user u without login",
            "grant select on m to u");
        AreEqual(expected, AsUser(sim, "select v from m"));
    }

    [TestMethod]
    [DataRow("binary(4)", "0x01020304")]
    [DataRow("varbinary(max)", "0xABCDEF")]
    public void BinaryDefault_IsOneByte(string type, string value)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"create table m (v {type} masked with (function = 'default()'))",
            $"insert m values ({value})",
            "create user u without login",
            "grant select on m to u");
        CollectionAssert.AreEqual("0"u8.ToArray(), (byte[])AsUser(sim, "select v from m")!);
    }

    [TestMethod]
    [DataRow("datetime2(3)", "datetime(\"Y\")", "2000-07-15 13:14:15")]
    [DataRow("datetime2(3)", "datetime(\"D\")", "2021-07-01 13:14:15")]
    [DataRow("datetime2(3)", "datetime(\"h\")", "2021-07-15 00:14:15")]
    [DataRow("datetime2(3)", "datetime(\"m\")", "2021-07-15 13:00:15")]
    [DataRow("datetime2(3)", "datetime(\"s\")", "2021-07-15 13:14:00")]
    [DataRow("smalldatetime", "datetime(\"D\")", "2021-07-01 13:14:00")]
    public void DatetimeMask(string type, string function, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"create table m (v {type} masked with (function = '{function}'))",
            "insert m values ('2021-07-15 13:14:15.678')",
            "create user u without login",
            "grant select on m to u");
        var moment = (DateTime)AsUser(sim, "select v from m")!;
        AreEqual(expected, moment.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)[..19]);
        AreEqual(0, moment.Ticks % TimeSpan.TicksPerSecond);
    }

    [TestMethod]
    public void RandomMask_StaysInRange()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table m (a int masked with (function = 'random(5, 9)'), b decimal(6, 2) masked with (function = 'random(1.5, 2.5)'))",
            "insert m select top 50 100, 100 from sys.all_columns",
            "create user u without login",
            "grant select on m to u");
        using var reader = sim.ExecuteReader("execute as user = 'u'; select a, b from m");
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            IsTrue(reader.GetInt32(0) is >= 5 and <= 9);
            var b = reader.GetDecimal(1);
            IsTrue(b is >= 1.5m and <= 2.5m && decimal.Round(b, 2) == b);
        }
        AreEqual(50, rows);
    }

    [TestMethod]
    public void Catalog_MaskedColumns()
    {
        using var reader = Seeded().ExecuteReader(
            "select c.name, c.is_masked, m.masking_function from sys.columns c left join sys.masked_columns m on m.object_id = c.object_id and m.column_id = c.column_id where c.object_id = object_id('t') order by c.column_id");
        string[] expected = ["id|False|", "s|True|default()", "e|True|email()", "p|True|partial(2, \"-XX-\", 1)", "i|True|default()", "d|True|default()", "dt|True|datetime(\"M\")", "plain|False|"];
        foreach (var row in expected)
        {
            IsTrue(reader.Read());
            AreEqual(row, $"{reader.GetString(0)}|{reader.GetBoolean(1)}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
        }
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("varchar(20)", "partial(1, \"x\", 1)", "partial(1, \"x\", 1)")]
    [DataRow("varchar(20)", "PARTIAL( 01 , \"a\"\"b\" , 0 )", "partial(1, \"a\"\"b\", 0)")]
    [DataRow("varchar(20)", "EMAIL()", "email()")]
    [DataRow("varchar(20)", "default( )", "default()")]
    [DataRow("decimal(6, 2)", "random(1.555, 3)", "random(1.56, 3.00)")]
    [DataRow("money", "random(1, 2.12345)", "random(1, 2.1235)")]
    [DataRow("float", "random(1.50, 2.500)", "random(1.5, 2.5)")]
    [DataRow("int", "Random( 01 , 003 )", "random(1, 3)")]
    [DataRow("date", "datetime(\"D\")", "datetime(\"D\")")]
    public void Catalog_DefinitionIsCanonical(string type, string function, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"create table m (a {type} masked with (function = '{function.Replace("'", "''", StringComparison.Ordinal)}')); select masking_function from sys.masked_columns"));

    [TestMethod]
    [DataRow("varchar(9)", "foo()", 16002, "Invalid data masking function in column 'a'.")]
    [DataRow("varchar(9)", "", 16002, "Invalid data masking function in column 'a'.")]
    [DataRow("varchar(9)", " default()", 16002, "Invalid data masking function in column 'a'.")]
    [DataRow("int", "partial(1,\"x\",1)", 16003, "The data type of column 'a' does not support data masking function 'partial'.")]
    [DataRow("int", "EMAIL()", 16003, "The data type of column 'a' does not support data masking function 'email'.")]
    [DataRow("varchar(9)", "random(1,2)", 16003, "The data type of column 'a' does not support data masking function 'random'.")]
    [DataRow("varchar(9)", "datetime(\"Y\")", 16003, "The data type of column 'a' does not support data masking function 'datetime'.")]
    [DataRow("varbinary(9)", "partial(1,\"x\",1)", 16003, "The data type of column 'a' does not support data masking function 'partial'.")]
    [DataRow("varchar(9)", "  Default ( ) ", 16004, "Incorrect number of parameters for data masking function '  Default ' for column 'a'.")]
    [DataRow("varchar(9)", "default(1)", 16004, "Incorrect number of parameters for data masking function 'default' for column 'a'.")]
    [DataRow("varchar(9)", "email() x", 16004, "Incorrect number of parameters for data masking function 'email' for column 'a'.")]
    [DataRow("int", "random(1,2,3)", 16004, "Incorrect number of parameters for data masking function 'random' for column 'a'.")]
    [DataRow("varchar(9)", "partial(-1,\"x\",1)", 16005, "Invalid argument for data masking function 'partial' for column 'a'.")]
    [DataRow("varchar(9)", "partial(1,x,1)", 16005, "Invalid argument for data masking function 'partial' for column 'a'.")]
    [DataRow("varchar(9)", "partial(1.0,\"x\",1)", 16005, "Invalid argument for data masking function 'partial' for column 'a'.")]
    [DataRow("int", "random(10,1)", 16005, "Invalid argument for data masking function 'random' for column 'a'.")]
    [DataRow("int", "random(1.5,2)", 16005, "Invalid argument for data masking function 'random' for column 'a'.")]
    [DataRow("float", "random(1,1e3)", 16005, "Invalid argument for data masking function 'random' for column 'a'.")]
    [DataRow("tinyint", "random(-5,300)", 16005, "Invalid argument for data masking function 'random' for column 'a'.")]
    [DataRow("bit", "random(0,2)", 16005, "Invalid argument for data masking function 'random' for column 'a'.")]
    [DataRow("decimal(6,2)", "random(1,99999)", 16005, "Invalid argument for data masking function 'random' for column 'a'.")]
    [DataRow("datetime", "datetime(\"Q\")", 16005, "Invalid argument for data masking function 'datetime' for column 'a'.")]
    [DataRow("datetime", "datetime(\"y\")", 16005, "Invalid argument for data masking function 'datetime' for column 'a'.")]
    [DataRow("date", "datetime(\"h\")", 16005, "Invalid argument for data masking function 'datetime' for column 'a'.")]
    [DataRow("time", "datetime(\"Y\")", 16005, "Invalid argument for data masking function 'datetime' for column 'a'.")]
    [DataRow("varchar(9)", "default", 16006, "Invalid data masking format for function 'default' in column 'a'.")]
    [DataRow("varchar(9)", "default)", 16006, "Invalid data masking format for function 'default)' in column 'a'.")]
    [DataRow("varchar(9)", "partial(1,\"x\",1", 16006, "Invalid data masking format for function 'partial' in column 'a'.")]
    public void BadFunction_Raises(string type, string function, int number, string message) =>
        new Simulation().AssertSqlError($"create table m (a {type} masked with (function = '{function}'))", number, message);

    [TestMethod]
    [DataRow("create table m (x int not null masked with (function = 'default()'))", "masked")]
    [DataRow("create table m (x int default 1 masked with (function = 'default()'))", "masked")]
    [DataRow("create table m (x int identity masked with (function = 'default()'))", "masked")]
    [DataRow("create table m (x int primary key masked with (function = 'default()'))", "masked")]
    [DataRow("create table m (x int masked with (function 'default()'))", "default()")]
    [DataRow("create table m (x int masked (function = 'default()'))", "(")]
    public void Grammar_Position(string sql, string near) => new Simulation().ValidateSyntaxError(sql, near);

    [TestMethod]
    [DataRow("x varchar(5) collate Latin1_General_BIN masked with (function = 'default()') not null")]
    [DataRow("x int sparse masked with (function = 'default()') null")]
    [DataRow("x int masked with (function = 'default()') identity primary key")]
    [DataRow("x uniqueidentifier masked with (function = 'default()') rowguidcol")]
    [DataRow("x rowversion masked with (function = 'default()')")]
    [DataRow("x int masked with ( FUNCTION = N'default()' )")]
    public void Grammar_Accepted(string column) =>
        AreEqual(1, new Simulation().ExecuteScalar($"create table m ({column}); select count(*) from sys.masked_columns"));

    [TestMethod]
    public void AlterColumn_AddDropReplace()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (x varchar(10), y int masked with (function = 'default()'), z varchar(10) masked with (function = 'email()'));
            alter table a alter column x add masked with (function = 'partial(1, "x", 0)');
            alter table a alter column y drop masked;
            alter table a alter column z add masked with (function = 'default()');
            alter table a add w int masked with (function = 'random(1, 2)') null;
            """);
        AreEqual("x=partial(1, \"x\", 0);z=default();w=random(1, 2)", sim.ExecuteScalar("select string_agg(name + '=' + (masking_function collate database_default), ';') within group (order by column_id) from sys.masked_columns"));
        sim.AssertSqlError("alter table a alter column y drop masked", 16007, "The column 'y' does not have a data masking function.");
        _ = sim.AssertSqlError("alter table a alter column zz drop masked", 4924);
        _ = sim.AssertSqlError("alter table a alter column y add masked with (function = 'email()')", 16003);
    }

    [TestMethod]
    public void AlterColumn_TypeChangeDropsTheMaskUnlessRestated()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (x varchar(10) masked with (function = 'email()'), y varchar(10) masked with (function = 'email()'));
            alter table a alter column x varchar(10);
            alter table a alter column y varchar(20) masked with (function = 'default()') null;
            """);
        AreEqual("y=default()", sim.ExecuteScalar("select string_agg(name + '=' + (masking_function collate database_default), ';') from sys.masked_columns"));
    }

    [TestMethod]
    public void AlterColumn_ComputedColumnRefusals()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table a (x varchar(10) masked with (function = 'default()'), c as x + '!')");
        sim.AssertSqlError("alter table a alter column c add masked with (function = 'default()')", 4928, "Cannot alter column 'c' because it is 'COMPUTED'.");
        var ex = sim.AssertSqlError("alter table a alter column x drop masked", 5074);
        Contains("ALTER TABLE ALTER COLUMN x failed because one or more objects access this column.", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_RollsBack()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table a (x varchar(10), y varchar(10) masked with (function = 'default()'))").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            begin tran;
            alter table a alter column x add masked with (function = 'default()');
            alter table a alter column y drop masked;
            rollback;
            """).ExecuteNonQuery();
        AreEqual("y", connection.CreateCommand("select string_agg(name, ',') from sys.masked_columns").ExecuteScalar());
    }

    [TestMethod]
    public void ColumnProperty_IsMasked() =>
        AreEqual("1|0", Seeded().ExecuteScalar("select concat(columnproperty(object_id('t'), 's', 'IsMasked'), '|', columnproperty(object_id('t'), 'plain', 'IsMasked'))"));

    [TestMethod]
    public void TableType_ListsItsMaskedColumn() =>
        AreEqual("email()", new Simulation().ExecuteScalar("create type tt as table (a varchar(9) masked with (function = 'email()')); select masking_function from sys.masked_columns"));

    [TestMethod]
    [DataRow("select (select t.p) from t where id = 1", "ab-XX-j")]
    [DataRow("select (select top 1 t.p from (values (1)) v (a)) from t where id = 1", "ab-XX-j")]
    [DataRow("select a.x from t cross apply (select t.p x) a where id = 1", "ab-XX-j")]
    [DataRow("select a.x from t outer apply (select upper(t.p) x) a where id = 1", "xxxx")]
    [DataRow("select a.x from t cross apply (select t.plain x) a where id = 1", "p1")]
    public void Read_OuterColumnThroughSubqueryOrApply(string query, object expected) => AreEqual(expected, AsUser(Seeded(), query));

    [TestMethod]
    [DataRow("create function dbo.f() returns varchar(30) as begin return (select p from t where id = 1) end", "select dbo.f()", "xxxx")]
    [DataRow("create function dbo.f() returns varchar(30) as begin declare @v varchar(30); select @v = p from t where id = 1; return @v end", "select dbo.f()", "xxxx")]
    [DataRow("create function dbo.f() returns int as begin declare @v varchar(30); select @v = p from t where id = 1; return case when @v = 'abcdefghij' then 7 else 3 end end", "select dbo.f()", 7)]
    [DataRow("create function dbo.f() returns varchar(30) as begin declare @v varchar(30) = (select p from t where id = 1); set @v = 'z'; return @v end", "select dbo.f()", "z")]
    [DataRow("create function dbo.f(@x int) returns varchar(30) as begin declare @v varchar(30) = 'a'; if @x = 1 select @v = p from t where id = 1; return @v end", "select dbo.f(0)", "xxxx")]
    [DataRow("create function dbo.f() returns int as begin return (select count(*) from t) end", "select dbo.f()", 3)]
    [DataRow("create function dbo.f() returns int as begin return (select i from t where id = 1) end", "select dbo.f()", 0)]
    [DataRow("create function dbo.f() returns int as begin return (select i from t where id = 1) end", "declare @v int; set @v = dbo.f(); select @v", 0)]
    [DataRow("create function dbo.f() returns int as begin return (select i from t where id = 1) end", "declare @v int = dbo.f(); select @v", 42)]
    [DataRow("create function dbo.f() returns int as begin return (select i from t where id = 1) end", "select count(*) from t where dbo.f() = 42", 3)]
    public void Read_ScalarFunctionResult(string function, string query, object expected)
    {
        var sim = Seeded(function, "grant execute to u");
        AreEqual(expected, AsUser(sim, query));
    }

    [TestMethod]
    public void Merge_WritesAndOutputsSourceColumnsMasked()
    {
        var sim = Seeded("create table m (id int, v varchar(30)); insert m values (1, 'old'); grant select, insert, update on m to u");
        using var reader = sim.ExecuteReader("""
            execute as user = 'u';
            merge m using t on m.id = t.id and t.id < 3
            when matched then update set v = t.p
            when not matched and t.id = 2 then insert values (t.id, t.p)
            output $action, t.p, t.s;
            revert;
            """);
        List<string> rows = [];
        while (reader.Read())
            rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetString(2)}");
        rows.Sort(StringComparer.Ordinal);
        AreEqual("INSERT:-XX-:xxxx|UPDATE:ab-XX-j:xxxx", string.Join("|", rows));
        reader.Close();
        AreEqual("1=ab-XX-j;2=-XX-", sim.ExecuteScalar("select string_agg(concat(id, '=', v), ';') within group (order by id) from m"));
    }

    [TestMethod]
    public void Merge_ActionsWritingOneColumnMeetAsCaseArms()
    {
        var sim = Seeded("create table m (id int, v varchar(30)); insert m values (1, 'old'); grant select, insert, update on m to u");
        _ = sim.ExecuteNonQuery("""
            execute as user = 'u';
            merge m using (select id, p, plain from t where id < 3) src on m.id = src.id
            when matched then update set v = src.p + '!'
            when not matched then insert values (src.id, src.plain);
            revert;
            """);
        AreEqual("1=xxxx;2=xxxx", sim.ExecuteScalar("select string_agg(concat(id, '=', v), ';') within group (order by id) from m"));
    }

    [TestMethod]
    public void Update_ThroughViewMasksByBaseColumn()
    {
        var sim = Seeded("create view dbo.v as select id, p, plain from t", "grant select, update on v to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; update v set plain = p where id = 1; update v set plain = left(p, 2) where id = 2; revert;");
        AreEqual("ab-XX-j|xxxx", sim.ExecuteScalar("select string_agg(plain, '|') within group (order by id) from t where id < 3"));
    }

    [TestMethod]
    [DataRow("select cast(p as int) from t where id = 1", 245, "Conversion failed when converting the ****** value '******' to data type ******.")]
    [DataRow("select x from (select p + 1 x from t where id = 1) q", 245, "Conversion failed when converting the ****** value '******' to data type ******.")]
    [DataRow("declare @v int; select @v = cast(p as int) from t where id = 1", 245, "Conversion failed when converting the ****** value '******' to data type ******.")]
    [DataRow("select cast(i + 1000 as tinyint) from t where id = 1", 220, "Arithmetic overflow error for data type ******, value = ******.")]
    [DataRow("select id from t where cast(p as int) = 1", 245, "Conversion failed when converting the varchar value 'abcdefghij' to data type int.")]
    [DataRow("select case when cast(p as int) = 1 then 1 end from t where id = 1", 245, "Conversion failed when converting the varchar value 'abcdefghij' to data type int.")]
    [DataRow("select cast(plain as int) from t where id = 1", 245, "Conversion failed when converting the varchar value 'p1' to data type int.")]
    public void ConversionError_HidesMaskedValue(string query, int number, string message) =>
        Seeded().AssertSqlError($"execute as user = 'u'; {query}", number, message);

    /// <summary>
    /// An inline function reads the mask its base table has when it is
    /// referenced, not the one it had at CREATE, as a view does (probed
    /// 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void InlineFunction_ReadsTheMaskItsTableHasNow()
    {
        var sim = Seeded("create function f() returns table as return select id, s, plain from t", "grant select on f to u");
        AreEqual("xxxx", AsUser(sim, "select s from f() where id = 1"));
        _ = sim.ExecuteNonQuery("alter table t alter column s drop masked; alter table t alter column plain add masked with (function = 'default()')");
        AreEqual("hello", AsUser(sim, "select s from f() where id = 1"));
        AreEqual("xxxx", AsUser(sim, "select plain from f() where id = 1"));
    }

    /// <summary>
    /// A <c>VALUES</c> table constructor cell reading an enclosing query's masked
    /// column masks the column it feeds, as a derived table's projection does;
    /// a masked and an unmasked cell in one column meet as a set operation's
    /// branches (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select (select max(x.v) from (values (t.s)) x(v)) from t where id = 1", "xxxx")]
    [DataRow("select (select max(v) from (values (t.s), ('z')) x(v)) from t where id = 1", "xxxx")]
    [DataRow("select (select x.v from (values (t.e)) x(v)) from t where id = 1", "jXXX@XXXX.com")]
    [DataRow("select (select upper(x.v) from (values (t.s)) x(v)) from t where id = 1", "xxxx")]
    [DataRow("select x.v from t cross apply (values (t.s)) x(v) where id = 1", "xxxx")]
    [DataRow("select x.v from t cross apply (values (t.plain)) x(v) where id = 1", "p1")]
    public void ValuesConstructor_OverAMaskedOuterColumn_Masks(string query, string expected) => AreEqual(expected, AsUser(Seeded(), query));

    /// <summary>
    /// A table-valued function's argument reading a masked column masks every
    /// column the function returns as <c>default()</c>, whatever the function
    /// on the column (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select (select x.v from dbo.tv(t.s) x) from t where id = 1", "xxxx")]
    [DataRow("select x.v from t cross apply dbo.tv(t.e) x where id = 1", "xxxx")]
    [DataRow("select x.v from t cross apply dbo.tv(upper(t.s)) x where id = 1", "xxxx")]
    [DataRow("select x.v from t cross apply dbo.tv(t.plain) x where id = 1", "p1")]
    public void TableValuedFunctionArgument_OverAMaskedColumn_MasksItsColumns(string query, string expected)
    {
        var sim = Seeded("create function dbo.tv(@p varchar(20)) returns table as return select @p v", "grant select on dbo.tv to u");
        AreEqual(expected, AsUser(sim, query));
    }

    // ---- Mask DDL takes ALTER ANY MASK (probed 2026-10-04 against SQL Server 2025) ----

    [TestMethod]
    public void AddingOrDroppingAMask_TakesAlterAnyMask()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.m (id int, d varchar(20) masked with (function = 'default()'))",
            "create user u without login; grant select, alter on dbo.m to u");
        AreEqual((byte)5, sim.AssertSqlError("execute as user = 'u'; alter table dbo.m alter column d drop masked", 15247).State);
        _ = sim.AssertSqlError("execute as user = 'u'; alter table dbo.m alter column id add masked with (function = 'default()')", 15247);
        _ = sim.ExecuteNonQuery("grant alter any mask to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; alter table dbo.m alter column d drop masked");
    }

    [TestMethod]
    public void HasPermsByName_Unmask_AColumnDenyClearsTheObject()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.m (id int, d varchar(20) masked with (function = 'default()'))",
            "create user u without login; grant unmask to u; deny unmask (d) on dbo.m to u");
        AreEqual("1|0", sim.ExecuteScalar("execute as user = 'u'; select concat_ws('|', has_perms_by_name(null, 'DATABASE', 'UNMASK'), has_perms_by_name('dbo.m', 'OBJECT', 'UNMASK'))"));
    }
}
