using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public sealed class PartitioningTests
{
    private const string LeftFunctionAndScheme = """
        create partition function pf (int) as range left for values (1, 10, 100);
        create partition scheme ps as partition pf all to ([PRIMARY]);
        """;

    private static string? Text(Simulation simulation, string sql) => simulation.ExecuteScalar(sql) as string;

    private static string? Boundaries(Simulation simulation, string function = "pf") =>
        Text(simulation, $"""
            select string_agg(isnull(cast(v.value as varchar(40)), 'NULL'), ',') within group (order by v.boundary_id)
            from sys.partition_range_values v join sys.partition_functions f on f.function_id = v.function_id
            where f.name = '{function}'
            """);

    private static string? Destinations(Simulation simulation) =>
        Text(simulation, "select string_agg(cast(data_space_id as varchar(10)), ',') within group (order by destination_id) from sys.destination_data_spaces");

    private static string? PartitionRows(Simulation simulation, string table, int indexId = 0) =>
        Text(simulation, $"select string_agg(cast(rows as varchar(10)), ',') within group (order by partition_number) from sys.partitions where object_id = object_id('{table}') and index_id = {indexId}");

    [TestMethod]
    public void CreateFunction_CatalogRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create partition function pf (int) as range left for values (1, 10, 100); create partition function pr (bigint) as range right for values (5)");
        AreEqual("pf|65536|4|0|0;pr|65537|2|1|0", Text(sim, """
            select string_agg(concat(name, '|', function_id, '|', fanout, '|', cast(boundary_value_on_right as int), '|', cast(is_system as int)), ';') within group (order by function_id)
            from sys.partition_functions
            """));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.partition_functions where type = 'R' and type_desc = 'RANGE'"));
        AreEqual("1,10,100", Boundaries(sim));
        AreEqual("65536|1|56|4|10|0||56", Text(sim, "select concat(function_id, '|', parameter_id, '|', system_type_id, '|', max_length, '|', precision, '|', scale, '|', collation_name, '|', user_type_id) from sys.partition_parameters where function_id = 65536"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.objects where name in ('pf', 'pr')"));
    }

    [TestMethod]
    [DataRow("varchar(10)", "'m', 'b'", "b,m", "varchar|0|0|10")]
    [DataRow("decimal(10,2)", "1.005, 2", "1.01,2.00", null)]
    [DataRow("int", "1.7, null, 3", "NULL,1,3", "int|10|0|4")]
    [DataRow("float", "2e0, 1.5", "1.5,2", "float|53|0|8")]
    [DataRow("sql_variant", "1, 'a', 2.5", "a,1,2.5", "varchar|0|0|1")]
    [DataRow("int", "", "", "")]
    public void CreateFunction_BoundaryValuesConvertAndSort(string type, string values, string expected, string? firstVariant)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create partition function pf ({type}) as range left for values ({values})");
        AreEqual(expected, Boundaries(sim) ?? "");
        if (firstVariant is null)
            return;
        AreEqual(firstVariant, Text(sim, """
            select top 1 concat(cast(sql_variant_property(value, 'BaseType') as sysname), '|', cast(sql_variant_property(value, 'Precision') as int), '|', cast(sql_variant_property(value, 'Scale') as int), '|', cast(sql_variant_property(value, 'MaxLength') as int))
            from sys.partition_range_values where value is not null order by boundary_id
            """) ?? "");
    }

    [TestMethod]
    public void CreateFunction_DateBoundaries()
        => AreEqual("2020-01-01|2021-01-01|2020-01-01 01:02:03.457|date|10|0|3", Text(new Simulation(), """
            create partition function a (date) as range right for values ('2021-01-01', '2020-01-01');
            create partition function b (datetime2(3)) as range right for values ('2020-01-01T01:02:03.4567');
            select concat(
                (select convert(varchar(10), cast(value as date), 23) from sys.partition_range_values where function_id = 65536 and boundary_id = 1), '|',
                (select convert(varchar(10), cast(value as date), 23) from sys.partition_range_values where function_id = 65536 and boundary_id = 2), '|',
                (select convert(varchar(23), cast(value as datetime2(3)), 121) from sys.partition_range_values where function_id = 65537), '|',
                (select cast(sql_variant_property(value, 'BaseType') as sysname) from sys.partition_range_values where function_id = 65536 and boundary_id = 1), '|',
                (select cast(sql_variant_property(value, 'Precision') as int) from sys.partition_range_values where function_id = 65536 and boundary_id = 1), '|',
                (select cast(sql_variant_property(value, 'Scale') as int) from sys.partition_range_values where function_id = 65536 and boundary_id = 1), '|',
                (select cast(sql_variant_property(value, 'MaxLength') as int) from sys.partition_range_values where function_id = 65536 and boundary_id = 1))
            """));

    [TestMethod]
    public void CreateFunction_StringParameterKeepsCollation()
        => AreEqual("231|40|Latin1_General_BIN", Text(new Simulation(), """
            create partition function pf (nvarchar(20) collate Latin1_General_BIN) as range left for values (N'x');
            select concat(system_type_id, '|', max_length, '|', collation_name) from sys.partition_parameters
            """));

    [TestMethod]
    public void CreateFunction_UnsortedValuesWarn()
    {
        var sim = new Simulation();
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        using var command = connection.CreateCommand();
        command.CommandText = "create partition function a (int) as range left for values (10, 1, 5)";
        _ = command.ExecuteNonQuery();
        AreEqual("Warning: Range value list for partition function 'a' is not sorted by value. Mapping of partitions to filegroups during CREATE PARTITION SCHEME will use the sorted boundary values if the function 'a' is referenced in CREATE PARTITION SCHEME.", messages.Single());
    }

    [TestMethod]
    [DataRow("(text) as range left for values ('a')", 7704, "The type 'text' is not valid for this operation.")]
    [DataRow("(varchar(max)) as range left for values ('a')", 7704, "The type 'varchar(max)' is not valid for this operation.")]
    [DataRow("(timestamp) as range left for values (1)", 7704, "The type 'timestamp' is not valid for this operation.")]
    [DataRow("(xml) as range left for values ('a')", 7704, "The type 'xml' is not valid for this operation.")]
    [DataRow("(geography) as range left for values (1)", 7704, "The type 'geography' is not valid for this operation.")]
    [DataRow("(nosuchtype) as range left for values (1)", 7704, "The type 'nosuchtype' is not valid for this operation.")]
    [DataRow("(int, int) as range left for values (1)", 7703, "Can not create RANGE partition function with multiple parameter types.")]
    [DataRow("(int) as range left for values ('abc')", 7705, "Could not implicitly convert range values type specified at ordinal 1 to partition function parameter type.")]
    [DataRow("(int) as range left for values (1, 3000000000)", 7705, "Could not implicitly convert range values type specified at ordinal 2 to partition function parameter type.")]
    [DataRow("(varchar(2)) as range left for values ('abc')", 7720, "Data truncated when converting range values to the partition function parameter type. The range value at ordinal 1 requires data truncation.")]
    [DataRow("(int) as range left for values (5, 1, 3, 1)", 7708, "Duplicate range boundary values are not allowed in partition function boundary values list. Partition boundary values at ordinal 2 and 4 are equal.")]
    [DataRow("(int) as range left for values (null, null)", 7708, "Duplicate range boundary values are not allowed in partition function boundary values list. Partition boundary values at ordinal 1 and 2 are equal.")]
    public void CreateFunction_Refusals(string tail, int number, string message)
        => new Simulation().AssertSqlError("create partition function pf " + tail, number, message);

    [TestMethod]
    [DataRow("(geography)", (byte)3)]
    [DataRow("(nosuchtype)", (byte)2)]
    [DataRow("(json)", (byte)1)]
    public void CreateFunction_InvalidTypeStates(string type, byte state)
        => AreEqual(state, new Simulation().AssertSqlError($"create partition function pf {type} as range left for values (1)", 7704).State);

    [TestMethod]
    public void CreateFunction_AliasTypeRefused()
        => new Simulation().AssertSqlError("create type myint from int; create partition function pf (myint) as range left for values (1)", 7704, "The type 'myint' is not valid for this operation.");

    [TestMethod]
    public void CreateFunction_TooManyPartitions()
    {
        var values = string.Join(", ", Enumerable.Range(0, 15000));
        _ = new Simulation().AssertSqlError($"create partition function pf (int) as range left for values ({values})", 7719);
        AreEqual(15000, new Simulation().ExecuteScalar($"create partition function pf (int) as range left for values ({string.Join(", ", Enumerable.Range(0, 14999))}); select fanout from sys.partition_functions"));
    }

    [TestMethod]
    public void CreateFunction_IdsAreSpentByFailuresAndNeverReused()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("create partition function a (int) as range left for values (1, 1)", 7708);
        _ = sim.AssertSqlError("create partition function b (text) as range left for values (1)", 7704);
        _ = sim.ExecuteNonQuery("create partition function c (int) as range left for values (1); drop partition function c");
        AreEqual(65539, sim.ExecuteScalar("create partition function d (int) as range left for values (1); select function_id from sys.partition_functions"));
    }

    [TestMethod]
    [DataRow("create partition function pf (int) as range left for values (1); create partition function PF (int) as range left for values (1)", 2714, "There is already an object named 'PF' in the database.")]
    [DataRow("create partition function dbo.pf (int) as range left for values (1)", 102, "Incorrect syntax near '.'.")]
    [DataRow("drop partition function nope", 15151, "Cannot drop the partition function 'nope', because it does not exist or you do not have permission.")]
    [DataRow("drop partition function if exists nope", 156, "Incorrect syntax near the keyword 'if'.")]
    [DataRow("alter partition function nope() split range (1)", 15151, "Cannot alter the partition function 'nope', because it does not exist or you do not have permission.")]
    [DataRow("create partition function pf (int) as range left for values (1); alter partition function pf split range (2)", 102, "Incorrect syntax near 'split'.")]
    public void FunctionStatements_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);

    [TestMethod]
    [DataRow("left", "-5,1,2,10,100,101,NULL", "1,1,2,2,3,4,1")]
    [DataRow("right", "-5,1,2,10,100,101,NULL", "1,2,2,3,4,4,1")]
    public void DollarPartition_MapsValues(string side, string inputs, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create partition function pf (int) as range {side} for values (1, 10, 100)");
        var projections = string.Join(", ", inputs.Split(',').Select(value => $"$partition.pf({value})"));
        AreEqual(expected, Text(sim, $"select concat_ws(',', {projections})"));
    }

    [TestMethod]
    public void DollarPartition_NullBoundaries()
        => AreEqual("1,2,3,2,2,3", Text(new Simulation(), """
            create partition function pl (int) as range left for values (null, 10);
            create partition function pr (int) as range right for values (null, 10);
            select concat_ws(',', $partition.pl(null), $partition.pl(-1), $partition.pl(11), $partition.pr(null), $partition.pr(-1), $partition.pr(11))
            """));

    [TestMethod]
    public void DollarPartition_ConvertsUnderTheParameterCollation()
        => AreEqual("1,2,2,3,3,1", Text(new Simulation(), """
            create partition function pf (varchar(10)) as range right for values ('b', 'm');
            select concat_ws(',', $partition.pf('a'), $partition.pf('B'), $partition.pf('b '), $partition.pf('m'), $partition.pf('z'), $partition.pf(5))
            """));

    [TestMethod]
    public void DollarPartition_QualifiedByTheCurrentDatabase()
        => AreEqual(3, new Simulation().ExecuteScalar("create partition function pf (int) as range left for values (1, 10); select simulated.$PARTITION.Pf(50)"));

    [TestMethod]
    public void DollarPartition_GroupsRows()
        => AreEqual("1:2,2:1,3:1,4:2", Text(new Simulation(), """
            create partition function pf (int) as range left for values (1, 10, 100);
            create table t (a int);
            insert t values (0), (1), (5), (50), (500), (600);
            select string_agg(concat(p, ':', n), ',') within group (order by p)
            from (select $partition.pf(a) p, count(*) n from t group by $partition.pf(a)) g
            """));

    [TestMethod]
    [DataRow("select $partition.nope(1)", 208, "Invalid object name '$partition.nope'.")]
    [DataRow("select nosuchdb.$partition.pf(1)", 208, "Invalid object name 'nosuchdb.$partition.pf'.")]
    [DataRow("select $partition.pf(1, 2)", 8144, "Procedure or function pf has too many arguments specified.")]
    [DataRow("select $partition.pf()", 102, "Incorrect syntax near ')'.")]
    [DataRow("select $partition.pf('abc')", 245, "Conversion failed when converting the varchar value 'abc' to data type int.")]
    [DataRow("select $partition.pf(cast(3000000000 as bigint))", 8115, "Arithmetic overflow error converting expression to data type int.")]
    public void DollarPartition_Refusals(string select, int number, string message)
        => new Simulation().AssertSqlError("create partition function pf (int) as range left for values (1); " + select, number, message);

    [TestMethod]
    public void DollarPartition_MissingDatabaseIsState212()
        => AreEqual(212, new Simulation().AssertSqlError("select nosuchdb.$partition.pf(1)", 208).State);

    [TestMethod]
    public void SplitAndMerge_WithoutScheme()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create partition function pf (int) as range left for values (10, 20); alter partition function pf() split range (15)");
        AreEqual("10,15,20", Boundaries(sim));
        _ = sim.ExecuteNonQuery("alter partition function pf() merge range (10); alter partition function pf() split range (null); alter partition function pf() split range (1.9)");
        AreEqual("NULL,1,15,20", Boundaries(sim));
        _ = sim.ExecuteNonQuery("alter partition function pf() merge range (null); alter partition function pf() merge range (1); alter partition function pf() merge range (15); alter partition function pf() merge range (20)");
        AreEqual(1, sim.ExecuteScalar("select fanout from sys.partition_functions"));
    }

    [TestMethod]
    [DataRow("split range (20)", 7721, "Duplicate range boundary values are not allowed in partition function boundary values list. The boundary value being added is already present at ordinal 2 of the boundary value list.")]
    [DataRow("split range ('x')", 7705, "Could not implicitly convert range values type specified at ordinal 1 to partition function parameter type.")]
    [DataRow("merge range (11)", 7715, "The specified partition range value could not be found.")]
    public void SplitAndMerge_Refusals(string action, int number, string message)
        => new Simulation().AssertSqlError($"create partition function pf (int) as range left for values (10, 20); alter partition function pf() {action}", number, message);

    [TestMethod]
    public void Merge_OfAnEmptyFunction_IsZeroPartitions()
        => new Simulation().AssertSqlError("create partition function pf (int) as range left for values (); alter partition function pf() merge range (1)", 7716, "Can not create or alter a partition function to have zero partitions.");

    [TestMethod]
    public void CreateScheme_CatalogRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme);
        AreEqual("ps|65601|0|0|65536", Text(sim, "select concat(name, '|', data_space_id, '|', cast(is_default as int), '|', cast(is_system as int), '|', function_id) from sys.partition_schemes where type = 'PS' and type_desc = 'PARTITION_SCHEME'"));
        AreEqual("PRIMARY,ps", Text(sim, "select string_agg(name, ',') within group (order by data_space_id) from sys.data_spaces"));
        AreEqual("ps", Text(sim, "select name from sys.data_spaces where type = 'PS' and type_desc = 'PARTITION_SCHEME'"));
        AreEqual("1,1,1,1,1", Destinations(sim));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.filegroups"));
    }

    [TestMethod]
    [DataRow("to ([PRIMARY], [PRIMARY], [PRIMARY])", "1,1,1", null)]
    [DataRow("to ([PRIMARY], [PRIMARY], [PRIMARY], [PRIMARY])", "1,1,1,1", "Partition scheme 'ps' has been created successfully. 'PRIMARY' is marked as the next used filegroup in partition scheme 'ps'.")]
    [DataRow("to ([PRIMARY], [PRIMARY], \"PRIMARY\", 'PRIMARY', [PRIMARY])", "1,1,1,1", "Partition scheme 'ps' has been created successfully. 'PRIMARY' is marked as the next used filegroup in partition scheme 'ps'.|1 filegroups specified after the next used filegroup are ignored.")]
    public void CreateScheme_ExplicitLists(string list, string destinations, string? messages)
    {
        var sim = new Simulation();
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var seen = new List<string>();
        connection.InfoMessage += (_, e) => seen.Add(e.Message);
        using var command = connection.CreateCommand();
        command.CommandText = "create partition function pf (int) as range left for values (1, 10); create partition scheme ps as partition pf " + list;
        _ = command.ExecuteNonQuery();
        AreEqual(destinations, Destinations(sim));
        AreEqual(messages ?? "", string.Join("|", seen));
    }

    [TestMethod]
    [DataRow("create partition scheme s as partition nope all to ([PRIMARY])", 208, "Invalid object name 'nope'.")]
    [DataRow("create partition scheme s as partition pf to ([PRIMARY])", 7707, "The associated partition function 'pf' generates more partitions than there are file groups mentioned in the scheme 's'.")]
    [DataRow("create partition scheme s as partition pf all to ([PRIMARY], [PRIMARY])", 7723, "Only a single filegroup can be specified while creating partition scheme using option ALL to specify all the filegroups.")]
    [DataRow("create partition scheme s as partition pf all to (nofg)", 208, "Invalid object name 'nofg'.")]
    [DataRow("create partition scheme [PRIMARY] as partition pf all to ([PRIMARY])", 2714, "There is already an object named 'PRIMARY' in the database.")]
    [DataRow("create partition scheme s as partition pf all to (primary)", 156, "Incorrect syntax near the keyword 'primary'.")]
    [DataRow("alter partition scheme nope next used [PRIMARY]", 15151, "Cannot alter the partition scheme 'nope', because it does not exist or you do not have permission.")]
    [DataRow("drop partition scheme nope", 15151, "Cannot drop the partition scheme 'nope', because it does not exist or you do not have permission.")]
    public void SchemeStatements_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError("create partition function pf (int) as range left for values (1); " + sql, number, message);

    [TestMethod]
    public void SchemeIds_SkipTheFailuresThatSpendOne()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create partition function pf (int) as range left for values (1)");
        _ = sim.AssertSqlError("create partition scheme a as partition pf to ([PRIMARY])", 7707);
        _ = sim.AssertSqlError("create partition scheme b as partition pf all to (nofg)", 208);
        AreEqual(65602, sim.ExecuteScalar("create partition scheme c as partition pf all to ([PRIMARY]); select data_space_id from sys.partition_schemes"));
    }

    [TestMethod]
    public void Split_NeedsEveryDependentSchemeToHaveANextUsedFilegroup()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (1);
            create partition scheme ps1 as partition pf all to ([PRIMARY]);
            create partition scheme ps2 as partition pf to ([PRIMARY], [PRIMARY]);
            """);
        sim.AssertSqlError("alter partition function pf() split range (5)", 7710, "Warning: The partition scheme 'ps2' does not have any next used filegroup. Partition scheme has not been changed.");
        _ = sim.ExecuteNonQuery("alter partition scheme ps2 next used [PRIMARY]; alter partition function pf() split range (5)");
        AreEqual("1,1,1,1,1,1", Destinations(sim));
        AreEqual("1,5", Boundaries(sim));
    }

    [TestMethod]
    public void NextUsed_ClearingNothingWarns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create partition function pf (int) as range left for values (1); create partition scheme ps as partition pf to ([PRIMARY], [PRIMARY])");
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var seen = new List<string>();
        connection.InfoMessage += (_, e) => seen.Add(e.Message);
        using var command = connection.CreateCommand();
        command.CommandText = "alter partition scheme ps next used; alter partition scheme ps next used [PRIMARY]; alter partition scheme ps next used";
        _ = command.ExecuteNonQuery();
        AreEqual("Warning: The partition scheme 'ps' does not have any next used filegroup. Partition scheme has not been changed.", seen.Single());
        AreEqual("1,1", Destinations(sim));
    }

    [TestMethod]
    [DataRow("left", "fg2,fg3,PRIMARY,fg2", "15", "2,2,3,1")]
    [DataRow("right", "fg2,fg3,PRIMARY,fg3", "15", "2,3,3,1")]
    public void Split_PlacesTheNewPartitionOnTheNextUsedFilegroup(string side, string filegroups, string split, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            alter database current add filegroup fg2;
            alter database current add filegroup fg3;
            create partition function pf (int) as range {side} for values (10, 20);
            create partition scheme ps as partition pf to ({string.Join(", ", filegroups.Split(',').Select(fg => $"[{fg}]"))});
            alter partition function pf() split range ({split});
            """);
        AreEqual(expected, Destinations(sim));
    }

    [TestMethod]
    [DataRow("left", "15", "2,3,1")]
    [DataRow("right", "10", "2,3,1")]
    public void Merge_DropsThePartitionHoldingTheBoundary(string side, string merge, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            alter database current add filegroup fg2;
            alter database current add filegroup fg3;
            create partition function pf (int) as range {side} for values (10, 15, 20);
            create partition scheme ps as partition pf to (fg2, fg3, fg3, [PRIMARY]);
            alter partition function pf() merge range ({merge});
            """);
        AreEqual(expected, Destinations(sim));
    }

    [TestMethod]
    public void Filegroups_AddAndRemove()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database current add filegroup fg2");
        sim.AssertSqlError("alter database current add filegroup fg2", 5035, "Filegroup 'fg2' already exists in this database. Specify a different name or remove the conflicting filegroup if it is empty.");
        _ = sim.ExecuteNonQuery("create partition function pf (int) as range left for values (1); create partition scheme ps as partition pf all to (fg2)");
        sim.AssertSqlError("alter database current remove filegroup fg2", 5042, "The filegroup 'fg2' cannot be removed because it is not empty.");
        _ = sim.ExecuteNonQuery("drop partition scheme ps; alter database current remove filegroup fg2");
        sim.AssertSqlError("alter database current remove filegroup fg2", 5014, "The filegroup 'fg2' does not exist in database 'simulated'.");
        AreEqual((short)2, sim.ExecuteScalar("alter database current add filegroup fg3; select filegroup_id('fg3')"));
    }

    [TestMethod]
    public void CreateDatabase_RegistersItsFilegroups()
        => AreEqual("PRIMARY,archive,hot", Text(new Simulation(), """
            create database d on primary (name = d, filename = 'd.mdf'), filegroup archive (name = a, filename = 'a.ndf'), filegroup hot (name = h, filename = 'h.ndf');
            select string_agg(name, ',') within group (order by data_space_id) from d.sys.filegroups
            """));

    [TestMethod]
    [DataRow("drop partition function pf", 7706, "Partition function 'pf' is being used by one or more partition schemes.")]
    [DataRow("create table t (a int) on ps(a); drop partition scheme ps", 7717, "The partition scheme \"ps\" is currently being used to partition one or more tables.")]
    public void Drop_RefusedWhileInUse(string sql, int number, string message)
        => new Simulation().AssertSqlError(LeftFunctionAndScheme + sql, number, message);

    [TestMethod]
    public void DropFunction_RefusedWhileASchemaBoundModuleCallsIt()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create partition function pf (int) as range left for values (1)",
            "create function f(@x int) returns int with schemabinding as begin return $partition.pf(@x) end");
        sim.AssertSqlError("drop partition function pf", 3729, "Cannot DROP PARTITION FUNCTION 'pf' because it is being referenced by object 'f'.");
        AreEqual(2, sim.ExecuteScalar("select dbo.f(5)"));
    }

    [TestMethod]
    public void Heap_PartitionsCountRowsByTheFunction()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + """
            create table t (a int, b varchar(10)) on ps(a);
            insert t values (0, 'x'), (1, 'x'), (5, 'x'), (50, 'x'), (500, 'x'), (null, 'x'), (600, 'y');
            """);
        AreEqual("3,1,1,2", PartitionRows(sim, "t"));
        AreEqual("3,1,1,2", Text(sim, "select string_agg(cast(row_count as varchar(10)), ',') within group (order by partition_number) from sys.dm_db_partition_stats where object_id = object_id('t')"));
        AreEqual(4, sim.ExecuteScalar("select count(distinct a.allocation_unit_id) from sys.allocation_units a join sys.partitions p on p.partition_id = a.container_id where p.object_id = object_id('t')"));
        AreEqual("HEAP|65601", Text(sim, "select concat(type_desc, '|', data_space_id) from sys.indexes where object_id = object_id('t')"));
        AreEqual("0|1|1|0|1|0", Text(sim, "select concat(index_id, '|', index_column_id, '|', column_id, '|', key_ordinal, '|', partition_ordinal, '|', cast(is_included_column as int)) from sys.index_columns where object_id = object_id('t')"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes i join sys.data_spaces ds on ds.data_space_id = i.data_space_id where i.object_id = object_id('t') and ds.type = 'PS'"));
        _ = sim.ExecuteNonQuery("alter partition scheme ps next used [PRIMARY]; alter partition function pf() split range (3); alter partition function pf() merge range (100)");
        AreEqual("3,0,1,3", PartitionRows(sim, "t"));
    }

    [TestMethod]
    public void ClusteredKey_AndAlignedIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range right for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (id int not null, a int not null, constraint pk primary key clustered (a, id)) on ps(a);
            create index ix on t(id);
            create unique index ux on t(id, a);
            create index ixp on t(id) on [PRIMARY];
            insert t values (1, 5), (2, 15), (3, 25), (4, 25);
            """);
        AreEqual("pk:65601,ix:65601,ux:65601,ixp:1", Text(sim, "select string_agg(concat(name, ':', data_space_id), ',') within group (order by index_id) from sys.indexes where object_id = object_id('t')"));
        AreEqual("pk:1:2:1:0,pk:2:1:2:1,ix:1:1:1:0,ix:2:0:2:1,ux:1:1:1:0,ux:2:2:2:1,ixp:1:1:1:0", Text(sim, """
            select string_agg(concat(i.name, ':', ic.column_id, ':', ic.key_ordinal, ':', ic.index_column_id, ':', ic.partition_ordinal), ',') within group (order by ic.index_id, ic.index_column_id)
            from sys.index_columns ic join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id where ic.object_id = object_id('t')
            """));
        AreEqual("1,1,2", PartitionRows(sim, "t", 1));
        AreEqual("1,1,2", PartitionRows(sim, "t", 2));
        AreEqual("4", PartitionRows(sim, "t", 4));
        AreEqual("2:1:1,2:2:2", Text(sim, "select string_agg(concat(stats_id, ':', stats_column_id, ':', column_id), ',') within group (order by stats_column_id) from sys.stats_columns where object_id = object_id('t') and stats_id = 2"));
    }

    [TestMethod]
    public void NonclusteredIndex_PartitionColumnFollowsTheIncludes()
        => AreEqual("1:2:1:0:0,2:3:0:0:1,3:1:0:1:0", Text(new Simulation(), LeftFunctionAndScheme + """
            create table t (a int not null, b int, c int) on ps(a);
            create index ix on t(b) include (c);
            select string_agg(concat(index_column_id, ':', column_id, ':', key_ordinal, ':', partition_ordinal, ':', cast(is_included_column as int)), ',') within group (order by index_column_id)
            from sys.index_columns where object_id = object_id('t') and index_id = 2
            """));

    [TestMethod]
    public void ClusteredIndex_MovesTheTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + """
            create table t (a int, b int);
            insert t values (1, 1), (20, 2), (30, 3);
            create clustered index cx on t(b) on ps(a);
            create index nx on t(b);
            """);
        AreEqual("cx:65601,nx:65601", Text(sim, "select string_agg(concat(name, ':', data_space_id), ',') within group (order by index_id) from sys.indexes where object_id = object_id('t')"));
        AreEqual("1,0,2,0", PartitionRows(sim, "t", 1));
        _ = sim.ExecuteNonQuery("drop index cx on t");
        AreEqual(65601, sim.ExecuteScalar("select data_space_id from sys.indexes where object_id = object_id('t') and index_id = 0"));
        _ = sim.ExecuteNonQuery("create clustered index cx on t(b) on [PRIMARY]");
        AreEqual(1, sim.ExecuteScalar("select data_space_id from sys.indexes where object_id = object_id('t') and index_id = 1"));
    }

    /// <summary>
    /// <c>DROP INDEX … WITH (MOVE TO …)</c> leaves the heap on the scheme it
    /// names, partitioned by the column it names, or unpartitioned on a
    /// filegroup.
    /// </summary>
    [TestMethod]
    public void DropClusteredIndex_MoveTo_PlacesTheHeap()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + """
            create table t (a int not null, b int);
            insert t values (1, 1), (20, 2), (30, 3);
            create clustered index cx on t (a);
            drop index cx on t with (move to ps (a));
            """);
        AreEqual("1,0,2,0", PartitionRows(sim, "t"));
        _ = sim.ExecuteNonQuery("create clustered index cx on t (a) on ps (a); drop index cx on t with (move to [primary])");
        AreEqual("3", PartitionRows(sim, "t"));
    }

    [TestMethod]
    [DataRow("drop index cx on t with (move to ps)", 2726, "Partition function 'pf' uses 1 columns which does not match with the number of partition columns used to partition the table or index.")]
    [DataRow("drop index cx on t with (move to ps (a, b))", 2726, "Partition function 'pf' uses 1 columns which does not match with the number of partition columns used to partition the table or index.")]
    [DataRow("drop index cx on t with (move to ps (zz))", 1911, "Column name 'zz' does not exist in the target table, index or view.")]
    [DataRow("drop index cx on t with (move to nofg)", 1921, "Invalid filegroup 'nofg' specified.")]
    public void DropClusteredIndex_MoveTo_Refusals(string drop, int number, string message)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + "create table t (a int not null, b int); create clustered index cx on t (a);");
        sim.AssertSqlError(drop, number, message);
    }

    [TestMethod]
    [DataRow("create table t (a int) on ps(b)", 1911, "Column name 'b' does not exist in the target table, index or view.")]
    [DataRow("create table t (a bigint) on ps(a)", 7726, "Partition column 'a' has data type bigint which is different from the partition function 'pf' parameter data type int.")]
    [DataRow("create table t (a varchar(10)) on ps(a)", 7726, "Partition column 'a' has data type varchar(10) which is different from the partition function 'pf' parameter data type int.")]
    [DataRow("create table t (a int) on ps(a, a)", 2703, "Cannot use duplicate column names in the partition columns list. Column name 'a' appears more than once.")]
    [DataRow("create table t (a int) on ps", 2726, "Partition function 'pf' uses 1 columns which does not match with the number of partition columns used to partition the table or index.")]
    [DataRow("create table t (a int) on nops(a)", 1921, "Invalid partition scheme 'nops' specified.")]
    [DataRow("create table t (a int) on pf(a)", 1921, "Invalid partition scheme 'pf' specified.")]
    [DataRow("create table t (a int) on nofg", 1921, "Invalid filegroup 'nofg' specified.")]
    [DataRow("create table t (a int, c as a + 1) on ps(c)", 7724, "Computed column cannot be used as a partition key if it is not persisted. Partition key column 'c' in table 't' is not persisted.")]
    [DataRow("create table t (a int, b text) on ps(a) textimage_on [PRIMARY]", 1707, "Cannot specify TEXTIMAGE_ON filegroup for a partitioned table.")]
    [DataRow("create table t (id int not null, a int not null, constraint pk2 primary key nonclustered (id)) on ps(a)", 1908, "Column 'a' is partitioning column of the index 'pk2'. Partition columns for a unique index must be a subset of the index key.")]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create unique index u on t(id)", 1908, "Column 'a' is partitioning column of the index 'u'. Partition columns for a unique index must be a subset of the index key.")]
    [DataRow("create table t (a int, b int) on ps(a); alter table t drop column a", 5074, "The object 't' is dependent on column 'a'.")]
    [DataRow("create table t (a int, b int) on ps(a); alter table t alter column a int not null", 5074, "The object 't' is dependent on column 'a'.")]
    public void Placement_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError(LeftFunctionAndScheme + sql, number, message);

    [TestMethod]
    public void Placement_ConstraintRefusalFollowedByMsg1750()
        => AreEqual(1750, new Simulation().AssertSqlError(LeftFunctionAndScheme + "create table t (id int not null primary key, a int not null) on ps(a)", 1908).Errors[1].Number);

    [TestMethod]
    public void Placement_StringCollationMustMatch()
        => new Simulation().AssertSqlError("""
            create partition function pf (varchar(10)) as range right for values ('m');
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a varchar(10) collate Latin1_General_BIN) on ps(a)
            """, 7727, "Collation of partition column 'a' does not match collation of corresponding parameter in partition function 'pf'.");

    [TestMethod]
    public void Placement_ReportedBySpHelp()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + "create table t (a int not null, b int, constraint pk primary key (a)) on ps(a)");
        using var reader = sim.ExecuteReader("exec sp_help 't'");
        var cells = new List<string>();
        do
        {
            if (reader.FieldCount > 0 && reader.GetName(0) is "Data_located_on_filegroup" or "index_name")
            {
                while (reader.Read())
                    cells.Add(reader.GetName(0) == "index_name" ? reader.GetString(1) : reader.GetString(0));
            }
        } while (reader.NextResult());
        AreEqual("ps|clustered, unique, primary key located on ps", string.Join("|", cells));
    }

    [TestMethod]
    public void TruncatePartitions_DeletesJustThoseRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + """
            create table t (id int identity, a int) on ps(a);
            insert t (a) values (0), (1), (5), (50), (500), (600);
            truncate table t with (partitions (2, 4));
            """);
        AreEqual("0,1,50", Text(sim, "select string_agg(cast(a as varchar(10)), ',') within group (order by a) from t"));
        AreEqual(7, sim.ExecuteScalar("truncate table t with (partitions (1 to 3)); insert t (a) values (7); select id from t"));
    }

    [TestMethod]
    public void TruncatePartitions_TakesExpressionsAndRollsBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + "create table t (a int) on ps(a); insert t values (0), (5), (50)");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran; declare @p int = 2; truncate table t with (partitions (@p, $partition.pf(50)))").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("select count(*) from t").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(3, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    [DataRow("truncate table t with (partitions (5))", 7722, "Invalid partition number 5 specified for table 't', partition number can range from 1 to 4.")]
    [DataRow("truncate table t with (partitions (-1))", 7722, "Invalid partition number -1 specified for table 't', partition number can range from 1 to 4.")]
    [DataRow("truncate table t with (partitions (3 to 1))", 7728, "Invalid partition range: 3 TO 1. Lower bound must not be greater than upper bound.")]
    [DataRow("truncate table t with (partitions (1 to 2, 2 to 3))", 7711, "The PARTITIONS option was specified more than once for the table, or for at least one of its partitions if the table is partitioned.")]
    [DataRow("truncate table t with (partitions ())", 102, "Incorrect syntax near ')'.")]
    [DataRow("truncate table u with (partitions (1))", 7729, "Cannot specify partition number in the truncate table statement as the table 'u' is not partitioned.")]
    [DataRow("create index nx on t(a) on [PRIMARY]; truncate table t with (partitions (1))", 3756, "TRUNCATE TABLE statement failed. Index 'nx' is not partitioned, but table 't' uses partition function 'pf'. Index and table must use an equivalent partition function.")]
    public void TruncatePartitions_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError(LeftFunctionAndScheme + "create table t (a int) on ps(a); create table u (a int primary key); " + sql, number, message);

    [TestMethod]
    [DataRow("alter index pk on t rebuild partition = 2", null)]
    [DataRow("alter index all on t rebuild partition = 1", null)]
    [DataRow("alter index ix on t reorganize partition = 1", null)]
    [DataRow("alter table t rebuild partition = 2", null)]
    [DataRow("alter index pk on t rebuild partition = 3", "Alter index statement failed because partition number 3 does not exist in index 'pk'.")]
    [DataRow("alter table t rebuild partition = 5", "Alter index statement failed because partition number 5 does not exist in index 'pk'.")]
    public void Rebuild_PartitionNumbers(string sql, string? message)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (10);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b int, constraint pk primary key (a)) on ps(a);
            create index ix on t(b);
            """);
        if (message is null)
            _ = sim.ExecuteNonQuery(sql);
        else
            sim.AssertSqlError(sql, 7730, message);
    }

    [TestMethod]
    public void Rebuild_PartitionNumberOnAPartitionedHeap()
        => new Simulation().AssertSqlError(LeftFunctionAndScheme + "create table t (a int) on ps(a); alter table t rebuild partition = 9", 7730, "Alter table statement failed because partition number 9 does not exist in table 't'.");

    [TestMethod]
    public void Switch_PartitionOutAndBackIn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b varchar(10)) on ps(a);
            create table s (a int not null, b varchar(10));
            insert t values (1, 'x'), (15, 'y'), (16, 'z'), (30, 'w');
            alter table t switch partition 2 to s;
            """);
        AreEqual("15,16", Text(sim, "select string_agg(cast(a as varchar(10)), ',') within group (order by a) from s"));
        AreEqual("1,0,1", PartitionRows(sim, "t"));
        _ = sim.ExecuteNonQuery("alter table s add constraint ck check (a > 10 and a <= 20); alter table s switch to t partition (1 + 1)");
        AreEqual("1,2,1", PartitionRows(sim, "t"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from s"));
    }

    [TestMethod]
    public void Switch_PartitionToPartitionAndIdentityCarried()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create partition function pf2 (int) as range left for values (10, 30);
            create partition scheme ps2 as partition pf2 all to ([PRIMARY]);
            create table t (a int not null, id int identity) on ps(a);
            create table w (a int not null, id int identity(100, 1)) on ps2(a);
            insert t (a) values (1), (15), (25);
            alter table t switch partition 2 to w partition 2;
            insert w (a) values (16);
            """);
        AreEqual("15:2,16:100", Text(sim, "select string_agg(concat(a, ':', id), ',') within group (order by a) from w"));
        sim.AssertSqlError("alter table t switch partition 3 to w partition 3", 4973, "ALTER TABLE SWITCH statement failed. Range defined by partition 3 in table 'simulated.dbo.t' is not a subset of range defined by partition 3 in table 'simulated.dbo.w'.");
    }

    [TestMethod]
    public void Switch_RollsBackWithTheTransaction()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme + "create table t (a int not null) on ps(a); create table s (a int not null); insert t values (5), (6)");
        using var connection = sim.CreateOpenConnection();
        AreEqual(0, connection.CreateCommand("begin tran; alter table t switch partition 2 to s; select @@rowcount").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual("2|0", Text(sim, "select concat((select count(*) from t), '|', (select count(*) from s))"));
    }

    // The source's CHECK constraints are read statically, the way real proves
    // a source fits partition 2 = (10, 20] of a RANGE LEFT function on a NOT
    // NULL int column: comparisons, BETWEEN, IN and OR over the partition
    // column alone, with integer bounds closing over the next integer.
    [TestMethod]
    [DataRow("", 4982)]
    [DataRow(", constraint ck check (b > 0)", 4982)]
    [DataRow(", constraint ck check (a > 10 and a <= 20 and b > 0)", 4982)]
    [DataRow(", constraint ck check (a > 10)", 4972)]
    [DataRow(", constraint ck check (a <= 20)", 4972)]
    [DataRow(", constraint ck check (a between 10 and 20)", 4972)]
    [DataRow(", constraint ck check (a > 10 and a <= 20)", 0)]
    [DataRow(", constraint ck check (a >= 11 and a <= 20)", 0)]
    [DataRow(", constraint ck check (a between 11 and 20)", 0)]
    [DataRow(", constraint ck check (a > 12 and a < 15)", 0)]
    [DataRow(", constraint ck check (a > 10), constraint ck2 check (a <= 20)", 0)]
    [DataRow(", constraint ck check (a in (15, 16))", 0)]
    [DataRow(", constraint ck check ((a > 10 and a <= 20) or a = 15)", 0)]
    [DataRow(", constraint ck check (10 < a and 20 >= a)", 0)]
    public void Switch_SourceCheckConstraintsMustConfineThePartition(string constraints, int number)
    {
        var sql = $"""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b int) on ps(a);
            create table s (a int not null, b int{constraints});
            alter table s switch to t partition 2;
            """;
        if (number == 0)
            _ = new Simulation().ExecuteNonQuery(sql);
        else
            _ = new Simulation().AssertSqlError(sql, number);
    }

    [TestMethod]
    [DataRow("alter table s with nocheck add constraint ck check (a > 10 and a <= 20)", 4972)]
    [DataRow("alter table s add constraint ck check (a > 10 and a <= 20); alter table s nocheck constraint ck", 4972)]
    public void Switch_UntrustedOrDisabledConstraintsProveNothing(string sql, int number)
        => new Simulation().AssertSqlError("""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null) on ps(a);
            create table s (a int not null);
            """ + sql + "; alter table s switch to t partition 2", number);

    [TestMethod]
    [DataRow(1, "check (a <= 10)", 0)]
    [DataRow(1, "", 4982)]
    [DataRow(3, "check (a > 20)", 4972)]
    [DataRow(3, "check (a > 20 and a is not null)", 0)]
    public void Switch_NullableColumnNeedsNotNullOutsidePartitionOne(int partition, string constraint, int number)
    {
        var sql = $"""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int null) on ps(a);
            create table s (a int null {constraint});
            alter table s switch to t partition {partition};
            """;
        if (number == 0)
            _ = new Simulation().ExecuteNonQuery(sql);
        else
            _ = new Simulation().AssertSqlError(sql, number);
    }

    [TestMethod]
    [DataRow("check (a > 100)", 4972)]
    [DataRow("check (a > 10 and a <= 20)", 0)]
    [DataRow("check (a > 0)", 0)]
    [DataRow("check (b > 0)", 4971)]
    public void Switch_TargetCheckConstraintsMustHold(string constraint, int number)
    {
        var sql = $"""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b int) on ps(a);
            insert t values (15, 1);
            create table s (a int not null, b int, constraint ck {constraint});
            alter table t switch partition 2 to s;
            """;
        if (number == 0)
            _ = new Simulation().ExecuteNonQuery(sql);
        else
            _ = new Simulation().AssertSqlError(sql, number);
    }

    [TestMethod]
    [DataRow("create table s (a int not null, b varchar(10)); insert s values (1, 'x'); alter table t switch partition 2 to s", 4905, "ALTER TABLE SWITCH statement failed. The target table 'simulated.dbo.s' must be empty.")]
    [DataRow("create table s (a int not null, b varchar(10)); insert s values (15, 'x'); alter table s switch to t partition 2", 4904, "ALTER TABLE SWITCH statement failed. The specified partition 2 of target table 'simulated.dbo.t' must be empty.")]
    [DataRow("create table s (a int not null, b varchar(11)); alter table t switch partition 2 to s", 4944, "ALTER TABLE SWITCH statement failed because column 'b' has data type varchar(10) in source table 'simulated.dbo.t' which is different from its type varchar(11) in target table 'simulated.dbo.s'.")]
    [DataRow("create table s (a int not null); alter table t switch partition 2 to s", 4943, "ALTER TABLE SWITCH statement failed because table 'simulated.dbo.t' has 2 columns and table 'simulated.dbo.s' has 1 columns.")]
    [DataRow("create table s (b varchar(10), a int not null); alter table t switch partition 2 to s", 4942, "ALTER TABLE SWITCH statement failed because column 'a' at ordinal 1 in table 'simulated.dbo.t' has a different name than the column 'b' at the same ordinal in table 'simulated.dbo.s'.")]
    [DataRow("create table s (a int null, b varchar(10)); alter table t switch partition 2 to s", 4985, "ALTER TABLE SWITCH statement failed because column 'a' does not have the same nullability attribute in tables 'simulated.dbo.t' and 'simulated.dbo.s'.")]
    [DataRow("create table s (a int not null, b varchar(10) collate Latin1_General_BIN); alter table t switch partition 2 to s", 4945, "ALTER TABLE SWITCH statement failed because column 'b' does not have the same collation in tables 'simulated.dbo.t' and 'simulated.dbo.s'.")]
    [DataRow("create table s (a int not null, b varchar(10)); alter table t switch partition 4 to s", 4950, "ALTER TABLE SWITCH statement failed because partition number 4 does not exist in table 'simulated.dbo.t'.")]
    [DataRow("create table s (a int not null, b varchar(10)); alter table t switch to s", 4911, "Cannot specify a partitioned table without partition number in ALTER TABLE SWITCH statement. The table 'simulated.dbo.t' is partitioned.")]
    [DataRow("alter table t switch partition 2 to t", 4955, "ALTER TABLE SWITCH statement failed. The source table 't' and target table 't' are same.")]
    [DataRow("alter table t switch partition 2 to nope", 1088, "Cannot find the object \"nope\" because it does not exist or you do not have permissions.")]
    [DataRow("create table s (a int not null, b varchar(10), constraint pk2 primary key (a)); alter table t switch partition 2 to s", 4913, "ALTER TABLE SWITCH statement failed. The table 'simulated.dbo.s' has clustered index 'pk2' while the table 'simulated.dbo.t' does not have clustered index.")]
    [DataRow("create table s (a int not null, b varchar(10)); create index ix on s(b desc); alter table t switch partition 2 to s", 4947, "ALTER TABLE SWITCH statement failed. There is no identical index in source table 'simulated.dbo.t' for the index 'ix' in target table 'simulated.dbo.s' .")]
    [DataRow("create table p (a int primary key); create table s (a int not null references p(a), b varchar(10)); alter table t switch partition 2 to s", 4968, null)]
    [DataRow("create table s (a int not null, b varchar(10)); alter table t switch 2 to s", 102, "Incorrect syntax near '2'.")]
    public void Switch_Refusals(string sql, int number, string? message)
    {
        var batch = """
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b varchar(10)) on ps(a);
            insert t values (15, 'y');
            """ + sql;
        if (message is null)
            _ = new Simulation().AssertSqlError(batch, number);
        else
            new Simulation().AssertSqlError(batch, number, message);
    }

    [TestMethod]
    public void Switch_BetweenUnpartitionedTables()
    {
        var sim = new Simulation();
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var seen = new List<string>();
        connection.InfoMessage += (_, e) => seen.Add($"{e.Errors[0].Number}/{e.Errors[0].State}: {e.Message}");
        _ = connection.CreateCommand("create table a (x int, constraint ck1 check (x > 5)); create table b (x int, constraint ck2 check (x > 0)); insert a values (6), (7); alter table a switch partition 1 to b").ExecuteNonQuery();
        AreEqual("4903/1: Warning: The specified partition 1 for the table 'simulated.dbo.a' was ignored in ALTER TABLE SWITCH statement because the table is not partitioned.", seen.Single());
        AreEqual("0|2", Text(sim, "select concat((select count(*) from a), '|', (select count(*) from b))"));
    }

    [TestMethod]
    [DataRow("alter table Customers switch to Archive", 13546)]
    [DataRow("alter table Archive switch to Customers", 13577)]
    public void Switch_SystemVersionedTables(string sql, int number)
        => new Simulation().AssertSqlError("""
            create table Customers (Id int not null primary key, Name nvarchar(30) not null,
              Vf datetime2 generated always as row start hidden not null, Vt datetime2 generated always as row end hidden not null,
              period for system_time (Vf, Vt)) with (system_versioning = on (history_table = dbo.CustomersHistory));
            create table Archive (Id int not null primary key, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null);
            """ + sql, number);

    [TestMethod]
    public void Switch_SourceReferencedByAForeignKey()
        => new Simulation().AssertSqlError("""
            create table p (a int not null constraint pkp primary key);
            create table c (x int constraint fkc references p(a));
            create table p2 (a int not null constraint pkp2 primary key);
            alter table p switch to p2
            """, 4967, "ALTER TABLE SWITCH statement failed. SWITCH is not allowed because source table 'simulated.dbo.p' contains primary key for constraint 'fkc'.");

    [TestMethod]
    public void Switch_ComputedDefinitionsMustMatch()
        => new Simulation().AssertSqlError("""
            create table t (a int not null, c as a * 2);
            create table s (a int not null, c as a * 3);
            alter table t switch to s
            """, 4966, "ALTER TABLE SWITCH statement failed. Computed column 'c' defined as '([a]*(2))' in table 'simulated.dbo.t' is different from the same column in table 'simulated.dbo.s' defined as '([a]*(3))'.");

    [TestMethod]
    public void Errors_EndTheBatchAndRollBackTheTransaction()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int); begin tran; insert t values (1)").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("create partition function pf (int, int) as range left for values (1); select 'after'").ExecuteScalar());
        AreEqual("0|0", connection.CreateCommand("select concat(@@trancount, '|', (select count(*) from t))").ExecuteScalar());
    }

    [TestMethod]
    public void Ddl_RollsBackWithTheTransaction()
        => AreEqual("1|1|0", Text(new Simulation(), """
            create partition function pf0 (int) as range left for values (1);
            begin tran;
            create partition function pf (int) as range left for values (1);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            alter partition function pf0() split range (5);
            rollback;
            select concat((select count(*) from sys.partition_functions), '|', (select count(*) from sys.partition_range_values), '|', (select count(*) from sys.partition_schemes))
            """));

    [TestMethod]
    public void Ddl_RaisesTheirEvents()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table log (e nvarchar(max))",
            "create trigger dt on database for DDL_PARTITION_EVENTS as insert log select eventdata().value('(/EVENT_INSTANCE/EventType)[1]', 'nvarchar(100)') + ':' + eventdata().value('(/EVENT_INSTANCE/ObjectType)[1]', 'nvarchar(100)')",
            """
            create partition function pf (int) as range left for values (1);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            alter partition scheme ps next used [PRIMARY];
            alter partition function pf() split range (5);
            drop partition scheme ps;
            drop partition function pf;
            """);
        AreEqual(
            "CREATE_PARTITION_FUNCTION:PARTITION FUNCTION,CREATE_PARTITION_SCHEME:PARTITION SCHEME,ALTER_PARTITION_SCHEME:PARTITION SCHEME,ALTER_PARTITION_FUNCTION:PARTITION FUNCTION,DROP_PARTITION_SCHEME:PARTITION SCHEME,DROP_PARTITION_FUNCTION:PARTITION FUNCTION",
            Text(sim, "select string_agg(e, ',') from log"));
    }

    /// <summary>Msg 7712 names the next-used filegroup as it was created, whatever the scheme wrote.</summary>
    [TestMethod]
    public void TheNextUsedMessage_NamesTheFilegroupCanonically()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        using var command = connection.CreateCommand();
        command.CommandText = "create partition function pf (int) as range right for values (10); create partition scheme ps as partition pf all to ([primary])";
        _ = command.ExecuteNonQuery();
        Contains("'PRIMARY' is marked as the next used filegroup", string.Join("|", messages));
    }

    /// <summary>A nonclustered columnstore index on a partitioned table is aligned with it, its rows counted per partition (probed 2026-09-30 against SQL Server 2025).</summary>
    [TestMethod]
    public void NonclusteredColumnstoreIndex_IsAlignedByDefault()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            {LeftFunctionAndScheme}
            create table t (id int not null, v int) on ps(id);
            insert t values (5, 1), (6, 2), (50, 3);
            create nonclustered columnstore index cs on t(v)
            """);
        AreEqual("ps", Text(simulation, "select ds.name from sys.indexes i join sys.data_spaces ds on ds.data_space_id = i.data_space_id where i.object_id = object_id('t') and i.name = 'cs'"));
        AreEqual("0,2,1,0", PartitionRows(simulation, "t", indexId: 2));
    }

    /// <summary>
    /// sys.partitions counts the rows a scan reads: a deleted row, a rolled-back
    /// insert and a row a SWITCH moved out don't count (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void RowCounts_CountLiveRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {LeftFunctionAndScheme}
            create table u (id int, a int); insert u values (1, 1), (2, 2), (3, 3); delete u where a = 2;
            begin tran; insert u values (9, 9); rollback;
            create table t (id int not null, a int not null) on ps(a);
            create table s (id int not null, a int not null, check (a > 1 and a <= 10));
            insert s values (1, 5);
            alter table s switch to t partition 2;
            """);
        AreEqual("2", PartitionRows(sim, "u"));
        AreEqual("0", PartitionRows(sim, "s"));
        AreEqual("0,1,0,0", PartitionRows(sim, "t"));
    }

    /// <summary>
    /// A varchar boundary keeps no trailing spaces and a varbinary one no
    /// trailing zero bytes; past the parameter's length only trailing spaces or
    /// zeros may run, which an nvarchar boundary loses (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    [DataRow("varchar(5)", "'a  '", "[a]|1")]
    [DataRow("nvarchar(5)", "N'a  '", "[a  ]|6")]
    [DataRow("nvarchar(2)", "N'ab   '", "[ab]|4")]
    [DataRow("char(2)", "'ab   '", "[ab]|2")]
    [DataRow("varbinary(4)", "0x0100", "[0x01]|1")]
    [DataRow("varbinary(2)", "0x010000", "[0x01]|1")]
    [DataRow("sql_variant", "'a  '", "[a  ]|3")]
    public void Boundaries_TrimTrailingPadding(string type, string value, string expected)
        => AreEqual(expected, Text(new Simulation(), $"""
            create partition function pf ({type}) as range left for values ({value});
            select concat('[', case when cast(sql_variant_property(value, 'BaseType') as sysname) like '%binary' then convert(varchar(10), cast(value as varbinary(10)), 1) else cast(value as nvarchar(10)) end, ']|', datalength(value))
            from sys.partition_range_values
            """));

    /// <summary>Msg 7705 is state 1 for a type pair an assignment can't convert, state 2 for a value that fails (probed 2026-10-05).</summary>
    [TestMethod]
    [DataRow("int", "getdate()", 1)]
    [DataRow("date", "20200101", 1)]
    [DataRow("int", "cast(1 as sql_variant)", 1)]
    [DataRow("int", "'abc'", 2)]
    [DataRow("tinyint", "256", 2)]
    public void UnconvertibleBoundary_StateByCause(string type, string value, int state)
        => AreEqual(state, new Simulation().AssertSqlError($"create partition function pf ({type}) as range left for values ({value})", 7705).State);

    /// <summary>The type Msg 7704 names: an alias type as written, sysname among them, and vector as sys.vector (probed 2026-10-05).</summary>
    [TestMethod]
    [DataRow("sysname", "The type 'sysname' is not valid for this operation.", 3)]
    [DataRow("dbo.myint", "The type 'dbo.myint' is not valid for this operation.", 3)]
    [DataRow("vector(3)", "The type 'sys.vector' is not valid for this operation.", 1)]
    public void InvalidParameterType_NamedAsReal(string type, string message, int state)
    {
        var error = new Simulation().AssertSqlError($"create type myint from int; create partition function pf ({type}) as range left for values (null)", 7704);
        AreEqual(message, error.Errors[0].Message);
        AreEqual(state, error.State);
    }

    /// <summary>The function statement refusals real raises compiling the batch (probed 2026-10-05).</summary>
    [TestMethod]
    [DataRow("create partition function pf () as range left for values (1)", 7702)]
    [DataRow("create partition function pf (int) as range left for values ((select 1))", 1046)]
    [DataRow("create partition function pf (varchar(5) collate nosuch) as range left for values ('a')", 448)]
    [DataRow("create partition function pf (int) as range left for values (1) foo", 102)]
    [DataRow("create partition function pf (int) as range left for values (1), (2)", 102)]
    public void FunctionStatement_CompileRefusals(string sql, int number)
        => _ = new Simulation().AssertSqlError($"select 1; {sql}; select 2", number);

    /// <summary><c>ALTER PARTITION FUNCTION … DROP</c> is Msg 156 at the DROP, then Msg 343 for the word after it.</summary>
    [TestMethod]
    public void AlterFunction_Drop_Msg156ThenMsg343()
    {
        var error = new Simulation().AssertSqlError("create partition function pf (int) as range left for values (1); alter partition function pf () drop range (1)", 156);
        CollectionAssert.AreEqual(new[] { 156, 343 }, error.Errors.Select(entry => entry.Number).ToArray());
    }

    /// <summary><c>[default]</c> in a scheme's filegroup list names the default filegroup (probed 2026-10-05).</summary>
    [TestMethod]
    public void Scheme_DefaultFilegroup()
        => AreEqual("1,1,1,1,1", Text(new Simulation(), $"""
            {LeftFunctionAndScheme.Replace("[PRIMARY]", "[default]", StringComparison.Ordinal)}
            select string_agg(cast(data_space_id as varchar(10)), ',') within group (order by destination_id) from sys.destination_data_spaces
            """));

    /// <summary>
    /// A partition number in <c>ALTER INDEX</c>, <c>ALTER TABLE … REBUILD</c> and
    /// <c>SWITCH</c> is any integer-typed expression, refused first as Msg 4957
    /// when not integer-typed and Msg 7722 outside 1 to 15,000 (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    [DataRow("alter index ix on t rebuild partition = 1 + 1", 0)]
    [DataRow("declare @p bigint = 2; alter index ix on t rebuild partition = @p", 0)]
    [DataRow("alter index ix on t rebuild partition = $partition.pf(5)", 0)]
    [DataRow("alter index ix on t rebuild partition = '2'", 4957)]
    [DataRow("alter index ix on t rebuild partition = 2.0", 4957)]
    [DataRow("alter index ix on t rebuild partition = 0", 7722)]
    [DataRow("alter index ix on t rebuild partition = 15001", 7722)]
    [DataRow("alter index ix on t reorganize partition = 9", 2586)]
    [DataRow("alter index ix on t rebuild partition = 9", 7730)]
    [DataRow("alter table t rebuild partition = 0", 7722)]
    [DataRow("alter table t switch partition null to s", 4957)]
    [DataRow("alter table t switch partition cast(2 as bit) to s", 4957)]
    [DataRow("alter table t switch partition -1 to s", 7722)]
    [DataRow("alter index ix on t rebuild partition = 2 with (fillfactor = 50)", 155)]
    [DataRow("alter index ix on t disable; alter index ix on t rebuild partition = 2", 1973)]
    public void PartitionNumberExpressions(string sql, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {LeftFunctionAndScheme}
            create table t (id int not null, a int not null) on ps(a);
            create index ix on t (id);
            create table s (id int not null, a int not null);
            """);
        if (number == 0)
            _ = sim.ExecuteNonQuery(sql);
        else
            _ = sim.AssertSqlError(sql, number);
    }

    /// <summary>
    /// <c>ALTER INDEX ALL … PARTITION = n</c> over a table with an index that
    /// isn't partitioned is Msg 7733, naming the first partitioned index and the
    /// first that isn't (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void AlterIndexAll_PartitionOverUnalignedIndex_Msg7733()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {LeftFunctionAndScheme}
            create table t (id int not null, a int not null, constraint pk primary key (a, id)) on ps(a);
            create index ixu on t (id) on [primary];
            """);
        sim.AssertSqlError("alter index all on t rebuild partition = 2", 7733,
            "'ALTER INDEX' statement failed. The index 'pk' is partitioned while index 'ixu' is not partitioned.");
    }

    /// <summary>
    /// Each partition keeps its own <c>DATA_COMPRESSION</c>: set by a create's
    /// <c>ON PARTITIONS</c> list, a one-partition rebuild or a rebuild of every
    /// partition listing some, and followed through a split, whose new
    /// partition takes the level of the one it split, and a merge (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void DataCompression_PerPartition()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (10, 20, 30);
            create partition scheme ps as partition pf all to ([primary]);
            create table t (id int not null, a int not null) on ps(a) with (data_compression = page on partitions (2), data_compression = row on partitions (4));
            create index ix on t (id) with (data_compression = row on partitions (1 to 2));
            """);
        string Levels(int indexId) => Text(sim, $"select string_agg(left(data_compression_desc, 1), '') within group (order by partition_number) from sys.partitions where object_id = object_id('t') and index_id = {indexId}")!;
        AreEqual("NPNR", Levels(0));
        AreEqual("RRNN", Levels(2));
        _ = sim.ExecuteNonQuery("alter partition function pf () split range (15)");
        AreEqual("NPPNR", Levels(0));
        _ = sim.ExecuteNonQuery("alter partition function pf () merge range (10)");
        AreEqual("PPNR", Levels(0));
        _ = sim.ExecuteNonQuery("alter index ix on t rebuild partition = 2 with (data_compression = none)");
        AreEqual("RNNN", Levels(2));
        _ = sim.ExecuteNonQuery("alter table t rebuild partition = all with (data_compression = none on partitions (1 to 3), data_compression = row on partitions (4))");
        AreEqual("NNNR", Levels(0));
        _ = sim.AssertSqlError("alter table t rebuild with (data_compression = row on partitions (1))", 10737);
        sim.AssertSqlError("alter table t rebuild partition = all with (data_compression = row on partitions (7))", 7722,
            "Invalid partition number 7 specified for table 't', partition number can range from 1 to 4.");
        _ = sim.AssertSqlError("alter index ix on t rebuild partition = all with (data_compression = row on partitions (1), data_compression = page)", 7711);
        sim.AssertSqlError("create index ix2 on t (id) with (data_compression = row on partitions (9))", 7722,
            "Invalid partition number 9 specified for index 'ix2', partition number can range from 1 to 4.");
        sim.AssertSqlError("create table u (id int) with (data_compression = row on partitions (1))", 7729,
            "Cannot specify partition number in the create table statement as the table 'u' is not partitioned.");
    }

    /// <summary>
    /// <c>XML_COMPRESSION</c> is kept per partition as <c>DATA_COMPRESSION</c>
    /// is, beside it, with refusals of its own (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void XmlCompression_PerPartition()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (10);
            create partition scheme ps as partition pf all to ([primary]);
            create table t (id int not null, x xml, v int) on ps(id) with (xml_compression = on on partitions (2), data_compression = page on partitions (1));
            create index ix on t (v) with (xml_compression = on on partitions (1)) on ps(id);
            """);
        string Levels(int indexId)
        {
            var levels = new List<string>();
            using var reader = sim.ExecuteReader($"select xml_compression_desc, left(data_compression_desc, 1) from sys.partitions where object_id = object_id('t') and index_id = {indexId} order by partition_number");
            while (reader.Read())
                levels.Add(reader.GetString(0) + reader.GetString(1));
            return string.Join(",", levels);
        }
        AreEqual("OFFP,ONN", Levels(0));
        AreEqual("ONN,OFFN", Levels(2));
        _ = sim.ExecuteNonQuery("alter table t rebuild partition = all with (xml_compression = off on partitions (2), xml_compression = on on partitions (1))");
        AreEqual("ONP,OFFN", Levels(0));
        _ = sim.ExecuteNonQuery("alter table t rebuild partition = 1 with (xml_compression = off)");
        AreEqual("OFFP,OFFN", Levels(0));
        _ = sim.ExecuteNonQuery("alter index ix on t rebuild partition = 2 with (xml_compression = on)");
        AreEqual("ONN,ONN", Levels(2));
        _ = sim.ExecuteNonQuery("alter table t rebuild partition = 1 with (xml_compression = off on partitions (2))");
        AreEqual("OFFP,OFFN", Levels(0));
        _ = sim.ExecuteNonQuery("alter table t rebuild partition = all with (xml_compression = on on partitions (2))");
        _ = sim.ExecuteNonQuery("alter partition function pf () split range (20)");
        AreEqual("OFFP,ONN,ONN", Levels(0));

        sim.AssertSqlError("create table t2 (id int, x xml) on ps(id) with (xml_compression = on on partitions (4))", 7722,
            "Invalid partition number 4 specified for table 't2', partition number can range from 1 to 3.");
        AreEqual((byte)2, sim.AssertSqlError("create table t2 (id int, x xml) on ps(id) with (xml_compression = on on partitions (1, 1))", 7741).State);
        var both = sim.AssertSqlError("create table t2 (id int, x xml) on ps(id) with (xml_compression = on, xml_compression = off on partitions (1))", 7741);
        AreEqual(1750, both.Errors[1].Number);
        AreEqual((byte)1, sim.AssertSqlError("create index ix2 on t (v) with (xml_compression = on, xml_compression = off on partitions (1)) on ps(id)", 7741).State);
        sim.AssertSqlError("create table t3 (id int, x xml) with (xml_compression = on on partitions (1))", 7729,
            "Cannot specify partition number in the create table statement as the table 't3' is not partitioned.");
        sim.AssertSqlError("alter table t rebuild with (xml_compression = on on partitions (1))", 16209,
            "The PARTITION=ALL clause must be specified to enable XML compression for the table or index.");
        _ = sim.AssertSqlError("alter table t rebuild with (data_compression = page on partitions (1), xml_compression = on on partitions (1))", 10737);
        _ = sim.AssertSqlError("alter index ix on t rebuild with (xml_compression = on on partitions (1))", 16209);
    }

    /// <summary>
    /// SWITCH refuses what real refuses (probed 2026-10-05 against SQL Server
    /// 2025): an index of a partitioned side not partitioned (ahead even of a
    /// non-empty target), a column's persistence, sparse storage or
    /// ROWGUIDCOL, partition columns that differ, compression, filegroups,
    /// change tracking, an indexed view, a view as target.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (id int not null, a int not null, b int) on ps(a); create index ix on t (b) on [primary]; create table s (id int not null, a int not null, b int); insert s values (1, 1, 1)", "alter table t switch partition 2 to s", 7733)]
    [DataRow("create table t (id int not null, a int not null, c as id + 1 persisted) on ps(a); create table s (id int not null, a int not null, c as id + 1)", "alter table t switch partition 2 to s", 4946)]
    [DataRow("create table t (id int not null, a int not null, b int sparse null) on ps(a); create table s (id int not null, a int not null, b int null)", "alter table t switch partition 2 to s", 11412)]
    [DataRow("create table t (id int not null, a int not null, g uniqueidentifier rowguidcol null) on ps(a); create table s (id int not null, a int not null, g uniqueidentifier null)", "alter table t switch partition 2 to s", 4958)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null) on ps(id)", "alter table t switch partition 2 to s partition 2", 4953)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null) with (data_compression = page)", "alter table t switch partition 2 to s", 11406)]
    [DataRow("alter database current add filegroup fg1; create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null) on fg1", "alter table t switch partition 2 to s", 4939)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null)", "alter table t switch partition 2 to v", 4949)]
    [DataRow("create table t (id int not null, a int not null, b int) on ps(a); create index ix on t (b); create table s (id int not null, a int not null, b int); create index ix on s (b)", "alter table t switch partition 2 to s", 4947)]
    public void Switch_Refusals(string setup, string sql, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{LeftFunctionAndScheme} {setup}");
        _ = sim.ExecuteNonQuery("create view v as select 1 x");
        _ = sim.AssertSqlError(sql, number);
    }

    /// <summary>
    /// What SWITCH lets through (probed 2026-10-05): a target index that is
    /// disabled, an aligned index matched by an unpartitioned one carrying the
    /// partition column as an included column, and CHECK constraints read as
    /// numbers, with <c>NOT</c>, a fractional bound and string constants for
    /// a date — where an exclusive bound proves no inclusive one.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (id int not null, a int not null, b int) on ps(a); insert t values (1, 5, 1); create table s (id int not null, a int not null, b int); create index ix on s (b); alter index ix on s disable", "alter table t switch partition 2 to s", true)]
    [DataRow("create table t (id int not null, a int not null, b int) on ps(a); create index ix on t (b); create table s (id int not null, a int not null, b int); create index ix9 on s (b) include (a)", "alter table t switch partition 2 to s", true)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null, check (not (a <= 1 or a > 10)))", "alter table s switch to t partition 2", true)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null, check (a > 1.5 and a <= 10))", "alter table s switch to t partition 2", true)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null, check (a >= 2 and a <= 10))", "alter table s switch to t partition 2", true)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null, check (a > 1 and a < 11))", "alter table s switch to t partition 2", false)]
    [DataRow("create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null, check (a > 1 and a <= 10.5))", "alter table s switch to t partition 2", false)]
    [DataRow("create partition function pd (datetime) as range right for values ('2020-01-01', '2021-01-01'); create partition scheme psd as partition pd all to ([primary]); create table t (id int not null, d datetime not null) on psd(d); create table s (id int not null, d datetime not null, check (d >= '2020-01-01' and d < '2021-01-01'))", "alter table s switch to t partition 2", true)]
    public void Switch_Admitted(string setup, string sql, bool admitted)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{LeftFunctionAndScheme} {setup}");
        if (admitted)
            _ = sim.ExecuteNonQuery(sql);
        else
            _ = sim.AssertSqlError(sql, 4972);
    }

    /// <summary>
    /// SWITCH's option list takes WAIT_AT_LOW_PRIORITY alone, Msg 102 state 170
    /// otherwise, and names a missing or self target as written (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void Switch_OptionsAndNames()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{LeftFunctionAndScheme} create table t (id int not null, a int not null) on ps(a); create table s (id int not null, a int not null)");
        AreEqual(170, sim.AssertSqlError("alter table t switch partition 2 to s with (truncate_target = on)", 102).State);
        _ = sim.AssertSqlError("alter table t switch partition 2 to s with (wait_at_low_priority (max_duration = 1, abort_after_wait = nosuch))", 102);
        _ = sim.ExecuteNonQuery("alter table t switch partition 2 to s with (wait_at_low_priority (max_duration = 1 minutes, abort_after_wait = none))");
        sim.AssertSqlError("alter table t switch partition 2 to dbo.nosuch", 1088, "Cannot find the object \"dbo.nosuch\" because it does not exist or you do not have permissions.");
        sim.AssertSqlError("alter table t switch partition 2 to dbo.t", 4955, "ALTER TABLE SWITCH statement failed. The source table 't' and target table 'dbo.t' are same.");
        _ = sim.AssertSqlError("alter table t switch partition 2 to s garbage", 102);
    }

    /// <summary>
    /// A partition TRUNCATE names the table as written, and an index partitioned
    /// on another column is Msg 4716 (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void TruncatePartitions_NamesAndColumnSet()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {LeftFunctionAndScheme}
            create table t (id int not null, a int not null) on ps(a);
            create table u (id int);
            """);
        sim.AssertSqlError("truncate table dbo.t with (partitions (5))", 7722, "Invalid partition number 5 specified for table 'dbo.t', partition number can range from 1 to 4.");
        sim.AssertSqlError("truncate table dbo.u with (partitions (1))", 7729, "Cannot specify partition number in the truncate table statement as the table 'dbo.u' is not partitioned.");
        _ = sim.ExecuteNonQuery("create index ix on t (id) on ps(id)");
        sim.AssertSqlError("truncate table t with (partitions (1))", 4716, "TRUNCATE TABLE statement failed. The column set used to partition the table 't' is different from the column set used to partition index 'ix'");
    }

    /// <summary>
    /// A partitioned table's statistics take <c>ON PARTITIONS</c>, and
    /// <c>INCREMENTAL = ON</c> unless an index isn't partitioned (Msg 9108
    /// state 3; probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void UpdateStatistics_OnPartitionedTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {LeftFunctionAndScheme}
            create table t (id int not null, a int not null) on ps(a);
            create index ix on t (id);
            update statistics t with resample on partitions (2);
            update statistics t with fullscan, incremental = on;
            create index ixu on t (id) on [primary];
            """);
        AreEqual(3, sim.AssertSqlError("update statistics t with fullscan, incremental = on", 9108).State);
    }

    /// <summary>
    /// A sparse partition column is Msg 1978, a key's own placement refused is
    /// followed by Msg 1750, and an XML index lands on the table's scheme
    /// (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void Placement_SparseKeyClauseAndXmlIndex()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(LeftFunctionAndScheme);
        _ = sim.AssertSqlError("create table t (id int, a int sparse null) on ps(a)", 1978);
        var error = sim.AssertSqlError("create table t (id int not null, b varchar(10) not null, primary key (b) on ps(b))", 7726);
        AreEqual(1750, error.Errors[^1].Number);
        AreEqual("ps", Text(sim, """
            create table x (id int not null, a int not null, d xml, primary key (id, a)) on ps(a);
            create primary xml index px on x (d);
            select ds.name from sys.indexes i join sys.data_spaces ds on ds.data_space_id = i.data_space_id where i.object_id = object_id('x') and i.name = 'px'
            """));
    }

    [TestMethod]
    [DataRow("[primary]")]
    [DataRow("[default]")]
    [DataRow("'primary'")]
    public void SelectInto_On_PlacesTheTable(string dataSpace)
    {
        var simulation = new Simulation();
        AreEqual(1, simulation.ExecuteScalar($"select 1 a into t on {dataSpace}; select data_space_id from sys.indexes where object_id = object_id('t')"));
    }

    [TestMethod]
    public void SelectInto_On_UnknownFilegroupOrScheme_Refused()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("select 1 a into t on nofg", 1921, "Invalid filegroup 'nofg' specified.");
        _ = simulation.ExecuteNonQuery(LeftFunctionAndScheme);
        _ = simulation.AssertSqlError("select 1 a into t on ps", 2726);
        _ = simulation.AssertSqlError("select 1 a into t on ps(a)", 2726);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void NumericParameter_ReportsType108_AndMatchesOnlyNumeric()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create partition function pfn (numeric(10, 2)) as range for values (10);
            create partition scheme psn as partition pfn all to ([primary]);
            create partition function pfd (decimal(10, 2)) as range for values (10);
            create partition scheme psd as partition pfd all to ([primary])
            """);
        AreEqual("108,106", simulation.ExecuteScalar("select string_agg(cast(p.system_type_id as varchar), ',') within group (order by f.name desc) from sys.partition_parameters p join sys.partition_functions f on f.function_id = p.function_id"));
        simulation.AssertSqlError("create table t (a decimal(10, 2)) on psn(a)", 7726, "Partition column 'a' has data type decimal(10,2) which is different from the partition function 'pfn' parameter data type numeric(10,2).");
        simulation.AssertSqlError("create table t (a numeric(10, 2)) on psd(a)", 7726, "Partition column 'a' has data type numeric(10,2) which is different from the partition function 'pfd' parameter data type decimal(10,2).");
        _ = simulation.ExecuteNonQuery("create table t1 (a numeric(10, 2)) on psn(a); create table t2 (a decimal(10, 2)) on psd(a)");
    }

    [TestMethod]
    public void LobValueOnAFilegroupWithoutFiles_Msg622EndsTheBatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database simulated add filegroup fgx");
        _ = simulation.ExecuteNonQuery("create table f (a int, b varchar(max)) on [primary] textimage_on fgx; insert f values (1, replicate(cast('x' as varchar(max)), 7000))");
        using var connection = simulation.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert f values (2, 'y'); update f set b = replicate(cast('x' as varchar(max)), 9000) where a = 2; select 1").ExecuteNonQuery());
        AreEqual(622, error.Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(1, connection.CreateCommand("select count(*) from f").ExecuteScalar());
        AreEqual("caught 622", connection.CreateCommand("begin try insert f values (3, replicate(cast('x' as varchar(max)), 9000)) end try begin catch select concat('caught ', error_number()) end catch").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("truncate table t with (maxdop = 1)", "=")]
    [DataRow("truncate table t with (maxdop)", ")")]
    [DataRow("truncate table t with (foo (1))", "foo")]
    public void TruncateOptionList_RefusedWhereRealRefusesIt(string sql, string near)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int)");
        simulation.ValidateSyntaxError(sql, near);
    }

    [TestMethod]
    [DataRow("alter table s switch to t garbage;", ";")]
    [DataRow("alter table s switch to t garbage", "garbage")]
    public void Switch_StrayWord_RefusedAtTheTokenAfter(string sql, string near)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table s (a int); create table t (a int)");
        simulation.ValidateSyntaxError(sql, near);
    }

    [TestMethod]
    public void Switch_TablesOnDifferentFilegroups_Msg4940()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database simulated add filegroup fgy; alter database simulated add file (name = 'fgyf', filename = '/fgyf.ndf') to filegroup fgy");
        _ = simulation.ExecuteNonQuery("create table s (a int) on [primary]; create table t (a int) on fgy; insert s values (1)");
        simulation.AssertSqlError("alter table s switch to t", 4940, "ALTER TABLE SWITCH statement failed. table 'simulated.dbo.s' is in filegroup 'PRIMARY' and table 'simulated.dbo.t' is in filegroup 'fgy'.");
    }

    [TestMethod]
    public void Ddl_GatedOnAlterAnyDataspace()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create user u without login; create user d without login; alter role db_ddladmin add member d;
            grant alter any dataspace to u; deny alter any dataspace to d
            """);
        _ = simulation.ExecuteNonQuery("execute as user = 'u'; create partition function pf1 (int) as range for values (1); revert");
        var error = simulation.AssertSqlError("execute as user = 'd'; create partition function pf2 (int) as range for values (1)", 6004);
        AreEqual(2, error.State);
        AreEqual("pf1", simulation.ExecuteScalar("select string_agg(name, ',') from sys.partition_functions"));
    }

    [TestMethod]
    [Description("Change tracking on either side refuses a switch, the target's first (Msg 4900 state 1, the source's state 2).")]
    public void Switch_ChangeTrackedSideRefuses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b varchar(10), constraint pkt primary key (a)) on ps(a);
            create table s (a int not null, b varchar(10), constraint pks primary key (a));
            alter database current set change_tracking = on;
            alter table s enable change_tracking;
            """);
        var target = sim.AssertSqlError("alter table t switch partition 2 to s", 4900);
        AreEqual("The ALTER TABLE SWITCH statement failed for table 'simulated.dbo.s'. It is not possible to switch the partition of a table that has change tracking enabled. Disable change tracking before using ALTER TABLE SWITCH.", target.Errors[0].Message);
        AreEqual((byte)1, target.State);
        _ = sim.ExecuteNonQuery("alter table s disable change_tracking; alter table t enable change_tracking");
        var source = sim.AssertSqlError("alter table t switch partition 2 to s", 4900);
        Contains("for table 'simulated.dbo.t'", source.Errors[0].Message);
        AreEqual((byte)2, source.State);
    }

    [TestMethod]
    public void Switch_PartitionsInDifferentFilegroups_Raises4938()
        => new Simulation().AssertSqlError("""
            alter database current add filegroup fg2;
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create partition scheme ps2 as partition pf all to (fg2);
            create table t (a int not null, b varchar(10)) on ps(a);
            create table w (a int not null, b varchar(10)) on ps2(a);
            insert t values (15, 'y');
            alter table t switch partition 2 to w partition 2
            """, 4938, "ALTER TABLE SWITCH statement failed. Partition 2 of table 'simulated.dbo.t' is in filegroup 'PRIMARY' and partition 2 of table 'simulated.dbo.w' is in filegroup 'fg2'.");

    [TestMethod]
    public void Switch_IndexedViews()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create partition function pf (int) as range left for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (a int not null, b varchar(10)) on ps(a);
            create table s (a int not null, b varchar(10));
            insert t values (15, 'y');
            """,
            "create view dbo.vt with schemabinding as select a, b from dbo.t",
            "create unique clustered index cx on dbo.vt (a)");
        sim.AssertSqlError("alter table t switch partition 2 to s", 11401, "ALTER TABLE SWITCH statement failed. Table 'simulated.dbo.t' is partitioned, but index 'cx' on indexed view 'vt' is not partitioned.");
        sim.ExecuteBatches("drop view dbo.vt", "create view dbo.vs with schemabinding as select a, b from dbo.s", "create unique clustered index cx on dbo.vs (a)");
        sim.AssertSqlError("alter table t switch partition 2 to s", 11402, "ALTER TABLE SWITCH statement failed. Target table 'simulated.dbo.s' is referenced by 1 indexed view(s), but source table 'simulated.dbo.t' is only referenced by 0 indexed view(s). Every indexed view on the target table must have at least one matching indexed view on the source table.");
    }
}
