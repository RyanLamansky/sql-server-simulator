using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Row-level security: <c>CREATE</c> / <c>ALTER</c> / <c>DROP SECURITY
/// POLICY</c> and their refusals, the catalog, what a filter predicate hides
/// from every read and write, and what a block predicate refuses. Probed
/// 2026-10-04 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class RowLevelSecurityTests
{
    private const string Table = """
        create table dbo.t (id int primary key, owner sysname not null, a int, b varchar(20));
        insert dbo.t values (1, 'u1', 10, 'x'), (2, 'u2', 20, 'y'), (3, 'dbo', 30, 'z'), (4, 'u1', 0, 'w');
        create table dbo.o (id int primary key, tid int, note varchar(10));
        insert dbo.o values (1, 1, 'o1'), (2, 2, 'o2'), (3, 3, 'o3'), (4, 4, 'o4');
        create user u1 without login;
        create user u2 without login;
        grant select, insert, update, delete on dbo.t to u1, u2;
        grant select, insert, update, delete on dbo.o to u1, u2;
        """;

    private const string Function = "create function dbo.fp(@o sysname) returns table with schemabinding as return select 1 as ok where @o = user_name();";

    private const string Filter = "create security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.t;";

    private static Simulation Seeded(params ReadOnlySpan<string> more)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches([Table, Function, .. more]);
        return simulation;
    }

    private static string Ids(Simulation simulation, string query)
    {
        using var reader = simulation.ExecuteReader(query);
        var ids = new List<string>();
        do
        {
            while (reader.Read())
                ids.Add(reader.IsDBNull(0) ? "null" : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }
        while (reader.NextResult());
        return string.Join(",", ids);
    }

    private static string As(string user, string query) => $"execute as user = '{user}'; {query}; revert;";

    [TestMethod]
    [DataRow("dbo", "3")]
    [DataRow("u1", "1,4")]
    [DataRow("u2", "2")]
    public void Filter_EachPrincipalReadsItsRows_DboIncluded(string user, string expected) =>
        AreEqual(expected, Ids(Seeded(Filter), As(user, "select id from dbo.t order by id")));

    [TestMethod]
    [DataRow("select count(*) from dbo.t", "2")]
    [DataRow("select id from dbo.t where id = 2", "")]
    [DataRow("select t.id from dbo.t join dbo.o on o.tid = t.id order by t.id", "1,4")]
    [DataRow("select o.id from dbo.o left join dbo.t on o.tid = t.id where t.id is null order by o.id", "2,3")]
    [DataRow("select id from dbo.o where tid in (select id from dbo.t) order by id", "1,4")]
    [DataRow("select (select count(*) from dbo.t)", "2")]
    [DataRow("with c as (select * from dbo.t) select count(*) from c", "2")]
    [DataRow("select id from dbo.t union all select id from dbo.t order by id", "1,1,4,4")]
    [DataRow("select x.id from dbo.o cross apply (select id from dbo.t where t.id = o.tid) x order by x.id", "1,4")]
    [DataRow("select id from dbo.t with (nolock) order by id", "1,4")]
    [DataRow("select id from dbo.t with (index(1)) where id between 1 and 4 order by id", "1,4")]
    [DataRow("select top 1 id from dbo.t order by id desc", "4")]
    [DataRow("select id from dbo.t order by id offset 1 rows fetch next 1 rows only", "4")]
    [DataRow("exec ('select count(*) from dbo.t')", "2")]
    public void Filter_AppliesToEveryReadShape(string query, string expected) =>
        AreEqual(expected, Ids(Seeded(Filter), As("u1", query)));

    [TestMethod]
    public void Filter_ViewAndProcedureReadAsTheCaller_ExecuteAsOwnerAsTheOwner()
    {
        var simulation = Seeded(
            Filter,
            "create view dbo.v as select id from dbo.t;",
            "create procedure dbo.p as select id from dbo.t order by id;",
            "create procedure dbo.po with execute as owner as select id from dbo.t order by id;",
            "grant select on dbo.v to u1; grant execute on dbo.p to u1; grant execute on dbo.po to u1;");
        AreEqual("1,4", Ids(simulation, As("u1", "select id from dbo.v order by id")));
        AreEqual("1,4", Ids(simulation, As("u1", "exec dbo.p")));
        AreEqual("3", Ids(simulation, As("u1", "exec dbo.po")));
    }

    [TestMethod]
    public void Filter_HiddenRowsAreNoWriteTarget()
    {
        var simulation = Seeded(Filter);
        AreEqual("0", Ids(simulation, As("u1", "update dbo.t set a = 99 where id = 2; select @@rowcount")));
        AreEqual("2", Ids(simulation, As("u1", "delete dbo.t; select @@rowcount")));
        AreEqual("1", Ids(simulation, As("u2", "update t set a = 7 from dbo.t t join dbo.o on o.tid = t.id; select @@rowcount")));
        AreEqual("1", Ids(simulation, As("u2", "merge dbo.t as tg using (values (1)) s(id) on tg.id = s.id when not matched by source then delete; select @@rowcount")));
        _ = simulation.ExecuteNonQuery("alter security policy dbo.sp with (state = off)");
        AreEqual("3", Ids(simulation, "select id from dbo.t order by id"));
    }

    [TestMethod]
    public void Filter_HiddenRowStillHoldsItsKey() =>
        Seeded(Filter).AssertSqlError(As("u1", "insert dbo.t values (2, 'u1', 1, 'q')"), 2627);

    [TestMethod]
    public void Filter_RunsAheadOfTheStatementsOwnPredicates_HidingADivideByZero()
    {
        var simulation = Seeded(Filter);
        AreEqual("", Ids(simulation, As("u2", "select id from dbo.t where id = 4 and 1 / a = 1")));
        AreEqual("0", Ids(simulation, As("u2", "select count(*) from dbo.t where 1 / a > 0")));
        _ = simulation.AssertSqlError(As("u1", "select id from dbo.t where 1 / a = 0"), 8134);
    }

    [TestMethod]
    public void Filter_PredicateFunctionReadsItsTargetUnfiltered()
    {
        var simulation = Seeded(
            "create function dbo.fr(@id int) returns table with schemabinding as return select 1 as ok from dbo.t where t.id = @id and t.owner = user_name();",
            "create security policy dbo.sp add filter predicate dbo.fr(id) on dbo.t;");
        AreEqual("1,4", Ids(simulation, As("u1", "select id from dbo.t order by id")));
    }

    [TestMethod]
    public void Filter_SessionContextAndLookupTable()
    {
        var simulation = Seeded(
            "create table dbo.acl (u sysname, o sysname); insert dbo.acl values ('u2', 'u1'), ('u2', 'u2');",
            "create function dbo.fl(@o sysname) returns table with schemabinding as return select 1 as ok from dbo.acl where acl.u = user_name() and acl.o = @o union all select 1 where @o = cast(session_context(N'o') as sysname);",
            "create security policy dbo.sp add filter predicate dbo.fl(owner) on dbo.t;");
        AreEqual("1,2,4", Ids(simulation, As("u2", "select id from dbo.t order by id")));
        AreEqual("3", Ids(simulation, "exec sp_set_session_context N'o', N'dbo'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void Filter_SemiJoinsTheFunction_ADuplicatedRowCountsOnce()
    {
        var simulation = Seeded(
            "create function dbo.f2(@o sysname) returns table with schemabinding as return select 1 as ok where @o = user_name() union all select 2 where @o = user_name();",
            "create security policy dbo.sp add filter predicate dbo.f2(owner) on dbo.t;");
        AreEqual("2", Ids(simulation, As("u1", "select count(*) from dbo.t")));
    }

    [TestMethod]
    public void Filter_TemporalQueryFiltersTheCurrentRowsOnly()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login;",
            "create table dbo.tt (id int primary key, owner sysname, vs datetime2 generated always as row start, ve datetime2 generated always as row end, period for system_time (vs, ve)) with (system_versioning = on (history_table = dbo.tth));",
            "insert dbo.tt (id, owner) values (1, 'u1'), (2, 'u2');",
            "delete dbo.tt where id = 2; grant select on dbo.tt to u1;",
            Function,
            "create security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.tt;");
        AreEqual("1,2", Ids(simulation, As("u1", "select id from dbo.tt for system_time all order by id")));
        AreEqual("1", Ids(simulation, As("u1", "select id from dbo.tt order by id")));
    }

    [TestMethod]
    public void Filter_OnASchemaBoundView()
    {
        var simulation = Seeded(
            "create view dbo.v2 with schemabinding as select id, owner from dbo.t;",
            "create security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.v2;",
            "grant select on dbo.v2 to u1;");
        AreEqual("1,4", Ids(simulation, As("u1", "select id from dbo.v2 order by id")));
        AreEqual("1,2,3,4", Ids(simulation, As("u1", "select id from dbo.t order by id")));
    }

    [TestMethod]
    public void Filter_NotSchemaBound_ReaderNeedsSelectOnTheFunction()
    {
        var simulation = Seeded("create security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.t with (schemabinding = off);");
        simulation.AssertSqlError(As("u1", "select id from dbo.t"), 229, "The SELECT permission was denied on the object 'fp', database 'simulated', schema 'dbo'.");
        _ = simulation.ExecuteNonQuery("grant select on dbo.fp to u1");
        AreEqual("1,4", Ids(simulation, As("u1", "select id from dbo.t order by id")));
    }

    [TestMethod]
    public void Filter_NotSchemaBound_DroppedFunctionFailsTheRead()
    {
        var simulation = Seeded("create security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.t with (schemabinding = off);", "drop function dbo.fp;");
        var error = simulation.AssertSqlError("select id from dbo.t", 208);
        AreEqual(33512, error.Errors[1].Number);
        AreEqual("Binding for the non-schema bound security predicate on object 't' failed with one or more errors, indicating the schema of the predicate function has changed. Update or drop the affected security predicates.", error.Errors[1].Message);
    }

    private const string BlockMessage = "The attempted operation failed because the target object 'simulated.dbo.t' has a block predicate that conflicts with this operation. If the operation is performed on a view, the block predicate might be enforced on the underlying table. Modify the operation to target only the rows that are allowed by the block predicate.";

    [TestMethod]
    [DataRow("after insert", "insert dbo.t values (10, 'u2', 1, 'n')", true)]
    [DataRow("after insert", "insert dbo.t values (10, 'u1', 1, 'n')", false)]
    [DataRow("after insert", "merge dbo.t as tg using (values (12)) s(id) on tg.id = s.id when not matched then insert values (s.id, 'u2', 0, 'm')", true)]
    [DataRow("after update", "update dbo.t set owner = 'u2' where id = 1", true)]
    [DataRow("after update", "update dbo.t set a = 0 where id = 2", true)]
    [DataRow("after update", "update dbo.t set owner = 'u1' where id = 2", false)]
    [DataRow("before update", "update dbo.t set owner = 'u1' where id = 2", true)]
    [DataRow("before update", "update dbo.t set owner = 'u2' where id = 1", false)]
    [DataRow("before update", "merge dbo.t as tg using (values (2)) s(id) on tg.id = s.id when matched then update set a = 0", true)]
    [DataRow("before delete", "delete dbo.t where id = 2", true)]
    [DataRow("before delete", "delete dbo.t where id = 1", false)]
    [DataRow("before delete", "insert dbo.t values (10, 'u2', 1, 'n')", false)]
    [DataRow("", "delete dbo.t", true)]
    public void Block_RefusesTheRowItsOperationJudges(string operation, string statement, bool refused)
    {
        var simulation = Seeded($"create security policy dbo.sp add block predicate dbo.fp(owner) on dbo.t {operation};");
        if (!refused)
        {
            _ = simulation.ExecuteNonQuery(As("u1", statement));
            return;
        }
        var error = simulation.AssertSqlError(As("u1", statement), 33504);
        AreEqual(BlockMessage, error.Errors[0].Message);
        AreEqual("The statement has been terminated.", error.Errors[1].Message);
        AreEqual("4", Ids(simulation, "select count(*) from dbo.t"));
    }

    [TestMethod]
    public void Block_AppliesToDboAndThroughAView()
    {
        var simulation = Seeded(
            "create view dbo.v as select * from dbo.t;",
            "grant insert on dbo.v to u1;",
            "create security policy dbo.sp add block predicate dbo.fp(owner) on dbo.t after insert;");
        _ = simulation.AssertSqlError("insert dbo.t values (10, 'u1', 1, 'n')", 33504);
        simulation.AssertSqlError(As("u1", "insert dbo.v values (11, 'u2', 1, 'n')"), 33504, BlockMessage);
    }

    [TestMethod]
    public void Block_ForeignKeyCascadePassesIt()
    {
        var simulation = Seeded(
            "create table dbo.child (id int primary key, tid int references dbo.t(id) on delete cascade, owner sysname); insert dbo.child values (1, 1, 'u2');",
            "create security policy dbo.sp add block predicate dbo.fp(owner) on dbo.child before delete;");
        _ = simulation.ExecuteNonQuery(As("u1", "delete dbo.t where id = 1"));
        AreEqual("0", Ids(simulation, "select count(*) from dbo.child"));
    }

    [TestMethod]
    public void Block_BulkInsertRefusesTheRow()
    {
        var simulation = new Simulation { OpenBulkFile = path => path == "/data/rows.csv" ? new MemoryStream("10,u2,1,n\n"u8.ToArray()) : null };
        simulation.ExecuteBatches(Table, Function, "create security policy dbo.sp add block predicate dbo.fp(owner) on dbo.t after insert;");
        simulation.AssertSqlError("bulk insert dbo.t from '/data/rows.csv' with (fieldterminator = ',', rowterminator = '\n')", 33504, BlockMessage);
        AreEqual("4", Ids(simulation, "select count(*) from dbo.t"));
    }

    [TestMethod]
    public void Block_DisabledPolicyRefusesNothing()
    {
        var simulation = Seeded("create security policy dbo.sp add block predicate dbo.fp(owner) on dbo.t with (state = off);");
        _ = simulation.ExecuteNonQuery(As("u1", "insert dbo.t values (11, 'u2', 1, 'n')"));
        AreEqual("5", Ids(simulation, "select count(*) from dbo.t"));
    }

    [TestMethod]
    [DataRow("select cast(b as int) from dbo.t")]
    [DataRow("select cast('abc' as int) from dbo.t")]
    [DataRow("update dbo.t set a = cast(b as int)")]
    [DataRow("insert dbo.t (id, owner, a) values (9, 'u1', 'abc')")]
    public void ConversionErrors_RedactedInAStatementApplyingAPredicate(string statement)
    {
        var simulation = Seeded(
            "create function dbo.fall(@o sysname) returns table with schemabinding as return select 1 as ok;",
            "create security policy dbo.sp add filter predicate dbo.fall(owner) on dbo.t;");
        simulation.AssertSqlError(statement, 245, "Conversion failed when converting the ****** value '******' to data type ******.");
    }

    [TestMethod]
    public void ConversionErrors_KeepTheirTextWhereNoPredicateApplies()
    {
        var simulation = Seeded(
            "create function dbo.fall(@o sysname) returns table with schemabinding as return select 1 as ok;",
            "create security policy dbo.sp add block predicate dbo.fall(owner) on dbo.t;");
        simulation.AssertSqlError("select cast(b as int) from dbo.t", 245, "Conversion failed when converting the varchar value 'x' to data type int.");
        simulation.AssertSqlError("select cast(note as int) from dbo.o", 245, "Conversion failed when converting the varchar value 'o1' to data type int.");
    }

    [TestMethod]
    public void Catalog_PoliciesAndPredicates()
    {
        var simulation = Seeded(
            "create security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.t, add block predicate dbo.fp(owner + '') on dbo.t after insert with (state = off) not for replication;",
            "alter security policy dbo.sp add block predicate dbo.fp(owner) on dbo.t before delete, drop block predicate on dbo.t after insert;");
        using (var reader = simulation.ExecuteReader("select name, type, type_desc, is_enabled, is_not_for_replication, uses_database_collation, is_schema_bound from sys.security_policies"))
        {
            IsTrue(reader.Read());
            AreEqual("sp", reader.GetString(0));
            AreEqual("SP", reader.GetString(1));
            AreEqual("SECURITY_POLICY", reader.GetString(2));
            IsFalse(reader.GetBoolean(3));
            IsTrue(reader.GetBoolean(4));
            IsTrue(reader.GetBoolean(5));
            IsTrue(reader.GetBoolean(6));
        }
        AreEqual(
            "1|([dbo].[fp]([owner]))|0|FILTER||,3|([dbo].[fp]([owner]))|1|BLOCK|4|BEFORE DELETE",
            Ids(simulation, "select string_agg(concat(security_predicate_id, '|', predicate_definition, '|', predicate_type, '|', predicate_type_desc, '|', operation, '|', operation_desc), ',') within group (order by security_predicate_id) from sys.security_predicates where target_object_id = object_id('dbo.t')"));
        AreEqual("SP", Ids(simulation, "select objectpropertyex(object_id('dbo.sp'), 'BaseType')"));
        AreEqual("SECURITY_POLICY", Ids(simulation, "select type_desc from sys.objects where name = 'sp'"));
    }

    [TestMethod]
    [DataRow("dbo.fp(owner)", "([dbo].[fp]([owner]))")]
    [DataRow("[dbo].[fp] ( [owner] )", "([dbo].[fp]([owner]))")]
    [DataRow("dbo.fp('u1')", "([dbo].[fp]('u1'))")]
    [DataRow("dbo.fp(cast(a as sysname))", "([dbo].[fp](CONVERT([sysname],[a])))")]
    [DataRow("dbo.fp(1)", "([dbo].[fp]((1)))")]
    [DataRow("dbo.fp(upper(owner))", "([dbo].[fp](upper([owner])))")]
    public void Catalog_PredicateDefinitionIsCanonical(string call, string expected) =>
        AreEqual(expected, Ids(Seeded($"create security policy dbo.sp add filter predicate {call} on dbo.t;"), "select predicate_definition from sys.security_predicates"));

    [TestMethod]
    public void Catalog_SchemaBoundPolicyDependencies()
    {
        var simulation = Seeded(Filter);
        AreEqual("fp:0,t:0,t:2", Ids(simulation, "select string_agg(concat(referenced_entity_name, ':', referenced_minor_id), ',') within group (order by referenced_entity_name, referenced_minor_id) from sys.sql_expression_dependencies where referencing_id = object_id('dbo.sp')"));
    }

    [TestMethod]
    [DataRow("create security policy dbo.x add filter predicate dbo.nofn(owner) on dbo.t;", 33270)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(owner) on dbo.nope;", 33268)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(nocol) on dbo.o;", 207)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(note, id) on dbo.o;", 8144)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(note) on dbo.o, add filter predicate dbo.fp(note) on dbo.o;", 33262)]
    [DataRow("create security policy dbo.x add block predicate dbo.fp(note) on dbo.o, add block predicate dbo.fp(note) on dbo.o after insert;", 33262)]
    [DataRow("create security policy dbo.x add filter predicate fp(note) on dbo.o;", 4512)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(owner) on #t;", 33269)]
    [DataRow("declare @v sysname = 'u1'; create security policy dbo.x add filter predicate dbo.fp(@v) on dbo.o;", 112)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp((select 'a')) on dbo.o;", 1046)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(note) on dbo.o after insert;", 102)]
    [DataRow("create security policy dbo.x add block predicate dbo.fp(note) on dbo.o after delete;", 156)]
    [DataRow("create security policy dbo.x add filter predicate dbo.fp(note) on dbo.o with (state = on, state = off);", 1039)]
    [DataRow("create security policy simulated.dbo.x;", 166)]
    [DataRow("create security policy nosch.x;", 2760)]
    [DataRow("alter security policy dbo.nope with (state = off);", 33268)]
    [DataRow("alter security policy dbo.sp drop filter predicate on dbo.o;", 33261)]
    [DataRow("alter security policy dbo.sp add filter predicate dbo.fp(owner) on dbo.t;", 33262)]
    [DataRow("alter security policy dbo.sp with (schemabinding = off);", 102)]
    public void Ddl_RefusalsStopTheBatchAsItCompiles(string statement, int error)
    {
        var simulation = Seeded(Filter);
        _ = simulation.AssertSqlError($"insert dbo.o values (9, 9, 'compiled'); {statement}", error);
        AreEqual("0", Ids(simulation, "select count(*) from dbo.o where id = 9"));
    }

    [TestMethod]
    public void Ddl_SecondEnabledPolicyOnATable_RefusedAsItRuns()
    {
        var simulation = Seeded(Filter);
        simulation.AssertSqlError(
            "create security policy dbo.sp2 add block predicate dbo.fp(owner) on dbo.t;",
            33264,
            "The security policy 'dbo.sp2' cannot be enabled with a predicate on table 'dbo.t'. Table 'dbo.t' is already referenced by the enabled security policy 'dbo.sp'.");
        _ = simulation.ExecuteNonQuery("create security policy dbo.sp2 add block predicate dbo.fp(owner) on dbo.t with (state = off);");
        _ = simulation.AssertSqlError("alter security policy dbo.sp2 with (state = on)", 33264);
    }

    [TestMethod]
    public void Ddl_SchemaBindingPinsTheTableItsColumnsAndTheFunction()
    {
        var simulation = Seeded(Filter);
        simulation.AssertSqlError("drop function dbo.fp", 3729, "Cannot DROP FUNCTION 'dbo.fp' because it is being referenced by object 'sp'.");
        simulation.AssertSqlError("drop table dbo.t", 3729, "Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'sp'.");
        simulation.AssertSqlError("alter table dbo.t alter column owner nvarchar(200)", 5074, "The object 'sp' is dependent on column 'owner'.");
        _ = simulation.ExecuteNonQuery("alter table dbo.t drop column b");
        simulation.AssertSqlError("drop table dbo.sp", 3705, "Cannot use DROP TABLE with 'dbo.sp' because 'dbo.sp' is a security policy. Use DROP SECURITY POLICY.");
    }

    [TestMethod]
    public void Ddl_IndexedViewAndPolicyExcludeEachOther()
    {
        var simulation = Seeded(Filter, "create view dbo.iv with schemabinding as select id, owner from dbo.t;");
        simulation.AssertSqlError("create unique clustered index cx on dbo.iv(id)", 33266, "The index on the view 'dbo.iv' cannot be created because the view is referencing table 'dbo.t' that is referenced by a security policy.");
        var other = Seeded("create view dbo.iv with schemabinding as select id, owner from dbo.t;", "create unique clustered index cx on dbo.iv(id);");
        _ = other.AssertSqlError(Filter, 33265);
    }

    [TestMethod]
    public void Ddl_Permissions()
    {
        var simulation = Seeded();
        var error = simulation.AssertSqlError(As("u1", Filter), 262);
        AreEqual(15247, error.Errors[1].Number);
        _ = simulation.ExecuteNonQuery("grant alter any security policy to u1; grant alter on schema::dbo to u1;");
        _ = simulation.AssertSqlError(As("u1", Filter), 229);
        _ = simulation.ExecuteNonQuery("grant select on dbo.fp to u1;");
        _ = simulation.ExecuteNonQuery(As("u1", Filter));
        AreEqual("1", Ids(simulation, "select count(*) from sys.security_policies"));
    }

    [TestMethod]
    public void Ddl_RollbackRestoresThePolicy()
    {
        var simulation = Seeded(Filter);
        _ = simulation.ExecuteNonQuery("begin tran; alter security policy dbo.sp with (state = off); drop security policy dbo.sp; rollback;");
        AreEqual("1,4", Ids(simulation, As("u1", "select id from dbo.t order by id")));
    }

    [TestMethod]
    public void Locks_RepeatableReadKeepsOnlyTheRowsTheFilterAdmits()
    {
        var simulation = Seeded(Filter);
        AreEqual("2", Ids(simulation, """
            execute as user = 'u1';
            set transaction isolation level repeatable read;
            begin tran;
            select count(*) from dbo.t;
            revert;
            select count(*) from sys.dm_tran_locks where request_session_id = @@spid and resource_type = 'KEY';
            rollback;
            """).Split(',')[1]);
    }

    [TestMethod]
    public void InsteadOfUpdateTrigger_SparesTheReplacedStatementItsChecks()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int not null constraint ck check (a > 0))",
            "insert t values (1)",
            "create function dbo.fn(@a int) returns table with schemabinding as return select 1 ok where @a > 0",
            "create security policy p add block predicate dbo.fn(a) on dbo.t after update",
            "create trigger tr on t instead of update as select a from inserted");
        AreEqual(-5, simulation.ExecuteScalar("update t set a = -5"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("update t set a = null"));
    }

    [TestMethod]
    public void NonSchemaBoundPredicate_ThatNoLongerBinds_StopsTheBatchBeforeItRuns()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int)",
            "create table helper (x int)",
            "create function dbo.fn(@a int) returns table as return select 1 ok from dbo.helper where @a > 0",
            "create security policy p add filter predicate dbo.fn(a) on dbo.t with (schemabinding = off)",
            "drop table helper");
        // Nothing runs: the PRINT's message would follow the errors.
        var ex = simulation.AssertSqlError("print 'ran'; if 1 = 0 select * from t", 208);
        AreEqual("208,4413,33512", string.Join(",", ex.Errors.Select(static e => e.Number)));
        _ = simulation.AssertSqlError("insert t values (1)", 208);
    }
}
