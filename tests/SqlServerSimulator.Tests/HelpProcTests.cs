using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the sp_help family — <c>sp_helptext</c>, <c>sp_help</c>,
/// <c>sp_helpindex</c> and <c>sp_helpconstraint</c>. Every asserted value is
/// probe-confirmed against SQL Server 2025 (2026-07-31).
/// </summary>
[TestClass]
public sealed class HelpProcTests
{
    // The probe fixture: an identity PK, a rowguidcol with a DEFAULT, a CHECK
    // on a scaled decimal, a computed column, a MAX column, and a descending
    // unique index with an INCLUDE.
    private static Simulation NewFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table dbo.t_full (
                id int identity(10, 3) not null constraint PK_t_full primary key,
                g uniqueidentifier rowguidcol not null constraint DF_g default newid(),
                name varchar(50) not null,
                amount decimal(10, 2) null constraint CK_amt check (amount >= 0),
                created datetime2(3) null,
                calc as name + '!',
                note nvarchar(max) null
            );
            create unique index UX_t_full_name on dbo.t_full(name desc) include (amount);
            """);
        return sim;
    }

    // One result set: its column names plus its rows as ordinal-keyed values.
    private sealed record HelpSet(string[] Names, List<object?[]> Rows);

    // Every result set of one command, plus the severity-10 messages it raised
    // — captured in a single execution so the two can't describe different runs.
    private static (List<HelpSet> Sets, List<SimulatedError> Errors) RunHelp(
        Simulation simulation, string commandText)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        var errors = new List<SimulatedError>();
        connection.InfoMessage += (_, e) => errors.AddRange(e.Errors);
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        using var reader = command.ExecuteReader();
        var sets = new List<HelpSet>();
        do
        {
            if (reader.FieldCount > 0)
            {
                var names = new string[reader.FieldCount];
                for (var i = 0; i < names.Length; i++)
                    names[i] = reader.GetName(i);
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var values = new object?[reader.FieldCount];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(values);
                }

                sets.Add(new HelpSet(names, rows));
            }
        }
        while (reader.NextResult());
        return (sets, errors);
    }

    private static List<HelpSet> ResultSets(Simulation simulation, string commandText) =>
        RunHelp(simulation, commandText).Sets;

    private static string[] ColumnNames(Simulation simulation, string commandText) =>
        ResultSets(simulation, commandText)[0].Names;

    private static List<string> HelpText(Simulation simulation, string commandText) =>
        ResultSets(simulation, commandText)[0].Rows.ConvertAll(r => (string)r[0]!);

    // ===== sp_helptext =====

    [TestMethod]
    public void SpHelpText_ResultSetIsSingleTextColumn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v1 as select 1 as x");
        CollectionAssert.AreEqual(new[] { "Text" }, ColumnNames(sim, "exec sp_helptext 'v1'"));
    }

    [TestMethod]
    public void SpHelpText_LfOnlyDefinition_IsOneRowWithEmbeddedNewlines()
    {
        // Real splits only on CR+LF pairs, so an LF-only module body under 255
        // characters comes back as a single row (probe-confirmed).
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p1\nas\nselect 1 as a");
        var lines = HelpText(sim, "exec sp_helptext 'p1'");
        HasCount(1, lines);
        AreEqual("create procedure dbo.p1\nas\nselect 1 as a", lines[0]);
    }

    [TestMethod]
    public void SpHelpText_CrLfDefinition_SplitsPerLineKeepingThePair()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p1\r\nas\r\nselect 1 as a");
        CollectionAssert.AreEqual(
            new[] { "create procedure dbo.p1\r\n", "as\r\n", "select 1 as a" },
            HelpText(sim, "exec sp_helptext 'p1'").ToArray());
    }

    [TestMethod]
    public void SpHelpText_LineLongerThan255_CutsIntoFixedPieces()
    {
        // 38-char prefix + a 700-char literal + a 7-char suffix = 745, which
        // real emits as 255 / 255 / 235.
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p_long as select '" + new string('x', 700) + "' as a");
        var lines = HelpText(sim, "exec sp_helptext 'p_long'");
        CollectionAssert.AreEqual(new[] { 255, 255, 235 }, lines.ConvertAll(l => l.Length).ToArray());
        AreEqual(745, string.Concat(lines).Length);
    }

    [TestMethod]
    public void SpHelpText_ReadsTheSameDefinitionAsObjectDefinition()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f1(@a int) returns int as begin return @a + 1 end");
        AreEqual(
            sim.ExecuteScalar("select object_definition(object_id('dbo.f1'))"),
            string.Concat(HelpText(sim, "exec sp_helptext '[dbo].[f1]'")));
    }

    [TestMethod]
    public void SpHelpText_Trigger()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.tr1 on dbo.t after insert as select 1");
        AreEqual("create trigger dbo.tr1 on dbo.t after insert as select 1",
            string.Concat(HelpText(sim, "exec sp_helptext 'tr1'")));
    }

    [TestMethod]
    public void SpHelpText_CheckAndDefaultConstraints()
    {
        var sim = NewFixture();
        AreEqual("(amount >= 0)", string.Concat(HelpText(sim, "exec sp_helptext 'CK_amt'")));
        AreEqual("(newid())", string.Concat(HelpText(sim, "exec sp_helptext 'DF_g'")));
    }

    [TestMethod]
    public void SpHelpText_ComputedColumn_PositionalAndNamedArguments()
    {
        var sim = NewFixture();
        AreEqual("(name + '!')", string.Concat(HelpText(sim, "exec sp_helptext 't_full', 'calc'")));
        AreEqual("(name + '!')",
            string.Concat(HelpText(sim, "exec sp_helptext @objname = 't_full', @columnname = 'calc'")));
    }

    [TestMethod]
    public void SpHelpText_MissingObject_Msg15009()
        => new Simulation().AssertSqlError("exec sp_helptext 'dbo.nope'", 15009,
            "The object 'dbo.nope' does not exist in database 'simulated' or is invalid for this operation.");

    [TestMethod]
    public void SpHelpText_Table_Msg15197()
        => NewFixture().AssertSqlError("exec sp_helptext 'dbo.t_full'", 15197,
            "There is no text for object 'dbo.t_full'.");

    [TestMethod]
    public void SpHelpText_KeyConstraint_Msg15197()
        => NewFixture().AssertSqlError("exec sp_helptext 'PK_t_full'", 15197,
            "There is no text for object 'PK_t_full'.");

    [TestMethod]
    public void SpHelpText_ColumnFormOnAView_Msg15218()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v1 as select 1 as x");
        sim.AssertSqlError("exec sp_helptext 'dbo.v1', 'x'", 15218, "Object 'dbo.v1' is not a table.");
    }

    [TestMethod]
    public void SpHelpText_UnknownColumn_Msg15645()
        => NewFixture().AssertSqlError("exec sp_helptext 't_full', 'zz'", 15645, "Column 'zz' does not exist.");

    [TestMethod]
    public void SpHelpText_NonComputedColumn_Msg15646()
        => NewFixture().AssertSqlError("exec sp_helptext 't_full', 'name'", 15646,
            "Column 'name' is not a computed column.");

    [TestMethod]
    public void SpHelpText_ForeignDatabaseQualifier_Msg15250()
        => NewFixture().AssertSqlError("exec sp_helptext 'master.dbo.t_full'", 15250,
            "The database name component of the object qualifier must be the name of the current database.");

    [TestMethod]
    public void SpHelpText_NoArgument_Msg201()
        => new Simulation().AssertSqlError("exec sp_helptext", 201,
            "Procedure or function 'sp_helptext' expects parameter '@objname', which was not supplied.");

    // ===== sp_help: object form =====

    [TestMethod]
    public void SpHelp_Table_ObjectRowAndResultSetSequence()
    {
        var sim = NewFixture();
        var sets = ResultSets(sim, "exec sp_help 'dbo.t_full'");
        // Object info, columns, identity, rowguidcol, filegroup, indexes,
        // constraints — the referencing-FK and referencing-view sets collapse
        // into severity-10 messages when empty.
        HasCount(7, sets);
        CollectionAssert.AreEqual(
            new[] { "Name", "Owner", "Type", "Created_datetime" }, sets[0].Names);
        AreEqual("t_full", sets[0].Rows[0][0]);
        AreEqual("dbo", sets[0].Rows[0][1]);
        AreEqual("user table", sets[0].Rows[0][2]);
        AreEqual("PRIMARY", sets[4].Rows[0][0]);
    }

    [TestMethod]
    public void SpHelp_Table_ColumnDetailRows()
    {
        var columns = ResultSets(NewFixture(), "exec sp_help 'dbo.t_full'")[1].Rows;
        // Column_name / Type / Computed / Length / Prec / Scale / Nullable /
        // TrimTrailingBlanks / FixedLenNullInSource / Collation. Prec and Scale
        // are char(5)-padded and blank for the types real excludes.
        CollectionAssert.AreEqual(
            new object?[] { "id", "int", "no", 4, "10   ", "0    ", "no", "(n/a)", "(n/a)", null },
            columns[0]);
        CollectionAssert.AreEqual(
            new object?[] { "g", "uniqueidentifier", "no", 16, "     ", "     ", "no", "(n/a)", "(n/a)", null },
            columns[1]);
        CollectionAssert.AreEqual(
            new object?[]
            {
                "name", "varchar", "no", 50, "     ", "     ", "no", "no", "no",
                "SQL_Latin1_General_CP1_CI_AS",
            },
            columns[2]);
        CollectionAssert.AreEqual(
            new object?[] { "amount", "decimal", "no", 9, "10   ", "2    ", "yes", "(n/a)", "(n/a)", null },
            columns[3]);
        // datetime2(3): max_length 7 bytes, display precision 23 (19 + scale + the point).
        CollectionAssert.AreEqual(
            new object?[] { "created", "datetime2", "no", 7, "23   ", "3    ", "yes", "(n/a)", "(n/a)", null },
            columns[4]);
        AreEqual("yes", columns[5][2]);   // calc is the computed column
        AreEqual(-1, columns[6][3]);      // nvarchar(max) reports max_length -1
    }

    [TestMethod]
    public void SpHelp_Table_IdentityAndRowGuidColSets()
    {
        var sets = ResultSets(NewFixture(), "exec sp_help 'dbo.t_full'");
        CollectionAssert.AreEqual(new object?[] { "id", 10m, 3m, 0 }, sets[2].Rows[0]);
        AreEqual("g", sets[3].Rows[0][0]);
    }

    [TestMethod]
    public void SpHelp_TableWithoutIdentityOrRowGuidCol_ReportsThePlaceholderRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int)");
        var sets = ResultSets(sim, "exec sp_help 'dbo.t'");
        CollectionAssert.AreEqual(new object?[] { "No identity column defined.", null, null, null }, sets[2].Rows[0]);
        AreEqual("No rowguidcol column defined.", sets[3].Rows[0][0]);
    }

    [TestMethod]
    public void SpHelp_Procedure_ParameterSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p1 @i int, @v varchar(50), @d decimal(9,2) as select 1");
        var sets = ResultSets(sim, "exec sp_help 'dbo.p1'");
        HasCount(2, sets);
        AreEqual("stored procedure", sets[0].Rows[0][2]);
        // Parameter_name / Type / Length / Prec / Scale / Param_order / Collation.
        CollectionAssert.AreEqual(new object?[] { "@i", "int", (short)4, 10, 0, 1, null }, sets[1].Rows[0]);
        CollectionAssert.AreEqual(
            new object?[] { "@v", "varchar", (short)50, 50, null, 2, "SQL_Latin1_General_CP1_CI_AS" },
            sets[1].Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "@d", "decimal", (short)5, 9, 2, 3, null }, sets[1].Rows[2]);
    }

    [TestMethod]
    public void SpHelp_ScalarFunction_LeadsWithTheReturnValueRow()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f1(@a int) returns int as begin return @a + 1 end");
        var sets = ResultSets(sim, "exec sp_help 'dbo.f1'");
        AreEqual("scalar function", sets[0].Rows[0][2]);
        // The return value is an empty-named parameter at order 0.
        CollectionAssert.AreEqual(new object?[] { "", "int", (short)4, 10, 0, 0, null }, sets[1].Rows[0]);
        AreEqual("@a", sets[1].Rows[1][0]);
    }

    [TestMethod]
    public void SpHelp_InlineTableValuedFunction_HasColumnsButNoIdentitySet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f1(@n int) returns table as return select @n as n");
        var sets = ResultSets(sim, "exec sp_help 'dbo.f1'");
        AreEqual("inline function", sets[0].Rows[0][2]);
        // Object info, columns, parameters — real gates the identity /
        // rowguidcol pair on type in ('S ','U ','V ','TF').
        HasCount(3, sets);
        AreEqual("n", sets[1].Rows[0][0]);
        AreEqual("@n", sets[2].Rows[0][0]);
    }

    [TestMethod]
    public void SpHelp_View_EmitsTheConstraintAndIndexMessages()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v1 as select 1 as x");
        var (sets, errors) = RunHelp(sim, "exec sp_help 'dbo.v1'");
        AreEqual("view", sets[0].Rows[0][2]);
        HasCount(4, sets);
        Contains(15469, errors.ConvertAll(e => e.Number));
    }

    [TestMethod]
    public void SpHelp_Table_ListsTheSchemaBoundViewsReferencingIt()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null primary key, k int not null)",
            "create view dbo.iv with schemabinding as select id, k from dbo.b",
            "create unique clustered index IX_iv on dbo.iv(id)");
        var sets = ResultSets(sim, "exec sp_help 'dbo.b'");
        HasCount(8, sets);
        AreEqual("iv", sets[7].Rows[0][0]);
        // A table with no dependent view falls to the severity-10 message,
        // which coalesces with the batch's other sev-10 texts.
        var withoutView = new Simulation();
        withoutView.ExecuteBatches("create table dbo.t (a int)");
        Assert.Contains("No views with schema binding reference table 't'.",
            RunHelp(withoutView, "exec sp_help 't'").Errors.Single().Message);
    }

    [TestMethod]
    public void SpHelp_MissingObject_Msg15009()
        => new Simulation().AssertSqlError("exec sp_help 'dbo.nope'", 15009,
            "The object 'dbo.nope' does not exist in database 'simulated' or is invalid for this operation.");

    [TestMethod]
    public void SpHelp_Constraint_ReportsItAsAnObject()
    {
        var sets = ResultSets(NewFixture(), "exec sp_help 'CK_amt'");
        HasCount(1, sets);
        AreEqual("CK_amt", sets[0].Rows[0][0]);
        AreEqual("check cns", sets[0].Rows[0][2]);
    }

    // ===== sp_help: type and no-argument forms =====

    [TestMethod]
    public void SpHelp_AliasType()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.MyInt from decimal(9, 3) not null", "create type dbo.MyStr from nvarchar(40) null");
        CollectionAssert.AreEqual(
            new[] { "Type_name", "Storage_type", "Length", "Prec", "Scale", "Nullable", "Default_name", "Rule_name", "Collation" },
            ColumnNames(sim, "exec sp_help 'MyInt'"));
        CollectionAssert.AreEqual(
            new object?[] { "MyInt", "decimal", (short)5, 9, 3, "no", "none", "none", null },
            ResultSets(sim, "exec sp_help 'MyInt'")[0].Rows[0]);
        CollectionAssert.AreEqual(
            new object?[]
            {
                "MyStr", "nvarchar", (short)80, 40, null, "yes", "none", "none",
                "SQL_Latin1_General_CP1_CI_AS",
            },
            ResultSets(sim, "exec sp_help 'MyStr'")[0].Rows[0]);
    }

    [TestMethod]
    public void SpHelp_TableType()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.TT as table (a int)");
        CollectionAssert.AreEqual(
            new object?[] { "TT", "table type", (short)-1, 0, null, "no", "none", "none", null },
            ResultSets(sim, "exec sp_help 'TT'")[0].Rows[0]);
    }

    [TestMethod]
    public void SpHelp_NoArgument_ListsObjectsThenUserTypes()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int constraint CK_a check (a > 0))",
            "create view dbo.v1 as select 1 as x",
            "create type dbo.MyInt from int");
        var sets = ResultSets(sim, "exec sp_help");
        HasCount(2, sets);
        CollectionAssert.AreEqual(
            new[] { "Name", "Owner", "Object_type" }, ColumnNames(sim, "exec sp_help"));
        // Owner ascending, object type DESCENDING, name ascending.
        CollectionAssert.AreEqual(
            new[] { ("v1", "view"), ("t", "user table"), ("CK_a", "check cns") },
            sets[0].Rows.ConvertAll(r => ((string?)r[0], (string?)r[2])).ToArray());
        AreEqual("MyInt", sets[1].Rows[0][0]);
        AreEqual("int", sets[1].Rows[0][1]);
    }

    // ===== sp_helpindex =====

    [TestMethod]
    public void SpHelpIndex_DescribesEachIndex()
    {
        var sim = NewFixture();
        CollectionAssert.AreEqual(
            new[] { "index_name", "index_description", "index_keys" },
            ColumnNames(sim, "exec sp_helpindex 't_full'"));
        var rows = ResultSets(sim, "exec sp_helpindex 't_full'")[0].Rows;
        CollectionAssert.AreEqual(
            new object?[] { "PK_t_full", "clustered, unique, primary key located on PRIMARY", "id" },
            rows[0]);
        // A descending key carries real's "(-)" marker; the INCLUDE column
        // never appears in index_keys.
        CollectionAssert.AreEqual(
            new object?[] { "UX_t_full_name", "nonclustered, unique located on PRIMARY", "name(-)" },
            rows[1]);
    }

    [TestMethod]
    public void SpHelpIndex_UniqueConstraintReportsTheUniqueKeyClause()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int not null constraint UQ_a unique nonclustered)");
        AreEqual("nonclustered, unique, unique key located on PRIMARY",
            ResultSets(sim, "exec sp_helpindex 't'")[0].Rows[0][1]);
    }

    [TestMethod]
    public void SpHelpIndex_NoIndexes_Msg15472AndNoResultSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int)");
        var (sets, errors) = RunHelp(sim, "exec sp_helpindex 't'");
        IsEmpty(sets);
        var error = errors.Single();
        AreEqual(15472, error.Number);
        AreEqual("The object 't' does not have any indexes, or you do not have permissions.", error.Message);
    }

    // ===== sp_helpconstraint =====

    [TestMethod]
    public void SpHelpConstraint_EchoesTheObjectNameThenTheConstraints()
    {
        var sim = NewFixture();
        var sets = ResultSets(sim, "exec sp_helpconstraint 'dbo.t_full'");
        HasCount(2, sets);
        AreEqual("dbo.t_full", sets[0].Rows[0][0]);
        CollectionAssert.AreEqual(
            new[] { "constraint_type", "constraint_name", "delete_action", "update_action", "status_enabled", "status_for_replication", "constraint_keys" },
            ColumnNames(sim, "exec sp_helpconstraint 'dbo.t_full', 'nomsg'"));
        CollectionAssert.AreEqual(
            new object?[]
            {
                "CHECK on column amount", "CK_amt", "(n/a)", "(n/a)", "Enabled",
                "Is_For_Replication", "(amount >= 0)",
            },
            sets[1].Rows[0]);
        CollectionAssert.AreEqual(
            new object?[] { "DEFAULT on column g", "DF_g", "(n/a)", "(n/a)", "(n/a)", "(n/a)", "(newid())" },
            sets[1].Rows[1]);
        CollectionAssert.AreEqual(
            new object?[] { "PRIMARY KEY (clustered)", "PK_t_full", "(n/a)", "(n/a)", "(n/a)", "(n/a)", "id" },
            sets[1].Rows[2]);
    }

    [TestMethod]
    public void SpHelpConstraint_NoMsg_SuppressesTheObjectNameSet()
        => HasCount(1, ResultSets(NewFixture(), "exec sp_helpconstraint 'dbo.t_full', 'nomsg'"));

    [TestMethod]
    public void SpHelpConstraint_ForeignKey_EmitsTheDeclarationAndReferencesRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table dbo.p (pid int not null constraint PK_p primary key);
            create table dbo.c (
                cid int not null primary key,
                pid int null constraint FK_c_p foreign key references dbo.p(pid)
                    on delete cascade on update set null
            );
            """);
        var rows = ResultSets(sim, "exec sp_helpconstraint 'dbo.c', 'nomsg'")[0].Rows;
        CollectionAssert.AreEqual(
            new object?[] { "FOREIGN KEY", "FK_c_p", "Cascade", "Set Null", "Enabled", "Is_For_Replication", "pid" },
            rows[0]);
        // The continuation row is entirely blank but for the REFERENCES text.
        CollectionAssert.AreEqual(
            new object?[] { " ", " ", " ", " ", " ", " ", "REFERENCES simulated.dbo.p (pid)" },
            rows[1]);
        // The referenced table lists the referencing key in its own trailing set.
        var parentSets = ResultSets(sim, "exec sp_helpconstraint 'dbo.p', 'nomsg'");
        AreEqual("simulated.dbo.c: FK_c_p", parentSets[1].Rows[0][0]);
    }

    [TestMethod]
    public void SpHelpConstraint_DisabledCheck_ReportsDisabled()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table dbo.t (v int null constraint CK_v check (v > 0));
            alter table dbo.t nocheck constraint CK_v;
            """);
        AreEqual("Disabled", ResultSets(sim, "exec sp_helpconstraint 't', 'nomsg'")[0].Rows[0][4]);
    }

    [TestMethod]
    public void SpHelpConstraint_NoConstraints_Msg15469And15470()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int)");
        var (sets, errors) = RunHelp(sim, "exec sp_helpconstraint 't', 'nomsg'");
        IsEmpty(sets);
        // Both messages land in one coalesced info event carrying the first
        // contributor's number — the simulator's batch-wide PRINT / sev-10
        // coalescing semantic.
        var error = errors.Single();
        AreEqual(15469, error.Number);
        Assert.Contains("No constraints are defined on object 't'", error.Message);
        Assert.Contains("No foreign keys reference table 't'", error.Message);
    }

    [TestMethod]
    public void SpHelpConstraint_TableLevelCheck_ReportsTheTableLevelWording()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int, b int, constraint CK_ab check (a < b))");
        AreEqual("CHECK Table Level ", ResultSets(sim, "exec sp_helpconstraint 't', 'nomsg'")[0].Rows[0][0]);
    }
}
