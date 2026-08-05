using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <strong>Msg 529</strong> is settled from the two <em>types</em> while real
/// compiles, before any value exists — so a typed NULL raises it, an empty
/// rowset raises it, and a module body carrying one refuses its own
/// <c>CREATE</c>. The pairs are the probed ones (SQL Server 2025, 2026-08-05:
/// every ordered pair of the 28 common type names, 284 refusals).
/// </summary>
[TestClass]
public sealed class ConversionLegalityTests
{
    private static void Refuses(string sourceType, string targetType)
        => new Simulation().AssertSqlError(
            $"select cast(cast(null as {sourceType}) as {targetType})",
            529,
            $"Explicit conversion from data type {FamilyRoot(sourceType)} to {FamilyRoot(targetType)} is not allowed.");

    private static void Converts(string sourceType, string targetType)
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($"select cast(cast(null as {sourceType}) as {targetType})"));

    private static string FamilyRoot(string typeName)
    {
        var paren = typeName.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? typeName : typeName[..paren];
    }

    // --- Numbers and the date/time family ---

    /// <summary>Only the day-count conversions cross: datetime and smalldatetime.</summary>
    [TestMethod]
    [DataRow("int", "date")]
    [DataRow("int", "time(7)")]
    [DataRow("int", "datetime2(7)")]
    [DataRow("int", "datetimeoffset(7)")]
    [DataRow("decimal(18,4)", "date")]
    [DataRow("float", "time(7)")]
    [DataRow("money", "datetime2(7)")]
    [DataRow("bit", "date")]
    public void NumberToNonLegacyDateTime_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("int", "datetime")]
    [DataRow("int", "smalldatetime")]
    [DataRow("float", "datetime")]
    public void NumberToLegacyDateTime_Converts(string source, string target) => Converts(source, target);

    [TestMethod]
    [DataRow("date", "int")]
    [DataRow("time(7)", "decimal(18,4)")]
    [DataRow("datetime2(7)", "float")]
    [DataRow("datetimeoffset(7)", "money")]
    public void NonLegacyDateTimeToNumber_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("datetime", "int")]
    [DataRow("smalldatetime", "float")]
    public void LegacyDateTimeToNumber_Converts(string source, string target) => Converts(source, target);

    /// <summary>A whole-day value and a within-day one share nothing.</summary>
    [TestMethod]
    [DataRow("date", "time(7)")]
    [DataRow("time(7)", "date")]
    public void DateAndTimeAcrossEachOther_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("date", "datetime2(7)")]
    [DataRow("time(7)", "datetimeoffset(7)")]
    [DataRow("datetimeoffset(7)", "date")]
    public void WithinTheDateTimeFamily_Converts(string source, string target) => Converts(source, target);

    // --- uniqueidentifier, xml, sql_variant, binary ---

    [TestMethod]
    [DataRow("uniqueidentifier", "int")]
    [DataRow("uniqueidentifier", "datetime")]
    [DataRow("uniqueidentifier", "xml")]
    [DataRow("int", "uniqueidentifier")]
    [DataRow("date", "uniqueidentifier")]
    public void UniqueIdentifierOutsideStringsAndBinary_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("uniqueidentifier", "varchar(40)")]
    [DataRow("uniqueidentifier", "varbinary(16)")]
    [DataRow("uniqueidentifier", "sql_variant")]
    public void UniqueIdentifierToStringsAndBinary_Converts(string source, string target) => Converts(source, target);

    [TestMethod]
    [DataRow("xml", "int")]
    [DataRow("xml", "date")]
    [DataRow("xml", "uniqueidentifier")]
    [DataRow("xml", "sql_variant")]
    [DataRow("sql_variant", "xml")]
    public void XmlOutsideStringsAndBinary_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("binary(8)", "float")]
    [DataRow("varbinary(8)", "real")]
    public void BinaryToApproximate_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("varbinary(8)", "int")]
    [DataRow("varbinary(8)", "datetime")]
    [DataRow("varbinary(8)", "image")]
    public void BinaryElsewhere_Converts(string source, string target) => Converts(source, target);

    // --- The legacy LOB targets take the mirror of their own allow-lists ---

    [TestMethod]
    [DataRow("int", "text")]
    [DataRow("date", "ntext")]
    [DataRow("xml", "text")]
    [DataRow("varbinary(8)", "ntext")]
    [DataRow("nvarchar(20)", "image")]
    [DataRow("int", "image")]
    public void IntoALegacyLob_Msg529(string source, string target) => Refuses(source, target);

    [TestMethod]
    [DataRow("varchar(20)", "text")]
    [DataRow("nvarchar(20)", "ntext")]
    [DataRow("varchar(20)", "image")]
    [DataRow("varbinary(8)", "image")]
    public void IntoALegacyLobFromItsOwnFamily_Converts(string source, string target) => Converts(source, target);

    // --- Bound at compile time ---

    /// <summary>An untyped NULL has no type to judge, so it converts anywhere.</summary>
    [TestMethod]
    public void UntypedNull_ConvertsAnywhere()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select cast(null as date)"));

    [TestMethod]
    public void EmptyRowset_StillRaises()
        => new Simulation().AssertSqlError(
            "create table t (d date); select cast(d as int) from t where 1 = 0",
            529);

    [TestMethod]
    public void TryCast_RaisesRatherThanReturningNull()
        => new Simulation().AssertSqlError("select try_cast(cast(null as date) as int)", 529);

    // --- A module body refuses its own CREATE ---

    private static SimulatedSqlException ModuleRefused(string createStatement)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table i1 (a int, d date, g uniqueidentifier)");
        return sim.AssertSqlError(createStatement, 529);
    }

    [TestMethod]
    public void ProcedureBody_Msg529AtCreate()
        => Assert.Contains("date to int", ModuleRefused("create procedure p1 as select cast(d as int) from i1").Message);

    [TestMethod]
    public void FunctionBody_Msg529AtCreate()
        => Assert.Contains("date to int", ModuleRefused("create function f1() returns int as begin return (select cast(d as int) from i1) end").Message);

    [TestMethod]
    public void ViewBody_Msg529AtCreate()
        => Assert.Contains("date to int", ModuleRefused("create view v1 as select cast(d as int) as x from i1").Message);

    [TestMethod]
    public void TriggerBody_Msg529AtCreate()
        => Assert.Contains("date to int", ModuleRefused("create trigger tr1 on i1 after insert as select cast(d as int) from i1").Message);

    /// <summary>A parameter's declared type is judged like a column's.</summary>
    [TestMethod]
    public void ParameterTyped_Msg529AtCreate()
        => Assert.Contains("date to int", ModuleRefused("create procedure p2 @x date as select cast(@x as int)").Message);

    /// <summary>Binding precedes control flow, so an unreachable branch is judged too.</summary>
    [TestMethod]
    public void UnreachableBranch_Msg529AtCreate()
        => Assert.Contains("date to int", ModuleRefused("create procedure p3 as if 1 = 0 select cast(d as int) from i1").Message);

    /// <summary>
    /// Msg 529 ends the report where it is: real gathers name-resolution errors
    /// across a whole body but stops at this one, so a later statement's bad
    /// column never joins it.
    /// </summary>
    [TestMethod]
    public void ConversionErrorEndsTheBindReport()
    {
        var ex = ModuleRefused("create procedure p4 as select cast(d as int) from i1; select nosuch from i1");
        AreEqual(1, ex.Errors.Count);
    }

    /// <summary>A name error found first still reports, with the conversion behind it.</summary>
    [TestMethod]
    public void NameErrorAheadOfIt_BothReport()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table i1 (a int, d date, g uniqueidentifier)");
        var ex = sim.AssertSqlError("create procedure p5 as select nosuch from i1; select cast(d as int) from i1", 207);
        AreEqual(2, ex.Errors.Count);
        AreEqual(207, ex.Errors[0].Number);
        AreEqual(529, ex.Errors[1].Number);
    }
}
