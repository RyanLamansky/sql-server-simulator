using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>WITH</c> option clause of every module kind: a function's grammar
/// and every other module's, and how each judges what it parsed. Probed
/// 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ModuleOptionTests
{
    private static string Module(string kind, string options) => kind switch
    {
        "scalar" => $"create function f() returns int with {options} as begin return 1 end",
        "inline" => $"create function f() returns table with {options} as return select 1 a",
        "multi" => $"create function f() returns @t table (a int) with {options} as begin return end",
        "procedure" => $"create procedure p with {options} as select 1",
        "view" => $"create view v with {options} as select 1 a",
        "trigger" => $"create trigger dt on database with {options} for create_table as select 1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [TestMethod]
    [DataRow("scalar", "encryption, schemabinding, execute as caller, returns null on null input")]
    [DataRow("scalar", "called on null input, inline = off")]
    [DataRow("inline", "encryption, schemabinding")]
    [DataRow("multi", "encryption, schemabinding, execute as caller")]
    [DataRow("procedure", "encryption, recompile, execute as caller")]
    [DataRow("view", "encryption, schemabinding, view_metadata")]
    [DataRow("trigger", "encryption, execute as caller")]
    public void AnOptionTheHostTakes_IsAccepted(string kind, string options)
        => _ = new Simulation().ExecuteNonQuery(Module(kind, options));

    [TestMethod]
    [DataRow("scalar", "recompile", "'recompile' is not a recognized option.")]
    [DataRow("inline", "view_metadata", "'view_metadata' is not a recognized option.")]
    [DataRow("procedure", "Bogus", "'Bogus' is not a recognized option.")]
    [DataRow("view", "encryption, encryption", "Option 'ENCRYPTION' is specified more than once.")]
    public void AnUnrecognizedOrRepeatedOption_IsRefused(string kind, string options, string message)
        => AreEqual(message, new Simulation().AssertSqlError(Module(kind, options), message.StartsWith("Option", StringComparison.Ordinal) ? 1039 : 195).Errors[0].Message);

    [TestMethod]
    [DataRow("inline", "execute as caller", "FUNCTION", 1)]
    [DataRow("inline", "returns null on null input", "FUNCTION", 1)]
    [DataRow("multi", "inline = on", "FUNCTION", 1)]
    [DataRow("procedure", "view_metadata", "PROCEDURE", 1)]
    [DataRow("view", "recompile", "VIEW", 1)]
    [DataRow("view", "execute as caller", "VIEW", 2)]
    [DataRow("trigger", "recompile", "TRIGGER", 1)]
    public void AnOptionTheHostRefuses_IsMsg487(string kind, string options, string statement, int state)
    {
        var error = new Simulation().AssertSqlError(Module(kind, options), 487).Errors[0];
        AreEqual($"An invalid option was specified for the statement \"CREATE/ALTER {statement}\".", error.Message);
        AreEqual(state, error.State);
    }

    /// <summary>Outside a function's grammar the multi-word options are a syntax error after their first word.</summary>
    [TestMethod]
    [DataRow("procedure", "returns null on null input", 156)]
    [DataRow("view", "called on null input", 156)]
    [DataRow("trigger", "inline = off", 102)]
    public void AFunctionOnlyOption_IsASyntaxErrorElsewhere(string kind, string options, int number)
        => _ = new Simulation().AssertSqlError(Module(kind, options), number);

    [TestMethod]
    [DataRow("procedure", "schemabinding", "p", 1)]
    [DataRow("procedure", "native_compilation", "p", 1)]
    [DataRow("trigger", "schemabinding", "dt", 1)]
    [DataRow("scalar", "native_compilation", "f", 2)]
    public void SchemaBindingWithoutNativeCompilation_IsMsg10796(string kind, string options, string module, int state)
    {
        var error = new Simulation().AssertSqlError(Module(kind, options), 10796).Errors[0];
        AreEqual(state, error.State);
        AreEqual(16, error.LineNumber);
        AreEqual(module, error.Procedure);
    }

    [TestMethod]
    public void ADmlTriggersRefusal_NamesTheTrigger()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        AreEqual("tr", sim.AssertSqlError("create trigger tr on t with bogus after insert as select 1", 195).Errors[0].Procedure);
    }

    [TestMethod]
    public void AMultiStatementFunctionsExecuteAs_IsRecorded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create function f() returns @t table (a int) with execute as owner as begin return end");
        AreEqual(-2, sim.ExecuteScalar("select execute_as_principal_id from sys.sql_modules where object_id = object_id('f')"));
    }
}
