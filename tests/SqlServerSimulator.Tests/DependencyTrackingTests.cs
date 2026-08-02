using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The expression-dependency surfaces: <c>sys.sql_expression_dependencies</c>,
/// <c>sys.dm_sql_referencing_entities</c>, <c>sys.dm_sql_referenced_entities</c>
/// and <c>sp_depends</c>. Every asserted shape, flag and message is
/// probe-confirmed against SQL Server 2025 (2026-08-02).
/// </summary>
[TestClass]
public sealed class DependencyTrackingTests
{
    /// <summary>
    /// One object of every referencing kind over one table, matching the probe
    /// matrix: plain / star / schema-bound views, the three function kinds, a
    /// procedure that reads and one that writes, an <c>EXEC</c> caller in both
    /// name shapes, a trigger, a synonym reader, a sequence reader, a dynamic-SQL
    /// body, a missing-object body, and a table carrying a computed column plus
    /// CHECK and DEFAULT constraints.
    /// </summary>
    private static Simulation Fixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int, b varchar(20))",
            "create table u (id int primary key, z int)",
            "create sequence sq as int start with 1",
            "create synonym syn for dbo.t",
            "create view v as select id, a from dbo.t where b = 'x'",
            "create view v_star as select * from dbo.t",
            "create view v_sb with schemabinding as select id, a from dbo.t",
            "create function f(@p int) returns int as begin return (select max(a) from dbo.t where id = @p) end",
            "create function itvf(@p int) returns table as return (select id, b from dbo.t where id = @p)",
            "create function mstvf(@p int) returns @r table (id int) as begin insert @r select id from dbo.t; return end",
            "create procedure p_read as select a from dbo.t",
            "create procedure p_write as update dbo.t set a = 5 where b = 'q'",
            "create procedure p_call as exec dbo.p_read",
            "create procedure p_call1 as exec p_read",
            "create procedure p_dyn as exec('select a from dbo.t')",
            "create procedure p_syn as select id from dbo.syn",
            "create procedure p_seq as select next value for dbo.sq",
            "create procedure p_missing as select * from dbo.nosuchtable",
            "create trigger trg on dbo.t after insert as select z from dbo.u",
            "create table cc (id int, a int, calc as a * 2, constraint ck_cc check (a > 0))");
        return sim;
    }

    private static List<Dictionary<string, object?>> Rows(Simulation sim, string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var reader = sim.ExecuteReader(sql);
        do
        {
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        } while (reader.NextResult());

        return rows;
    }

    /// <summary>Reads a bit cell from a row the DMVs and the catalog view return.</summary>
    private static bool Flag(Dictionary<string, object?> row, string column) => (bool)row[column]!;

    /// <summary>The <c>sys.sql_expression_dependencies</c> rows one referencing object contributes.</summary>
    private static List<Dictionary<string, object?>> Deps(Simulation sim, string referencingName) => Rows(sim, $"""
        select referenced_entity_name, referenced_schema_name, referenced_database_name, referenced_server_name,
               referenced_id, referenced_minor_id, referenced_class, referenced_class_desc,
               referencing_minor_id, referencing_class, referencing_class_desc,
               is_schema_bound_reference, is_caller_dependent, is_ambiguous
        from sys.sql_expression_dependencies
        where referencing_id = object_id('dbo.{referencingName}')
        order by referenced_entity_name, referenced_minor_id
        """);

    // ---- sys.sql_expression_dependencies: the referencing-kind matrix ----

    [TestMethod]
    [DataRow("v")]
    [DataRow("v_star")]
    [DataRow("f")]
    [DataRow("itvf")]
    [DataRow("mstvf")]
    [DataRow("p_read")]
    [DataRow("p_write")]
    public void EveryModuleKind_RecordsOneObjectRowForTheTableItReads(string module)
    {
        var sim = Fixture();
        var rows = Deps(sim, module);
        HasCount(1, rows);
        AreEqual("t", rows[0]["referenced_entity_name"]);
        AreEqual("dbo", rows[0]["referenced_schema_name"]);
        AreEqual(0, rows[0]["referenced_minor_id"]);
        AreEqual((byte)1, rows[0]["referenced_class"]);
        AreEqual("OBJECT_OR_COLUMN", rows[0]["referenced_class_desc"]);
        IsFalse(Flag(rows[0], "is_schema_bound_reference"));
        IsNotNull(rows[0]["referenced_id"]);
    }

    /// <summary>
    /// Column rows exist in the catalog view only for a schema-bound reference:
    /// the plain view reports one row and the schema-bound one reports the
    /// object row plus a row per bound column.
    /// </summary>
    [TestMethod]
    public void SchemaBoundView_AddsAColumnRowPerBoundColumn()
    {
        var sim = Fixture();
        var rows = Deps(sim, "v_sb");
        HasCount(3, rows);
        CollectionAssert.AreEqual(new object?[] { 0, 1, 2 }, rows.ConvertAll(r => r["referenced_minor_id"]));
        foreach (var row in rows)
        {
            AreEqual("t", row["referenced_entity_name"]);
            IsTrue(Flag(row, "is_schema_bound_reference"));
        }
    }

    [TestMethod]
    public void PlainView_ReportsNoColumnRows()
    {
        var sim = Fixture();
        var rows = Deps(sim, "v");
        HasCount(1, rows);
        AreEqual(0, rows[0]["referenced_minor_id"]);
    }

    /// <summary>
    /// A computed column references its own table's columns without an
    /// object-level reference to it, so its rows are column rows only — under
    /// the table's object id with the computed column's own <c>column_id</c> as
    /// the referencing minor id.
    /// </summary>
    [TestMethod]
    public void ComputedColumn_ReportsAColumnRowUnderItsOwnColumnId()
    {
        var sim = Fixture();
        var rows = Rows(sim, """
            select referencing_minor_id, referenced_entity_name, referenced_minor_id, is_schema_bound_reference
            from sys.sql_expression_dependencies
            where referencing_id = object_id('dbo.cc') order by referenced_minor_id
            """);
        HasCount(1, rows);
        AreEqual(3, rows[0]["referencing_minor_id"]);
        AreEqual("cc", rows[0]["referenced_entity_name"]);
        AreEqual(2, rows[0]["referenced_minor_id"]);
        IsTrue(Flag(rows[0], "is_schema_bound_reference"));
    }

    [TestMethod]
    public void CheckConstraint_ReportsAColumnRowUnderItsOwnObjectId()
    {
        var sim = Fixture();
        var rows = Rows(sim, """
            select referencing_minor_id, referenced_entity_name, referenced_minor_id, is_schema_bound_reference
            from sys.sql_expression_dependencies
            where referencing_id = object_id('dbo.ck_cc')
            """);
        HasCount(1, rows);
        AreEqual(0, rows[0]["referencing_minor_id"]);
        AreEqual("cc", rows[0]["referenced_entity_name"]);
        AreEqual(2, rows[0]["referenced_minor_id"]);
        IsTrue(Flag(rows[0], "is_schema_bound_reference"));
    }

    /// <summary>
    /// A DEFAULT expression calling a function records that function as an
    /// object reference, schema bound like every constraint expression.
    /// </summary>
    [TestMethod]
    public void DefaultConstraintCallingAFunction_RecordsASchemaBoundObjectRow()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create table d (id int, dv int constraint df_d default (dbo.f(1)))");
        var rows = Rows(sim, """
            select referenced_entity_name, referenced_minor_id, is_schema_bound_reference
            from sys.sql_expression_dependencies where referencing_id = object_id('dbo.df_d')
            """);
        HasCount(1, rows);
        AreEqual("f", rows[0]["referenced_entity_name"]);
        AreEqual(0, rows[0]["referenced_minor_id"]);
        IsTrue(Flag(rows[0], "is_schema_bound_reference"));
    }

    [TestMethod]
    public void Trigger_RecordsTheTableItsBodyReads()
    {
        var sim = Fixture();
        var rows = Deps(sim, "trg");
        HasCount(1, rows);
        AreEqual("u", rows[0]["referenced_entity_name"]);
    }

    [TestMethod]
    public void SynonymReference_RecordsTheSynonymRatherThanItsBase()
    {
        var sim = Fixture();
        var rows = Deps(sim, "p_syn");
        HasCount(1, rows);
        AreEqual("syn", rows[0]["referenced_entity_name"]);
        AreEqual(sim.ExecuteScalar("select object_id('dbo.syn')"), rows[0]["referenced_id"]);
    }

    [TestMethod]
    public void NextValueFor_RecordsTheSequence()
    {
        var sim = Fixture();
        var rows = Deps(sim, "p_seq");
        HasCount(1, rows);
        AreEqual("sq", rows[0]["referenced_entity_name"]);
        IsNotNull(rows[0]["referenced_id"]);
    }

    /// <summary>
    /// A two-part <c>EXEC</c> name resolves; a one-part one is
    /// <c>is_caller_dependent</c> with a NULL schema and a NULL id, even though
    /// a procedure of that name exists.
    /// </summary>
    [TestMethod]
    public void QualifiedExec_Resolves_WhileBareExecIsCallerDependent()
    {
        var sim = Fixture();
        var qualified = Deps(sim, "p_call");
        HasCount(1, qualified);
        AreEqual("p_read", qualified[0]["referenced_entity_name"]);
        AreEqual("dbo", qualified[0]["referenced_schema_name"]);
        IsFalse(Flag(qualified[0], "is_caller_dependent"));
        IsNotNull(qualified[0]["referenced_id"]);

        var bare = Deps(sim, "p_call1");
        HasCount(1, bare);
        AreEqual("p_read", bare[0]["referenced_entity_name"]);
        IsNull(bare[0]["referenced_schema_name"]);
        IsTrue(Flag(bare[0], "is_caller_dependent"));
        IsNull(bare[0]["referenced_id"]);
    }

    /// <summary>
    /// An unqualified table reference keeps a NULL schema name but still
    /// resolves its id through the default schema — probe-confirmed, and the
    /// opposite of the <c>EXEC</c> rule above.
    /// </summary>
    [TestMethod]
    public void UnqualifiedTableReference_HasANullSchemaButAResolvedId()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_bare as select a from t");
        var rows = Deps(sim, "p_bare");
        HasCount(1, rows);
        IsNull(rows[0]["referenced_schema_name"]);
        AreEqual(sim.ExecuteScalar("select object_id('dbo.t')"), rows[0]["referenced_id"]);
    }

    [TestMethod]
    public void MissingObject_KeepsItsNameWithANullId()
    {
        var sim = Fixture();
        var rows = Deps(sim, "p_missing");
        HasCount(1, rows);
        AreEqual("nosuchtable", rows[0]["referenced_entity_name"]);
        AreEqual("dbo", rows[0]["referenced_schema_name"]);
        IsNull(rows[0]["referenced_id"]);
    }

    [TestMethod]
    public void DynamicSql_RecordsNothing() => IsEmpty(Deps(Fixture(), "p_dyn"));

    [TestMethod]
    public void TempTablesAndTableVariablesAndSystemViews_RecordNothing()
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create procedure p_temp as select 1 from #nope",
            "create procedure p_var as declare @x table (i int); select i from @x",
            "create procedure p_sys as select name from sys.tables");
        IsEmpty(Deps(sim, "p_temp"));
        IsEmpty(Deps(sim, "p_var"));
        IsEmpty(Deps(sim, "p_sys"));
    }

    /// <summary>
    /// A three-part reference into another database keeps the database name and
    /// resolves to no id — ids are database-local, so real reports NULL.
    /// </summary>
    [TestMethod]
    public void CrossDatabaseReference_KeepsTheDatabaseNameAndReportsANullId()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_xdb as select x from master.dbo.other");
        var rows = Deps(sim, "p_xdb");
        HasCount(1, rows);
        AreEqual("master", rows[0]["referenced_database_name"]);
        AreEqual("dbo", rows[0]["referenced_schema_name"]);
        AreEqual("other", rows[0]["referenced_entity_name"]);
        IsNull(rows[0]["referenced_id"]);
    }

    /// <summary>
    /// A two-part call whose qualifier names no schema is real's
    /// <c>is_ambiguous</c>: the binder can't tell <c>schema.function()</c> from
    /// an XML or UDT method on a column named <c>q</c>.
    /// </summary>
    [TestMethod]
    public void MethodCallOnANonSchemaQualifier_IsAmbiguous()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_amb as select doc.value('(/a)[1]', 'int') from dbo.t");
        var rows = Rows(sim, """
            select referenced_schema_name, referenced_entity_name, referenced_id, is_ambiguous
            from sys.sql_expression_dependencies
            where referencing_id = object_id('dbo.p_amb') and is_ambiguous = 1
            """);
        HasCount(1, rows);
        AreEqual("doc", rows[0]["referenced_schema_name"]);
        AreEqual("value", rows[0]["referenced_entity_name"]);
        IsNull(rows[0]["referenced_id"]);
    }

    /// <summary>
    /// A table-valued parameter is a <c>referenced_class</c> 6 reference off
    /// the parameter declaration, carrying the type's <c>user_type_id</c> and
    /// no schema binding.
    /// </summary>
    [TestMethod]
    public void TableValuedParameter_IsATypeClassReference()
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create type tt as table (a int)",
            "create procedure p_tvp (@tv dbo.tt readonly) as select a from @tv");
        var rows = Rows(sim, """
            select referenced_entity_name, referenced_class, referenced_class_desc, referenced_id, is_schema_bound_reference
            from sys.sql_expression_dependencies where referencing_id = object_id('dbo.p_tvp')
            """);
        HasCount(1, rows);
        AreEqual("tt", rows[0]["referenced_entity_name"]);
        AreEqual((byte)6, rows[0]["referenced_class"]);
        AreEqual("TYPE", rows[0]["referenced_class_desc"]);
        AreEqual(sim.ExecuteScalar("select user_type_id from sys.types where name = 'tt'"), rows[0]["referenced_id"]);
        IsFalse(Flag(rows[0], "is_schema_bound_reference"));
    }

    /// <summary>A database-scoped DDL trigger records under <c>referencing_class</c> 12.</summary>
    [TestMethod]
    public void DatabaseDdlTrigger_RecordsUnderTheDdlTriggerClass()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create trigger ddl_trg on database for create_table as select z from dbo.u");
        var rows = Rows(sim, """
            select referencing_class, referencing_class_desc, referenced_entity_name
            from sys.sql_expression_dependencies where referencing_class = 12
            """);
        HasCount(1, rows);
        AreEqual((byte)12, rows[0]["referencing_class"]);
        AreEqual("DATABASE_DDL_TRIGGER", rows[0]["referencing_class_desc"]);
        AreEqual("u", rows[0]["referenced_entity_name"]);
    }

    // ---- refresh semantics ----

    [TestMethod]
    public void AlterOfAModule_RefreshesItsRows()
    {
        var sim = Fixture();
        AreEqual("t", Deps(sim, "v")[0]["referenced_entity_name"]);
        sim.ExecuteBatches("alter view v as select id from dbo.u");
        var rows = Deps(sim, "v");
        HasCount(1, rows);
        AreEqual("u", rows[0]["referenced_entity_name"]);
    }

    /// <summary>
    /// A DROP of the referenced object leaves the row with its name and a NULL
    /// id; recreating an object of that name brings the id back. Both
    /// probe-confirmed, and both are what "the store holds names" means.
    /// </summary>
    [TestMethod]
    public void DropOfTheReferencedObject_NullsTheIdAndRecreateRestoresIt()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table gone (id int)",
            "create view vg as select id from dbo.gone");
        IsNotNull(Deps(sim, "vg")[0]["referenced_id"]);

        sim.ExecuteBatches("drop table gone");
        var afterDrop = Deps(sim, "vg");
        HasCount(1, afterDrop);
        AreEqual("gone", afterDrop[0]["referenced_entity_name"]);
        IsNull(afterDrop[0]["referenced_id"]);

        sim.ExecuteBatches("create table gone (id int)");
        IsNotNull(Deps(sim, "vg")[0]["referenced_id"]);
    }

    /// <summary>
    /// <c>sp_rename</c> of a referenced table leaves the row naming the old
    /// name with a NULL id — the reference is by name, so a rename breaks it
    /// exactly as real reports.
    /// </summary>
    [TestMethod]
    public void RenameOfTheReferencedTable_LeavesTheStaleNameWithANullId()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table before (id int)",
            "create view vr as select id from dbo.before",
            "exec sp_rename 'dbo.before', 'after'");
        var rows = Deps(sim, "vr");
        HasCount(1, rows);
        AreEqual("before", rows[0]["referenced_entity_name"]);
        IsNull(rows[0]["referenced_id"]);
        IsEmpty(Rows(sim, "select * from sys.dm_sql_referencing_entities('dbo.after', 'OBJECT')"));
    }

    // ---- sys.dm_sql_referencing_entities ----

    [TestMethod]
    public void ReferencingEntities_ListsEveryDirectReferencer()
    {
        var sim = Fixture();
        var names = Rows(sim, """
            select referencing_schema_name, referencing_entity_name, referencing_class, referencing_class_desc, is_caller_dependent
            from sys.dm_sql_referencing_entities('dbo.t', 'OBJECT') order by referencing_entity_name
            """).ConvertAll(r => (string)r["referencing_entity_name"]!);
        CollectionAssert.AreEquivalent(
            new[] { "v", "v_star", "v_sb", "f", "itvf", "mstvf", "p_read", "p_write" },
            names);
    }

    /// <summary>
    /// Real reports direct referencers only — a procedure that calls a
    /// procedure that reads the table isn't listed against the table.
    /// </summary>
    [TestMethod]
    public void ReferencingEntities_IsNotTransitive()
    {
        var sim = Fixture();
        var names = Rows(sim, "select referencing_entity_name from sys.dm_sql_referencing_entities('dbo.t', 'OBJECT')")
            .ConvertAll(r => (string)r["referencing_entity_name"]!);
        DoesNotContain("p_call", names);
    }

    /// <summary>
    /// The <c>TYPE</c> class addresses a table type by its <c>user_type_id</c>,
    /// so the procedure declaring it as a TVP parameter answers there.
    /// </summary>
    [TestMethod]
    public void ReferencingEntities_TypeClass_FindsTheTvpDeclaringProcedure()
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create type tt as table (a int)",
            "create procedure p_tvp (@tv dbo.tt readonly) as select a from @tv");
        var rows = Rows(sim, "select referencing_entity_name from sys.dm_sql_referencing_entities('dbo.tt', 'TYPE')");
        HasCount(1, rows);
        AreEqual("p_tvp", rows[0]["referencing_entity_name"]);
    }

    [TestMethod]
    public void ReferencingEntities_ReportsSixColumns()
    {
        var sim = Fixture();
        using var reader = sim.ExecuteReader("select * from sys.dm_sql_referencing_entities('dbo.t', 'OBJECT')");
        AreEqual(6, reader.FieldCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "referencing_schema_name", "referencing_entity_name", "referencing_id",
                "referencing_class", "referencing_class_desc", "is_caller_dependent",
            },
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
    }

    /// <summary>
    /// Every miss is silent: an unrecognized class string, a name that resolves
    /// to nothing, and a one-part name all yield an empty rowset rather than an
    /// error (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("select * from sys.dm_sql_referencing_entities('dbo.t', 'BOGUS')")]
    [DataRow("select * from sys.dm_sql_referencing_entities('dbo.nosuch', 'OBJECT')")]
    [DataRow("select * from sys.dm_sql_referencing_entities('t', 'OBJECT')")]
    [DataRow("select * from sys.dm_sql_referencing_entities(null, 'OBJECT')")]
    [DataRow("select * from sys.dm_sql_referenced_entities('dbo.t', 'OBJECT')")]
    [DataRow("select * from sys.dm_sql_referenced_entities('v', 'OBJECT')")]
    public void DependencyDmvs_ReportNoRowsRatherThanAnErrorOnAMiss(string sql) => IsEmpty(Rows(Fixture(), sql));

    // ---- sys.dm_sql_referenced_entities ----

    /// <summary>
    /// Unlike the catalog view, the DMV reports column rows for every
    /// referencing kind — a plain view included — with the object row first.
    /// A column named only in the WHERE still reads as selected.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_ReportsColumnRowsForAPlainView()
    {
        var sim = Fixture();
        var rows = Rows(sim, """
            select referenced_entity_name, referenced_minor_id, referenced_minor_name,
                   is_selected, is_updated, is_select_all, is_all_columns_found, is_insert_all, is_incomplete
            from sys.dm_sql_referenced_entities('dbo.v', 'OBJECT')
            """);
        HasCount(4, rows);
        CollectionAssert.AreEqual(new object?[] { 0, 1, 2, 3 }, rows.ConvertAll(r => r["referenced_minor_id"]));
        CollectionAssert.AreEqual(new object?[] { null, "id", "a", "b" }, rows.ConvertAll(r => r["referenced_minor_name"]));
        foreach (var row in rows)
        {
            IsTrue(Flag(row, "is_selected"));
            IsFalse(Flag(row, "is_updated"));
            IsTrue(Flag(row, "is_all_columns_found"));
            IsFalse(Flag(row, "is_incomplete"));
        }
    }

    /// <summary>
    /// <c>SELECT *</c> sets <c>is_select_all</c> on the object row and on every
    /// column, and clears <c>is_selected</c> even for a column the WHERE names
    /// separately — probe-confirmed.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_SelectStar_SetsSelectAllAndClearsSelected()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_star as select * from dbo.t where a > 1");
        var rows = Rows(sim, """
            select referenced_minor_id, is_selected, is_select_all
            from sys.dm_sql_referenced_entities('dbo.p_star', 'OBJECT')
            """);
        HasCount(4, rows);
        foreach (var row in rows)
        {
            IsFalse(Flag(row, "is_selected"));
            IsTrue(Flag(row, "is_select_all"));
        }
    }

    /// <summary>
    /// The object row's flags follow the reference position, not the columns: a
    /// body whose only mention of the table is an UPDATE reports the object as
    /// updated and not selected, even though its WHERE reads a column.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_UpdateTarget_SplitsWrittenAndReadColumns()
    {
        var sim = Fixture();
        var rows = Rows(sim, """
            select referenced_minor_id, referenced_minor_name, is_selected, is_updated
            from sys.dm_sql_referenced_entities('dbo.p_write', 'OBJECT')
            """);
        HasCount(3, rows);
        AreEqual(0, rows[0]["referenced_minor_id"]);
        IsFalse(Flag(rows[0], "is_selected"));
        IsTrue(Flag(rows[0], "is_updated"));

        AreEqual("a", rows[1]["referenced_minor_name"]);
        IsFalse(Flag(rows[1], "is_selected"));
        IsTrue(Flag(rows[1], "is_updated"));

        AreEqual("b", rows[2]["referenced_minor_name"]);
        IsTrue(Flag(rows[2], "is_selected"));
        IsFalse(Flag(rows[2], "is_updated"));
    }

    /// <summary>
    /// A body that both reads and writes one table reports the object row with
    /// both flags — the counterpart to the update-only case above.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_ReadAndWrite_SetsBothObjectFlags()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_both as select b from dbo.t; update dbo.t set a = 1");
        var rows = Rows(sim, """
            select referenced_minor_id, referenced_minor_name, is_selected, is_updated
            from sys.dm_sql_referenced_entities('dbo.p_both', 'OBJECT')
            """);
        HasCount(3, rows);
        IsTrue(Flag(rows[0], "is_selected"));
        IsTrue(Flag(rows[0], "is_updated"));
        AreEqual("a", rows[1]["referenced_minor_name"]);
        IsTrue(Flag(rows[1], "is_updated"));
        AreEqual("b", rows[2]["referenced_minor_name"]);
        IsTrue(Flag(rows[2], "is_selected"));
    }

    /// <summary>
    /// An <c>INSERT</c> with no column list is <c>is_insert_all</c> and carries
    /// no column rows; one with a column list marks those columns updated.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_InsertAll_HasNoColumnRows()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_ins as insert dbo.t values (1, 2, 'x'); insert dbo.u (id) values (9)");
        var rows = Rows(sim, """
            select referenced_entity_name, referenced_minor_id, referenced_minor_name, is_updated, is_insert_all
            from sys.dm_sql_referenced_entities('dbo.p_ins', 'OBJECT')
            """);
        HasCount(3, rows);
        AreEqual("t", rows[0]["referenced_entity_name"]);
        AreEqual(0, rows[0]["referenced_minor_id"]);
        IsTrue(Flag(rows[0], "is_updated"));
        IsTrue(Flag(rows[0], "is_insert_all"));

        AreEqual("u", rows[1]["referenced_entity_name"]);
        IsFalse(Flag(rows[1], "is_insert_all"));
        AreEqual("id", rows[2]["referenced_minor_name"]);
        IsTrue(Flag(rows[2], "is_updated"));
    }

    /// <summary>
    /// A qualified column reference lands on the source its qualifier names and
    /// nowhere else, so a join between two tables that share an <c>id</c>
    /// column reports each table's own read columns — probe-confirmed exact.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_QualifiedColumns_LandOnTheirOwnSource()
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create table j (id int, aid int, tag varchar(20))",
            "create procedure p_join as select t.a, j.tag from dbo.t as t inner join dbo.j as j on t.id = j.aid where t.b > ''");
        var rows = Rows(sim, """
            select referenced_entity_name, referenced_minor_id, isnull(referenced_minor_name, '-') mn
            from sys.dm_sql_referenced_entities('dbo.p_join', 'OBJECT')
            """);
        CollectionAssert.AreEqual(
            new object?[] { "-", "id", "a", "b", "-", "aid", "tag" },
            rows.ConvertAll(r => r["mn"]));
    }

    /// <summary>
    /// An <c>APPLY</c> right side is part of the same statement, so its
    /// correlated predicate attaches to the outer source while its own FROM
    /// contributes a second referenced object.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_CrossApply_ReportsBothSourcesWithTheirOwnColumns()
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create table j (id int, aid int, tag varchar(20))",
            "create procedure p_apply as select ta.id, x.tag from dbo.t ta cross apply (select top 1 tag from dbo.j where aid = ta.id) x");
        var rows = Rows(sim, """
            select referenced_entity_name ent, isnull(referenced_minor_name, '-') mn
            from sys.dm_sql_referenced_entities('dbo.p_apply', 'OBJECT')
            """);
        CollectionAssert.AreEqual(new object?[] { "t", "t", "j", "j", "j" }, rows.ConvertAll(r => r["ent"]));
        CollectionAssert.AreEqual(new object?[] { "-", "id", "-", "aid", "tag" }, rows.ConvertAll(r => r["mn"]));
    }

    /// <summary>A CTE name is not an object; only what the CTE's own body reads is recorded.</summary>
    [TestMethod]
    public void ReferencedEntities_Cte_RecordsOnlyTheCteBodysSource()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_cte as with c as (select id, a from dbo.t) select id from c where a > 1");
        var rows = Rows(sim, """
            select referenced_entity_name ent, isnull(referenced_minor_name, '-') mn
            from sys.dm_sql_referenced_entities('dbo.p_cte', 'OBJECT')
            """);
        CollectionAssert.AreEqual(new object?[] { "t", "t", "t" }, rows.ConvertAll(r => r["ent"]));
        CollectionAssert.AreEqual(new object?[] { "-", "id", "a" }, rows.ConvertAll(r => r["mn"]));
    }

    /// <summary>
    /// A MERGE reports its target as updated and its USING source as selected,
    /// with the WHEN clauses' SET list and INSERT column list landing on the
    /// target rather than on both sources.
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_Merge_SplitsTargetAndUsingSource()
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create table j (id int, aid int, tag varchar(20))",
            """
            create procedure p_merge as
            merge dbo.t as tgt using dbo.j as src on tgt.id = src.aid
            when matched then update set tgt.b = src.tag
            when not matched then insert (id, b) values (src.id, src.tag);
            """);
        var rows = Rows(sim, """
            select referenced_entity_name ent, isnull(referenced_minor_name, '-') mn, is_selected, is_updated
            from sys.dm_sql_referenced_entities('dbo.p_merge', 'OBJECT')
            """);
        var target = rows.FindAll(r => (string)r["ent"]! == "t");
        IsFalse(Flag(target[0], "is_selected"));
        IsTrue(Flag(target[0], "is_updated"));
        IsTrue(Flag(target.Single(r => (string)r["mn"]! == "b"), "is_updated"));

        var source = rows.FindAll(r => (string)r["ent"]! == "j");
        IsTrue(Flag(source[0], "is_selected"));
        IsFalse(Flag(source[0], "is_updated"));
        CollectionAssert.AreEqual(new object?[] { "-", "id", "aid", "tag" }, source.ConvertAll(r => r["mn"]));
        foreach (var row in source)
            IsFalse(Flag(row, "is_updated"));
    }

    [TestMethod]
    public void ReferencedEntities_ReportsSeventeenColumns()
    {
        var sim = Fixture();
        using var reader = sim.ExecuteReader("select * from sys.dm_sql_referenced_entities('dbo.v', 'OBJECT')");
        AreEqual(17, reader.FieldCount);
        AreEqual("referenced_server_name", reader.GetName(0));
        AreEqual("referenced_minor_name", reader.GetName(6));
        AreEqual("is_incomplete", reader.GetName(16));
    }

    /// <summary>
    /// A reference the analysis can't resolve makes the DMV raise
    /// <strong>Msg 2020</strong>, verbatim including real's double space before
    /// "Before rerunning".
    /// </summary>
    [TestMethod]
    public void ReferencedEntities_UnresolvableReference_RaisesMsg2020() =>
        Fixture().AssertSqlError(
            "select * from sys.dm_sql_referenced_entities('dbo.p_missing', 'OBJECT')",
            2020,
            "The dependencies reported for entity \"dbo.p_missing\" might not include references to all columns. This is either because the entity references an object that does not exist or because of an error in one or more statements in the entity.  Before rerunning the query, ensure that there are no errors in the entity and that all objects referenced by the entity exist.");

    // ---- sp_depends ----

    [TestMethod]
    public void SpDepends_ReferencedBySet_ListsEveryReferencerWithItsTypeLabel()
    {
        var sim = Fixture();
        var rows = Rows(sim, "exec sp_depends 'dbo.t'");
        var byName = rows.ToDictionary(r => (string)r["name"]!, r => (string)r["type"]!);
        AreEqual("view", byName["dbo.v"]);
        AreEqual("view", byName["dbo.v_sb"]);
        AreEqual("scalar function", byName["dbo.f"]);
        AreEqual("inline function", byName["dbo.itvf"]);
        AreEqual("table function", byName["dbo.mstvf"]);
        AreEqual("stored procedure", byName["dbo.p_read"]);
        // The trigger is listed against what its body reads, never against the
        // table it is attached to — probe-confirmed.
        DoesNotContain("dbo.trg", byName.Keys);
        AreEqual("trigger", Rows(sim, "exec sp_depends 'dbo.u'")
            .Single(r => (string)r["name"]! == "dbo.trg")["type"]);
    }

    /// <summary>
    /// The references set is one row per referenced column, with real's
    /// <c>updated</c> / <c>selected</c> yes-no cells. The <c>selected</c> cell
    /// is <c>readobj | selall</c>, so a column reached through a <c>*</c> reads
    /// as selected here even though the catalog view separates the two.
    /// </summary>
    [TestMethod]
    public void SpDepends_ReferencesSet_ReportsOneRowPerColumn()
    {
        var sim = Fixture();
        var rows = Rows(sim, "exec sp_depends 'dbo.v'");
        HasCount(3, rows);
        CollectionAssert.AreEqual(new object?[] { "id", "a", "b" }, rows.ConvertAll(r => r["column"]));
        foreach (var row in rows)
        {
            AreEqual("dbo.t", row["name"]);
            AreEqual("user table", row["type"]);
            AreEqual("no", row["updated"]);
            AreEqual("yes", row["selected"]);
        }
    }

    /// <summary>A reference carrying no column detail reports a NULL <c>column</c> cell with both flags off.</summary>
    [TestMethod]
    public void SpDepends_FunctionReference_ReportsANullColumnCell()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_fn as select a from dbo.t; select dbo.f(1)");
        var rows = Rows(sim, "exec sp_depends 'dbo.p_fn'");
        var function = rows.Single(r => (string)r["name"]! == "dbo.f");
        AreEqual("scalar function", function["type"]);
        AreEqual("no", function["updated"]);
        AreEqual("no", function["selected"]);
        IsNull(function["column"]);
    }

    [TestMethod]
    public void SpDepends_UpdatedColumn_ReportsYesInTheUpdatedCell()
    {
        var sim = Fixture();
        var rows = Rows(sim, "exec sp_depends 'dbo.p_write'");
        var written = rows.Single(r => (string)r["column"]! == "a");
        AreEqual("yes", written["updated"]);
        AreEqual("no", written["selected"]);
        var read = rows.Single(r => (string)r["column"]! == "b");
        AreEqual("no", read["updated"]);
        AreEqual("yes", read["selected"]);
    }

    /// <summary>Both sets arrive when the object sits on both sides of the graph, references first.</summary>
    [TestMethod]
    public void SpDepends_EmitsBothSets_ReferencesThenReferencedBy()
    {
        var sim = Fixture();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("exec sp_depends 'dbo.p_read'");
        using var reader = command.ExecuteReader();
        AreEqual(5, reader.FieldCount);
        AreEqual("column", reader.GetName(4));
        IsTrue(reader.NextResult());
        AreEqual(2, reader.FieldCount);
        AreEqual("name", reader.GetName(0));
        AreEqual("type", reader.GetName(1));
        IsFalse(reader.NextResult());
    }

    /// <summary>
    /// Each set carries a severity-10 header — Msg 15459 for the references set
    /// and Msg 15460 for the referenced-by set. Both ride the batch's single
    /// coalesced <c>InfoMessage</c> event (the simulator's batch-wide
    /// info-message semantic, shared with PRINT), so the delivered error
    /// carries the first header's number and both texts.
    /// </summary>
    [TestMethod]
    public void SpDepends_HeadersArriveAsSeverityTenInfoMessages()
    {
        var sim = Fixture();
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var errors = new List<SimulatedError>();
        connection.InfoMessage += (_, e) => errors.AddRange(e.Errors);
        using var command = connection.CreateCommand("exec sp_depends 'dbo.p_read'");
        _ = command.ExecuteNonQuery();
        HasCount(1, errors);
        AreEqual(15459, errors[0].Number);
        AreEqual((byte)10, errors[0].Class);
        Assert.Contains("In the current database, the specified object references the following:", errors[0].Message);
        Assert.Contains("In the current database, the specified object is referenced by the following:", errors[0].Message);
    }

    /// <summary>
    /// An object on neither side of the graph gets Msg 15461 and no result set.
    /// </summary>
    [TestMethod]
    public void SpDepends_ObjectWithNoDependencies_ReportsMsg15461AndNoResultSet()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create table lonely (x int)");
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<(int Number, string Message)>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add((error.Number, error.Message));
        };
        using var command = connection.CreateCommand("exec sp_depends 'dbo.lonely'");
        _ = command.ExecuteNonQuery();
        CollectionAssert.AreEqual(
            new[] { (15461, "Object does not reference any object, and no objects reference it.") },
            messages);
    }

    [TestMethod]
    public void SpDepends_MissingObject_RaisesMsg15009() =>
        Fixture().AssertSqlError(
            "exec sp_depends 'dbo.nosuch'",
            15009,
            "The object 'dbo.nosuch' does not exist in database 'simulated' or is invalid for this operation.");

    [TestMethod]
    public void SpDepends_OtherDatabaseQualifier_RaisesMsg15250() =>
        Fixture().AssertSqlError(
            "exec sp_depends 'master.dbo.t'",
            15250,
            "The database name component of the object qualifier must be the name of the current database.");

    // ---- The legacy pair: sys.sql_dependencies and sysdepends ----

    /// <summary>The <c>sys.sql_dependencies</c> rows one referencing object contributes.</summary>
    private static List<Dictionary<string, object?>> LegacyDeps(Simulation sim, string referencingName) => Rows(sim, $"""
        select class, class_desc, column_id, referenced_major_id, referenced_minor_id,
               is_selected, is_updated, is_select_all
        from sys.sql_dependencies
        where object_id = object_id('dbo.{referencingName}')
        order by referenced_major_id, referenced_minor_id
        """);

    /// <summary>The <c>sysdepends</c> rows one referencing object contributes.</summary>
    private static List<Dictionary<string, object?>> Sysdepends(Simulation sim, string referencingName) => Rows(sim, $"""
        select depid, number, depnumber, status, deptype, depdbid, depsiteid, selall, resultobj, readobj
        from sysdepends
        where id = object_id('dbo.{referencingName}')
        order by depid, depnumber
        """);

    /// <summary>
    /// A plain view reports its read columns and <em>no</em> object row — the
    /// legacy shape is narrower than <c>sys.sql_expression_dependencies</c>',
    /// which reports the object row instead.
    /// </summary>
    [TestMethod]
    public void SqlDependencies_PlainView_ReportsColumnRowsOnly()
    {
        var sim = Fixture();
        var rows = LegacyDeps(sim, "v");
        HasCount(3, rows);
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, rows.ConvertAll(r => r["referenced_minor_id"]));
        AreEqual((byte)0, rows[0]["class"]);
        AreEqual("OBJECT_OR_COLUMN_REFERENCE_NON_SCHEMA_BOUND", rows[0]["class_desc"]);
        AreEqual(0, rows[0]["column_id"]);
        AreEqual(sim.ExecuteScalar("select object_id('dbo.t')"), rows[0]["referenced_major_id"]);
        IsTrue(rows.TrueForAll(r => Flag(r, "is_selected")));
        IsTrue(rows.TrueForAll(r => !Flag(r, "is_updated") && !Flag(r, "is_select_all")));
    }

    /// <summary>A <c>SELECT *</c> marks every column <c>is_select_all</c> instead.</summary>
    [TestMethod]
    public void SqlDependencies_StarView_MarksEveryColumnSelectAll()
    {
        var rows = LegacyDeps(Fixture(), "v_star");
        HasCount(3, rows);
        IsTrue(rows.TrueForAll(r => Flag(r, "is_select_all") && !Flag(r, "is_selected")));
    }

    /// <summary>
    /// A schema-bound reference is class 1 and carries the object row as well
    /// as its columns, since schema binding pins the object itself.
    /// </summary>
    [TestMethod]
    public void SqlDependencies_SchemaBoundView_ReportsObjectRowAndColumns()
    {
        var rows = LegacyDeps(Fixture(), "v_sb");
        HasCount(3, rows);
        CollectionAssert.AreEqual(new object?[] { 0, 1, 2 }, rows.ConvertAll(r => r["referenced_minor_id"]));
        IsTrue(rows.TrueForAll(r => (byte)r["class"]! == 1));
        AreEqual("OBJECT_OR_COLUMN_REFERENCE_SCHEMA_BOUND", rows[0]["class_desc"]);
    }

    /// <summary>
    /// The object row also stands alone where the reference reaches no column:
    /// a whole-object read, a <c>DELETE</c>, an <c>INSERT</c> with no column
    /// list, and an <c>EXEC</c> under either name shape.
    /// </summary>
    [TestMethod]
    [DataRow("p_whole_read", "select 1 from dbo.t", true, false)]
    [DataRow("p_delete", "delete from dbo.t", false, true)]
    [DataRow("p_insert_all", "insert dbo.t values (1, 2, 'x')", false, true)]
    [DataRow("p_call", "exec dbo.p_read", false, false)]
    [DataRow("p_call1", "exec p_read", false, false)]
    public void SqlDependencies_ReferenceReachingNoColumn_ReportsObjectRow(
        string module, string body, bool selected, bool updated)
    {
        var sim = Fixture();
        if (!module.StartsWith("p_call", StringComparison.Ordinal))
            sim.ExecuteBatches($"create procedure {module} as {body}");
        var rows = LegacyDeps(sim, module);
        HasCount(1, rows);
        AreEqual(0, rows[0]["referenced_minor_id"]);
        AreEqual(selected, Flag(rows[0], "is_selected"));
        AreEqual(updated, Flag(rows[0], "is_updated"));
        IsFalse(Flag(rows[0], "is_select_all"));
    }

    /// <summary>
    /// A write that names columns reports those columns and no object row —
    /// an <c>UPDATE</c>'s SET list and an <c>INSERT</c>'s column list alike.
    /// </summary>
    [TestMethod]
    [DataRow("p_update_col", "update dbo.t set a = 5")]
    [DataRow("p_insert_col", "insert dbo.t (a) values (5)")]
    public void SqlDependencies_WriteNamingColumns_ReportsColumnRowsOnly(string module, string body)
    {
        var sim = Fixture();
        sim.ExecuteBatches($"create procedure {module} as {body}");
        var rows = LegacyDeps(sim, module);
        HasCount(1, rows);
        AreEqual(2, rows[0]["referenced_minor_id"]);
        IsTrue(Flag(rows[0], "is_updated"));
        IsFalse(Flag(rows[0], "is_selected"));
    }

    /// <summary>
    /// A computed column is a referencing entity in its own right: the row
    /// carries the <em>table</em>'s object id with <c>column_id</c> naming the
    /// computed column, and a CHECK constraint carries its own id at
    /// <c>column_id</c> 0. Both are schema-bound references.
    /// </summary>
    [TestMethod]
    public void SqlDependencies_ComputedColumnAndCheckConstraint_CarryTheirOwnReferencingIds()
    {
        var sim = Fixture();
        var computed = Rows(sim, """
            select column_id, class, referenced_minor_id
            from sys.sql_dependencies
            where object_id = object_id('dbo.cc') order by referenced_minor_id
            """);
        HasCount(1, computed);
        AreEqual(3, computed[0]["column_id"]);
        AreEqual((byte)1, computed[0]["class"]);
        AreEqual(2, computed[0]["referenced_minor_id"]);

        var check = Rows(sim, """
            select column_id, class, referenced_major_id, referenced_minor_id
            from sys.sql_dependencies
            where object_id = object_id('dbo.ck_cc')
            """);
        HasCount(1, check);
        AreEqual(0, check[0]["column_id"]);
        AreEqual((byte)1, check[0]["class"]);
        AreEqual(sim.ExecuteScalar("select object_id('dbo.cc')"), check[0]["referenced_major_id"]);
    }

    /// <summary>
    /// The legacy pair stores ids, so a reference it can't resolve to one has
    /// no row: a body naming a missing object, and a procedure whose only
    /// dependency is a table-valued parameter's type.
    /// </summary>
    [TestMethod]
    [DataRow("p_missing")]
    [DataRow("p_tvp")]
    public void SqlDependencies_ReferenceWithoutAnId_ContributesNoRow(string module)
    {
        var sim = Fixture();
        sim.ExecuteBatches(
            "create type tt as table (x int)",
            "create procedure p_tvp @p tt readonly as select 1");
        IsEmpty(LegacyDeps(sim, module));
    }

    /// <summary>
    /// <c>sysdepends</c> packs the three use flags into <c>status</c> — 2 =
    /// selall, 4 = resultobj, 8 = readobj — beside the bit columns that carry
    /// them one apiece.
    /// </summary>
    [TestMethod]
    public void Sysdepends_StatusPacksTheThreeUseFlags()
    {
        var sim = Fixture();
        sim.ExecuteBatches("create procedure p_mixed as begin select * from dbo.t; update dbo.t set a = 1 where id = 2 end");
        var rows = Sysdepends(sim, "p_mixed");
        HasCount(3, rows);
        CollectionAssert.AreEqual(new object?[] { (short)1, (short)2, (short)3 }, rows.ConvertAll(r => r["depnumber"]));
        CollectionAssert.AreEqual(new object?[] { (short)10, (short)6, (short)2 }, rows.ConvertAll(r => r["status"]));
        IsTrue(rows.TrueForAll(r => Flag(r, "selall")));
        CollectionAssert.AreEqual(
            new object?[] { true, false, false }, rows.ConvertAll(r => (object?)Flag(r, "readobj")));
        CollectionAssert.AreEqual(
            new object?[] { false, true, false }, rows.ConvertAll(r => (object?)Flag(r, "resultobj")));
    }

    /// <summary>
    /// <c>number</c> is the referencing entity's minor id — a computed column's
    /// own <c>column_id</c> — except on a procedure, where it is the procedure
    /// group number and 1 stands for the single ungrouped body. <c>deptype</c>
    /// mirrors <c>sys.sql_dependencies</c>' class, and the two legacy
    /// cross-database columns are 0 on every row.
    /// </summary>
    [TestMethod]
    [DataRow("p_read", (short)1, (byte)0)]
    [DataRow("v", (short)0, (byte)0)]
    [DataRow("v_sb", (short)0, (byte)1)]
    [DataRow("trg", (short)0, (byte)0)]
    public void Sysdepends_NumberAndDeptype(string module, short number, byte deptype)
    {
        var rows = Sysdepends(Fixture(), module);
        IsNotEmpty(rows);
        IsTrue(rows.TrueForAll(r => (short)r["number"]! == number));
        IsTrue(rows.TrueForAll(r => (byte)r["deptype"]! == deptype));
        IsTrue(rows.TrueForAll(r => (short)r["depdbid"]! == 0 && (short)r["depsiteid"]! == 0));
    }

    [TestMethod]
    public void Sysdepends_ComputedColumn_ReportsItsColumnIdAsNumber()
    {
        var rows = Sysdepends(Fixture(), "cc");
        HasCount(1, rows);
        AreEqual((short)3, rows[0]["number"]);
    }

    /// <summary>
    /// <c>sysdepends</c> resolves unqualified as well as under <c>sys.</c>,
    /// the way <c>sysobjects</c> does; <c>sql_dependencies</c> takes the
    /// qualifier only (probe-confirmed both ways).
    /// </summary>
    [TestMethod]
    public void Sysdepends_ResolvesUnqualifiedAndUnderSys()
    {
        var sim = Fixture();
        AreEqual(
            sim.ExecuteScalar("select count(*) from sysdepends"),
            sim.ExecuteScalar("select count(*) from sys.sysdepends"));
    }

    [TestMethod]
    public void SqlDependencies_UnqualifiedName_RaisesMsg208() =>
        Fixture().AssertSqlError("select count(*) from sql_dependencies", 208);

    /// <summary>
    /// Both views are computed on read like every other dependency surface, so
    /// dropping the referencing module takes its rows away.
    /// </summary>
    [TestMethod]
    public void SqlDependencies_DropOfReferencingModule_RemovesItsRows()
    {
        var sim = Fixture();
        var viewId = sim.ExecuteScalar("select object_id('dbo.v')");
        IsNotEmpty(LegacyDeps(sim, "v"));
        sim.ExecuteBatches("drop view v");
        IsEmpty(Rows(sim, $"select 1 from sys.sql_dependencies where object_id = {viewId}"));
        IsEmpty(Rows(sim, $"select 1 from sysdepends where id = {viewId}"));
    }
}
