using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the per-object creation-time <c>ANSI_NULLS</c> capture: the
/// session's <c>SET ANSI_NULLS</c> at CREATE is recorded on the object and
/// surfaces through <c>sys.tables.uses_ansi_nulls</c> /
/// <c>sys.sql_modules.uses_ansi_nulls</c> and
/// <c>OBJECTPROPERTY(id, 'IsAnsiNullsOn' | 'ExecIsAnsiNullsOn')</c>.
/// Metadata only — the <c>= NULL</c> comparison semantic the option governs
/// isn't modeled, so a body created under OFF still compares ANSI-style.
/// Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class AnsiNullsCaptureTests
{
    /// <summary>
    /// Runs <paramref name="batches"/> with the session flipped to
    /// <c>ANSI_NULLS OFF</c> first, so every object they create captures OFF.
    /// </summary>
    private static Simulation CreatedUnderOff(params IEnumerable<string> batches)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches([.. new[] { "set ansi_nulls off" }.Concat(batches)]);
        return simulation;
    }

    // ----- sys.tables -----

    [TestMethod]
    public void SysTables_UsesAnsiNulls_DefaultsOn()
        => IsTrue(new Simulation().ExecuteScalar<bool>("""
            create table t (id int);
            select uses_ansi_nulls from sys.tables where name = 't'
            """));

    [TestMethod]
    public void SysTables_UsesAnsiNulls_ReflectsTheCapture()
    {
        var simulation = CreatedUnderOff("create table t_off (id int)");
        simulation.ExecuteBatches("set ansi_nulls on", "create table t_on (id int)");
        IsFalse(simulation.ExecuteScalar<bool>("select uses_ansi_nulls from sys.tables where name = 't_off'"));
        IsTrue(simulation.ExecuteScalar<bool>("select uses_ansi_nulls from sys.tables where name = 't_on'"));
    }

    /// <summary>
    /// <c>SELECT … INTO</c> creates a table too, so it captures the same way.
    /// </summary>
    [TestMethod]
    public void SelectInto_CapturesTheSessionSetting()
        => IsFalse(CreatedUnderOff("select 1 as v into t")
            .ExecuteScalar<bool>("select uses_ansi_nulls from sys.tables where name = 't'"));

    // ----- sys.sql_modules -----

    [TestMethod]
    public void SqlModules_UsesAnsiNulls_ReflectsTheCapture()
    {
        var simulation = CreatedUnderOff("create procedure p_off as select 1 as v");
        simulation.ExecuteBatches("set ansi_nulls on", "create procedure p_on as select 1 as v");
        IsFalse(simulation.ExecuteScalar<bool>("select uses_ansi_nulls from sys.sql_modules where object_id = object_id('p_off')"));
        IsTrue(simulation.ExecuteScalar<bool>("select uses_ansi_nulls from sys.sql_modules where object_id = object_id('p_on')"));
    }

    [TestMethod]
    public void SqlModules_View_CapturesOff()
        => IsFalse(CreatedUnderOff("create view v as select 1 as v")
            .ExecuteScalar<bool>("select uses_ansi_nulls from sys.sql_modules where object_id = object_id('v')"));

    [TestMethod]
    public void SqlModules_ScalarFunction_CapturesOff()
        => IsFalse(CreatedUnderOff("create function f() returns int as begin return 1 end")
            .ExecuteScalar<bool>("select uses_ansi_nulls from sys.sql_modules where object_id = object_id('f')"));

    [TestMethod]
    public void SqlModules_Trigger_CapturesOff()
        => IsFalse(CreatedUnderOff(
                "create table t (id int)",
                "create trigger tr on t after insert as select 1")
            .ExecuteScalar<bool>("select uses_ansi_nulls from sys.sql_modules where object_id = object_id('tr')"));

    /// <summary>
    /// <c>ALTER</c> re-stamps the capture from the altering session, the same
    /// way it re-stamps <c>QUOTED_IDENTIFIER</c>.
    /// </summary>
    [TestMethod]
    public void Alter_RestampsTheCapture()
    {
        var simulation = CreatedUnderOff("create procedure p as select 1 as v");
        simulation.ExecuteBatches("set ansi_nulls on", "alter procedure p as select 2 as v");
        IsTrue(simulation.ExecuteScalar<bool>("select uses_ansi_nulls from sys.sql_modules where object_id = object_id('p')"));
    }

    // ----- OBJECTPROPERTY read-backs -----

    [TestMethod]
    public void ObjectProperty_IsAnsiNullsOn_OnModule_ReflectsTheCapture()
        => AreEqual(0, CreatedUnderOff("create procedure p as select 1 as v")
            .ExecuteScalar("select objectproperty(object_id('p'), 'IsAnsiNullsOn')"));

    [TestMethod]
    public void ObjectProperty_ExecIsAnsiNullsOn_OnModule_ReflectsTheCapture()
        => AreEqual(0, CreatedUnderOff("create procedure p as select 1 as v")
            .ExecuteScalar("select objectproperty(object_id('p'), 'ExecIsAnsiNullsOn')"));

    /// <summary>
    /// A table answers the captured value for the shorter spelling — unlike
    /// <c>IsQuotedIdentOn</c>, which is a constant 1 for any table — and NULL
    /// for the module-only <c>ExecIs…</c> spelling (both probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ObjectProperty_OnTableCreatedUnderOff_IsZeroAndExecFormIsNull()
    {
        var simulation = CreatedUnderOff("create table t (id int)");
        AreEqual(0, simulation.ExecuteScalar("select objectproperty(object_id('t'), 'IsAnsiNullsOn')"));
        using var reader = simulation.ExecuteReader("select objectproperty(object_id('t'), 'ExecIsAnsiNullsOn')");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    /// <summary>
    /// Both spellings answer NULL for a sequence — the kind filter real
    /// applies (probe-confirmed alongside synonyms and constraints).
    /// </summary>
    [TestMethod]
    public void ObjectProperty_OnSequence_IsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create sequence s as int start with 1;
            select objectproperty(object_id('s'), 'IsAnsiNullsOn')
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }
}
