using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The legacy <c>CREATE DEFAULT</c> / <c>CREATE RULE</c> objects, the four
/// procedures that bind them to columns and alias types, and a bound rule's
/// enforcement. Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class RulesAndDefaultsTests
{
    private static Simulation WithRuleAndDefault(string table = "create table t (id int, q int)")
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create default d_zero as 0", "create rule r_pos as @v >= 0", table);
        return sim;
    }

    private static List<string> Messages(Simulation simulation, string commandText)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Procedure}:{error.Number}@{error.LineNumber}"));
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = command.ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void TheObjects_ListAsDefaultAndRuleWithTheirBatchAsDefinition()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create default d_zero as 0;\n", "\ncreate rule r_pos as @v >= 0;\n");
        const string Row = """
            select concat(type, ':', type_desc, ':', parent_object_id, ':', objectproperty(object_id, 'IsDefault'), ':', objectproperty(object_id, 'IsRule'))
            from sys.objects where name = 
            """;
        AreEqual("D :DEFAULT_CONSTRAINT:0:1:0", sim.ExecuteScalar(Row + "'d_zero'"));
        AreEqual("R :RULE:0:0:1", sim.ExecuteScalar(Row + "'r_pos'"));
        AreEqual("\ncreate rule r_pos as @v >= 0;\n", sim.ExecuteScalar("select object_definition(object_id('r_pos', 'R'))"));
        AreEqual("create default d_zero as 0;\n", sim.ExecuteScalar("select definition from sys.sql_modules where object_id = object_id('d_zero', 'D')"));
    }

    [TestMethod]
    public void ABoundDefault_FillsAnOmittedColumn()
    {
        var sim = WithRuleAndDefault();
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd_zero', 't.q'; insert t (id) values (1); insert t default values");
        AreEqual("0,0", sim.ExecuteScalar("select string_agg(q, ',') from t"));
        AreEqual(1, sim.ExecuteScalar("select iif(default_object_id = object_id('d_zero'), 1, 0) from sys.columns where object_id = object_id('t') and name = 'q'"));
        AreEqual("create default d_zero as 0", sim.ExecuteScalar("select column_default from information_schema.columns where table_name = 't' and column_name = 'q'"));
    }

    [TestMethod]
    public void ABoundDefault_ConvertsAtTheInsert()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create default d as 'abc'", "create table t (a int, b varchar(2)); exec sp_bindefault 'd', 't.a'; exec sp_bindefault 'd', 't.b'");
        _ = sim.AssertSqlError("insert t (b) values ('x')", 245);
        _ = sim.AssertSqlError("insert t (a) values (1)", 2628);
    }

    [TestMethod]
    public void ARuleViolation_IsMsg513AndTerminatesTheStatement()
    {
        var sim = WithRuleAndDefault();
        _ = sim.ExecuteNonQuery("exec sp_bindrule 'r_pos', 't.q'");
        var errors = sim.AssertSqlError("insert t values (1, -5)", 513).Errors;
        AreEqual("A column insert or update conflicts with a rule imposed by a previous CREATE RULE statement. The statement was terminated. The conflict occurred in database 'simulated', table 'dbo.t', column 'q'.", errors[0].Message);
        AreEqual(3621, errors[1].Number);
        AreEqual(1, sim.ExecuteNonQuery("insert t values (2, null)"));
    }

    [TestMethod]
    public void AnUpdate_ChecksOnlyTheColumnsItSets()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create rule r1 as @v > 0", "create rule r2 as @v > 100",
            "create table t (a int, b int); exec sp_bindrule 'r1', 't.a'; insert t values (5, 1); exec sp_bindrule 'r2', 't.a'");
        AreEqual(1, sim.ExecuteNonQuery("update t set b = 2"));
        _ = sim.AssertSqlError("update t set a = a", 513);
    }

    [TestMethod]
    public void NotNull_PrecedesTheRule()
    {
        var sim = WithRuleAndDefault("create table t (id int, q int, n int not null)");
        _ = sim.ExecuteNonQuery("exec sp_bindrule 'r_pos', 't.q'");
        _ = sim.AssertSqlError("insert t values (1, -1, null)", 515);
    }

    [TestMethod]
    public void BindingToAnAliasType_RebindsItsColumnsUnlessFutureOnly()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type ssn from varchar(11)", "create default d_x as 'unknown'", "create rule r_len as len(@s) = 11",
            "create table p0 (id int, s ssn); create table p1 (id int, s ssn)");
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd_x', 'ssn', 'futureonly'; exec sp_bindrule 'r_len', 'ssn'; create table p2 (id int, s ssn)");
        AreEqual("p0:0:1|p1:0:1|p2:1:1", sim.ExecuteScalar("""
            select string_agg(concat(object_name(object_id), ':', sign(default_object_id), ':', sign(rule_object_id)), '|') within group (order by object_name(object_id))
            from sys.columns where name = 's'
            """));
        AreEqual("create default d_x as 'unknown'", sim.ExecuteScalar("select domain_default from information_schema.domains where domain_name = 'ssn'"));
        // The inherited default is judged by the inherited rule.
        _ = sim.AssertSqlError("insert p2 (id) values (1)", 513);
    }

    [TestMethod]
    public void UnbindingAnAliasType_UnbindsTheColumnsCarryingItsBinding()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type ty from int", "create rule r1 as @v > 0", "create rule r2 as @v > 5",
            "create table t (a ty, b ty); exec sp_bindrule 'r1', 'ty'; exec sp_bindrule 'r2', 't.b'; exec sp_bindrule 'r2', 'ty'");
        AreEqual("a:r2|b:r2", sim.ExecuteScalar("select string_agg(concat(name, ':', object_name(rule_object_id)), '|') within group (order by name) from sys.columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("exec sp_unbindrule 'ty'");
        AreEqual(0, sim.ExecuteScalar("select sum(rule_object_id) from sys.columns where object_id = object_id('t')"));
    }

    [TestMethod]
    public void TheBindingProcedures_PrintTheirConfirmations()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type ty from int", "create default d as 0", "create rule r as @v > 0", "create table t (a int, b ty)");
        CollectionAssert.AreEqual(
            new[] { "sp_bindefault:15511@197", "sp_bindrule:15514@194", "sp_bindefault:15512@250", "sp_bindefault:15513@303", "sp_bindrule:15515@247" },
            Messages(sim, "exec sp_bindefault 'd', 't.a'; exec sp_bindrule 'r', 't.a'; exec sp_bindefault 'd', 'ty'; exec sp_bindrule 'r', 'ty', 'futureonly'"));
        CollectionAssert.AreEqual(
            new[] { "sp_unbindefault:15519@115", "sp_unbindrule:15522@111", "sp_unbindefault:15520@161", "sp_unbindefault:15521@216", "sp_unbindrule:15523@150", "sp_unbindrule:15524@203" },
            Messages(sim, "exec sp_unbindefault 't.a'; exec sp_unbindrule 't.a'; exec sp_unbindefault 'ty'; exec sp_unbindrule 'ty'"));
    }

    [TestMethod]
    [DataRow("exec sp_bindefault 'nope', 't.q'", 15016, 102)]
    [DataRow("exec sp_bindrule 'nope', 't.q'", 15017, 104)]
    [DataRow("exec sp_bindefault 'd_zero', 't.nope'", 15148, 230)]
    [DataRow("exec sp_bindrule 'r_pos', 'nope'", 15148, 225)]
    [DataRow("exec sp_bindefault 'd_zero', 'int'", 4185, 223)]
    [DataRow("exec sp_bindefault 'd_zero', 't.q', 'x'", 15100, 80)]
    [DataRow("exec sp_bindrule 'r_pos', 't.q', 'x'", 15106, 81)]
    [DataRow("exec sp_bindefault 'd_zero', 't.id'", 15102, 173)]
    [DataRow("exec sp_bindefault 'd_zero', 't.c'", 15101, 165)]
    [DataRow("exec sp_bindefault 'd_zero', 't.dq'", 15103, 185)]
    [DataRow("exec sp_bindrule 'r_pos', 't.c'", 15107, 162)]
    [DataRow("exec sp_unbindefault 't.q'", 15236, 73)]
    [DataRow("exec sp_unbindrule 't.q'", 15238, 79)]
    [DataRow("exec sp_unbindrule 'nope'", 15148, 137)]
    public void TheBindingProcedures_RefuseWhereRealDoes(string statement, int number, int line)
    {
        var sim = WithRuleAndDefault("create table t (id int identity, q int, c as q + 1, dq int default 1)");
        AreEqual(line, sim.AssertSqlError(statement, number).Errors[0].LineNumber);
    }

    [TestMethod]
    public void AColumnWithABoundDefault_RefusesADefaultConstraint()
    {
        var sim = WithRuleAndDefault();
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd_zero', 't.q'");
        _ = sim.AssertSqlError("alter table t add constraint df default 1 for q", 1781);
    }

    [TestMethod]
    public void Binding_RollsBackWithItsTransaction()
    {
        var sim = WithRuleAndDefault();
        _ = sim.ExecuteNonQuery("begin tran; exec sp_bindefault 'd_zero', 't.q'; rollback");
        AreEqual(0, sim.ExecuteScalar("select default_object_id from sys.columns where object_id = object_id('t') and name = 'q'"));
    }

    [TestMethod]
    public void ABoundObject_CannotBeDroppedUntilUnbound()
    {
        var sim = WithRuleAndDefault();
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd_zero', 't.q'; exec sp_bindrule 'r_pos', 't.q'");
        var error = sim.AssertSqlError("drop default d_zero", 3716).Errors[0];
        AreEqual("The default 'd_zero' cannot be dropped because it is bound to one or more column.", error.Message);
        AreEqual((byte)3, error.State);
        AreEqual((byte)1, sim.AssertSqlError("drop rule r_pos", 3716).Errors[0].State);
        AreEqual(0, sim.ExecuteScalar("exec sp_unbindefault 't.q'; drop default d_zero; drop table t; drop rule r_pos; select count(*) from sys.objects where type in ('D', 'R')"));
    }

    [TestMethod]
    [DataRow("drop default dfa", 3717)]
    [DataRow("drop table d_zero", 3705)]
    [DataRow("drop default r_pos", 3705)]
    [DataRow("drop rule nope", 3701)]
    public void Drop_RefusesWhereRealDoes(string statement, int number)
        => _ = WithRuleAndDefault("create table t (a int constraint dfa default 1)").AssertSqlError(statement, number);

    [TestMethod]
    public void DropIfExists_ToleratesAMissingObject()
        => AreEqual(-1, WithRuleAndDefault().ExecuteNonQuery("drop default if exists nope; drop rule if exists nope"));

    [TestMethod]
    [DataRow("create rule r1 as @a > 0 and @b > 0", 161)]
    [DataRow("create rule r1 as 1 = 1", 160)]
    [DataRow("create rule r1 as a > 0", 128)]
    [DataRow("create rule r1 as @v > (select 1)", 1046)]
    [DataRow("create rule r1 as @v > dbo.nosuch(1)", 4105)]
    [DataRow("create default d1 as a + 1", 128)]
    [DataRow("create default d1 as (select 1)", 1046)]
    [DataRow("create default d1 as @x", 137)]
    [DataRow("create default d1 as 0; select 1", 156)]
    public void Creation_RefusesWhereRealDoes(string statement, int number)
        => IsTrue(new Simulation().AssertSqlError(statement, number).Errors[0].Procedure is "d1" or "r1");

    [TestMethod]
    [DataRow("select 1; create default d1 as 0", "CREATE DEFAULT", 13)]
    [DataRow("select 1; create rule r1 as @v > 0", "CREATE RULE", 12)]
    public void Creation_MustLeadItsBatch(string batch, string label, int state)
    {
        var error = new Simulation().AssertSqlError(batch, 111).Errors[0];
        AreEqual($"'{label}' must be the first statement in a query batch.", error.Message);
        AreEqual((byte)state, error.State);
    }

    [TestMethod]
    public void ANameTaken_IsMsg2714State3()
        => AreEqual((byte)3, WithRuleAndDefault().AssertSqlError("create default t as 1", 2714).Errors[0].State);

    [TestMethod]
    [DataRow("declare @t table (a ty)")]
    [DataRow("create type tt as table (a ty)")]
    public void ABoundAliasType_CannotTypeATableVariableColumn(string statement)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type ty from int", "create rule r1 as @v > 0", "exec sp_bindrule 'r1', 'ty'");
        sim.AssertSqlError($"select 1; {statement}; select 2", 1710,
            "Cannot use alias type with rule or default bound to it as a column type in table variable or return table definition in table valued function. Type 'ty' has a rule bound to it.");
    }

    [TestMethod]
    public void AlteringOrDroppingABoundColumn_IsBlocked()
    {
        var sim = WithRuleAndDefault("create table t (a int, b int)");
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd_zero', 't.a'; exec sp_bindrule 'r_pos', 't.b'");
        Assert.Contains("The object 'd_zero' is dependent on column 'a'.", sim.AssertSqlError("alter table t alter column a bigint", 5074).Message);
        Assert.Contains("The object 'r_pos' is dependent on column 'b'.", sim.AssertSqlError("alter table t drop column b", 5074).Message);
    }

    [TestMethod]
    public void SpHelpConstraint_ListsTheBoundObjects()
    {
        var sim = WithRuleAndDefault();
        _ = sim.ExecuteNonQuery("exec sp_bindefault 'd_zero', 't.q'; exec sp_bindrule 'r_pos', 't.q'");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "exec sp_helpconstraint 't'";
        using var reader = command.ExecuteReader();
        _ = reader.NextResult();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}");
        CollectionAssert.AreEqual(new[] { "DEFAULT on column q (bound with sp_bindefault)|d_zero", "RULE on column q (bound with sp_bindrule)|r_pos" }, rows);
    }

    [TestMethod]
    public void ObjectProperty_TellsConstraintsFromBoundObjects()
        => AreEqual("1:0:0:1:0:0:0:0", WithRuleAndDefault("create table t (a int constraint dfa default 1)").ExecuteScalar("""
            select concat(objectproperty(object_id('dfa'), 'IsConstraint'), ':', objectproperty(object_id('dfa'), 'IsDefault'), ':',
                objectproperty(object_id('dfa'), 'IsRule'), ':', objectproperty(object_id('dfa'), 'IsDefaultCnst'), ':',
                objectproperty(object_id('t'), 'IsConstraint'), ':', objectproperty(object_id('d_zero'), 'IsDefaultCnst'), ':',
                objectproperty(object_id('d_zero'), 'IsEncrypted'), ':', objectproperty(object_id('d_zero'), 'IsQuotedIdentOn'))
            """));

    /// <summary>A DEFAULT constraint's value longer than its column is a truncation, as an inserted one is.</summary>
    [TestMethod]
    public void ADefaultConstraintsValue_IsCheckedForTruncation()
        => _ = new Simulation().AssertSqlError("create table t (a int, b varchar(2) default 'abc'); insert t (a) values (1)", 2628);
}
