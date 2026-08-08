using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>float</c> / <c>real</c> types — IEEE 754
/// double / single, scientific-notation literal parsing, and the
/// empty-string-to-zero quirk that distinguishes them from
/// <c>decimal</c>'s strict parsing.
/// </summary>
[TestClass]
public sealed class FloatTests
{
    [TestMethod]
    public void Literal_ScientificNotation_ProducesFloat()
    {
        // Verified against SQL Server 2025: any literal with `e` is float.
        AreEqual(150.0, ExecuteScalar("select 1.5e2"));
    }

    [TestMethod]
    public void Cast_StringToFloat_BasicRoundTrip()
    {
        AreEqual(1.5, ExecuteScalar("select cast('1.5' as float)"));
    }

    [TestMethod]
    public void Cast_StringToFloat_AcceptsScientific()
    {
        AreEqual(15000000000.0, ExecuteScalar("select cast('1.5E+10' as float)"));
    }

    [TestMethod]
    public void Cast_EmptyStringToFloat_ReturnsZero()
    {
        // SQL Server quirk: empty/whitespace-only strings cast to 0 for
        // float (verified against SQL Server 2025; differs from decimal,
        // where empty raises Msg 8114).
        AreEqual(0.0, ExecuteScalar("select cast('' as float)"));
    }

    [TestMethod]
    [DataRow("'inf'")]
    [DataRow("'NaN'")]
    [DataRow("'$5.95'")]
    [DataRow("'abc'")]
    public void Cast_BadStringToFloat_RaisesMsg8114(string literal)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({literal} as float)"));
        AreEqual("Error converting data type varchar to float.", ex.Message);
    }

    [TestMethod]
    public void Cast_RealHas4ByteStorage_FloatHas8()
    {
        // Smoke test: float(N) for N ≤ 24 → real (4 bytes), N ≥ 25 → float (8 bytes).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a float, b real, c float(24), d float(25))");
    }

    [TestMethod]
    [DataRow("1.5", 1)]
    [DataRow("-1.5", -1)]
    [DataRow("0.5", 0)]
    [DataRow("-0.5", 0)]
    public void Cast_FloatToInt_TruncatesTowardZero(string sourceLiteral, int expected)
    {
        // Float → int truncates (verified 1.5 → 1, -1.5 → -1, 0.5 → 0).
        var value = ExecuteScalar<int>($"select cast(cast({sourceLiteral} as float) as int)");
        AreEqual(expected, value);
    }

    [TestMethod]
    public void FloatArithmetic_FloatAndIntPromotesToFloat()
    {
        // float + int → float (verified against SQL Server 2025).
        AreEqual(3.5, ExecuteScalar("select cast(1.5 as float) + 2"));
    }

    [TestMethod]
    public void FloatArithmetic_FloatAndDecimalPromotesToFloat()
    {
        AreEqual(2.73, (double)ExecuteScalar("select cast(1.5 as float) + cast(1.23 as decimal(5, 2))")!, 0.0001);
    }

    [TestMethod]
    public void FloatArithmetic_DivideByZero_RaisesMsg8134()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(1 as float) / cast(0 as float)"));
        AreEqual("Divide by zero error encountered.", ex.Message);
    }

    [TestMethod]
    public void Parameter_DoubleRoundTrips()
    {
        const double expected = 3.14;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    public void Parameter_SingleRoundTrips()
    {
        const float expected = 3.14f;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    public void GetDouble_ReturnsTheValue()
    {
        const double expected = 3.14;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetDouble(0));
    }

    // IEEE 754 negative zero. `AreEqual(0.0, x)` can't see it — -0.0 == 0.0 in
    // .NET as in SQL Server — so these assert the rendering or the sign bit.

    [TestMethod]
    [DataRow("-cast(0 as real)")]
    [DataRow("-cast(0 as float)")]
    [DataRow("-cast(-0 as real)")]
    [DataRow("-cast(0.0 as float)")]
    [DataRow("-(0e0)")]
    [DataRow("-0.0e0")]
    [DataRow("-(0e0 - 0e0)")]
    [DataRow("0e0 * -1")]
    [DataRow("-1 * 0e0")]
    [DataRow("0e0 / -1")]
    [DataRow("-cast(0 as real) * 58")]
    [DataRow("power(-cast(0 as float), 1)")]
    [DataRow("round(-cast(0 as float), 0)")]
    public void NegativeZero_ApproximateNegation_KeepsTheSign(string expression)
        // Unary minus flips the IEEE sign bit rather than computing `0 - x`,
        // which would fold the two zeros together (0.0 - 0.0 is +0.0).
        => AreEqual("-0", ExecuteScalar($"select cast({expression} as varchar(30))"));

    [TestMethod]
    [DataRow("-cast(0 as real)", "-0")]
    [DataRow("-cast(-0 as real)", "-0")]
    [DataRow("-cast(-col1 + col1 as real)", "-0")]
    [DataRow("-cast(+0 as real) * col1", "-0")]
    [DataRow("+ -col1 * + cast(-col1 + col1 as real) * + 58", "-0")]
    public void NegativeZero_CorpusShapes_RenderNegativeZeroOnce(string expression, string expected)
    {
        // Each shape collapses to a single row: -0 and +0 are one value for
        // DISTINCT even though the surviving row still renders its sign.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table tab2 (col1 float); insert tab2 values (1), (2), (3)");
        AreEqual(1, sim.ExecuteScalar($"select count(*) from (select distinct {expression} c from tab2) q"));
        AreEqual(expected, sim.ExecuteScalar($"select cast(c as varchar(30)) from (select distinct {expression} c from tab2) q"));
    }

    [TestMethod]
    [DataRow("cast(-0.0 as float)")]
    [DataRow("cast(-0.0 as real)")]
    [DataRow("cast(0.0 * -1 as float)")]
    [DataRow("cast(-cast(0 as decimal(10, 2)) as float)")]
    [DataRow("cast(0.0 - 0.0 as float)")]
    [DataRow("cast(0 * -1 as float)")]
    public void NegativeZero_ExactNumericSource_StaysUnsigned(string expression)
        // SQL Server's exact numerics have no signed zero, so a decimal or
        // integer zero widens to a *positive* float however it was produced —
        // .NET's decimal does keep a sign bit through `0.0m * -1`, and that
        // must not leak across the conversion.
        => AreEqual("0", ExecuteScalar($"select cast({expression} as varchar(30))"));

    [TestMethod]
    [DataRow("select cast(-0.0 as varchar(30))", "0.0")]
    [DataRow("select cast(-cast(0 as decimal(10, 2)) as varchar(30))", "0.00")]
    [DataRow("select cast(cast(0e0 * -1 as decimal(10, 2)) as varchar(30))", "0.00")]
    [DataRow("select cast(0 * -1 as varchar(30))", "0")]
    [DataRow("select cast(sign(-cast(0 as float)) as varchar(30))", "0")]
    [DataRow("select cast(abs(-cast(0 as float)) as varchar(30))", "0")]
    public void NegativeZero_ExactNumericAndSignScalars_RenderUnsigned(string commandText, string expected)
        => AreEqual(expected, ExecuteScalar(commandText));

    [TestMethod]
    [DataRow("cast(f as varchar(30))", "-0")]
    [DataRow("cast(r as varchar(30))", "-0")]
    [DataRow("concat('[', f, ']')", "[-0]")]
    [DataRow("'[' + str(f, 10, 2) + ']'", "[     -0.00]")]
    [DataRow("convert(varchar(30), f, 2)", "-0.000000000000000e+000")]
    [DataRow("cast(cast(cast(f as real) as float) as varchar(30))", "-0")]
    [DataRow("cast((select f as a for json path) as varchar(100))", """[{"a":-0.000000000000000e+000}]""")]
    [DataRow("cast((select f as a for xml path('r')) as varchar(100))", "<r><a>-0.000000000000000e+000</a></r>")]
    public void NegativeZero_SurvivesStorageAndStringConversion(string projection, string expected)
    {
        // A stored float / real keeps the sign bit, and every string surface
        // but FORMAT reports it.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (f float, r real);
            insert t values (-cast(0 as float), -cast(0 as real))
            """);
        AreEqual(expected, sim.ExecuteScalar($"select {projection} from t"));
    }

    [TestMethod]
    [DataRow("'G'", "0")]
    [DataRow("'N2'", "0.00")]
    [DataRow("'F3'", "0.000")]
    [DataRow("'E2'", "0.00E+000")]
    [DataRow("'C'", "$0.00")]
    [DataRow("'0.00'", "0.00")]
    [DataRow("'#.##'", "")]
    public void NegativeZero_Format_DropsTheSign(string format, string expected)
    {
        // FORMAT is the one string surface that hides it — real's CLR
        // implementation carries .NET Framework's unsigned-zero rendering.
        AreEqual(expected, ExecuteScalar($"select format(-cast(0 as float), {format})"));
        AreEqual(expected, ExecuteScalar($"select format(-cast(0 as real), {format})"));
    }

    [TestMethod]
    public void NegativeZero_Format_StillSignsANonZero()
        => AreEqual("-1", ExecuteScalar("select format(cast(-1 as float), 'G')"));

    [TestMethod]
    public void NegativeZero_Print_ReportsTheSign()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        _ = connection.CreateCommand("declare @f float = -cast(0 as float); print @f").ExecuteNonQuery();
        AreEqual("-0", string.Join("\n", messages));
    }

    [TestMethod]
    [DataRow("select count(*) from t where f = 0", 2)]
    [DataRow("select count(*) from t where f = -cast(0 as float)", 2)]
    [DataRow("select count(*) from (select distinct f from t) q", 1)]
    [DataRow("select count(*) from (select f from t group by f) q", 1)]
    [DataRow("select count(*) from (select f from t union select cast(0 as float)) q", 1)]
    [DataRow("select count(*) from (select f from t intersect select cast(0 as float)) q", 1)]
    [DataRow("select count(*) from (select f from t except select cast(0 as float)) q", 0)]
    public void NegativeZero_ComparesEqualToPositiveZero(string commandText, int expected)
    {
        // IEEE equality: the sign of zero is a stored value, never an identity.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (f float);
            insert t values (-cast(0 as float)), (cast(0 as float))
            """);
        AreEqual(expected, sim.ExecuteScalar(commandText));
    }

    [TestMethod]
    [DataRow("-cast(0 as float)", "cast(0 as float)", "-0")]
    [DataRow("cast(0 as float)", "-cast(0 as float)", "0")]
    public void NegativeZero_DistinctReportsTheRowItMetFirst(string first, string second, string expected)
    {
        // Grouping collapses the pair, and the surviving row is whichever
        // arrived first — so the reported sign follows insertion order.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (f float); insert t values ({first}), ({second})");
        AreEqual(expected, sim.ExecuteScalar("select cast(f as varchar(30)) from (select distinct f from t) q"));
        AreEqual(expected, sim.ExecuteScalar("select cast(f as varchar(30)) from t group by f"));
        AreEqual(expected, sim.ExecuteScalar("select cast(min(f) as varchar(30)) from t"));
        AreEqual(expected, sim.ExecuteScalar("select cast(max(f) as varchar(30)) from t"));
    }

    [TestMethod]
    public void NegativeZero_UniqueIndexTreatsItAsADuplicate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (f float);
            insert t values (-cast(0 as float)), (cast(0 as float))
            """);
        var ex = sim.AssertSqlError("create unique index ix on t (f)", 1505);
        Assert.Contains("duplicate key", ex.Message);
    }

    [TestMethod]
    [DataRow("f + 0", "0")]
    [DataRow("f * 1", "-0")]
    [DataRow("-f", "0")]
    [DataRow("sum(f)", "0")]
    [DataRow("avg(f)", "0")]
    public void NegativeZero_IeeeArithmetic(string expression, string expected)
    {
        // Straight IEEE: -0 + 0 is +0, -0 * 1 is -0, negating -0 is +0, and a
        // sum accumulating from +0 comes back positive.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (f float); insert t values (-cast(0 as float))");
        AreEqual(expected, sim.ExecuteScalar($"select cast({expression} as varchar(30)) from t"));
    }

    [TestMethod]
    public void NegativeZero_ClientValueCarriesTheSignBit()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select -cast(0 as float), -cast(0 as real), cast(0 as float), cast(0 as real)");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(double.IsNegative(reader.GetDouble(0)));
        IsTrue(float.IsNegative(reader.GetFloat(1)));
        IsFalse(double.IsNegative(reader.GetDouble(2)));
        IsFalse(float.IsNegative(reader.GetFloat(3)));
    }

    [TestMethod]
    public void NegativeZero_SelectIntoAndTableVariablePreserveIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (f float);
            insert t values (-cast(0 as float));
            select f into t2 from t
            """);
        AreEqual("-0", sim.ExecuteScalar("select cast(f as varchar(30)) from t2"));
        AreEqual("-0", sim.ExecuteScalar("""
            declare @v table (f float);
            insert @v select f from t;
            select cast(f as varchar(30)) from @v
            """));
    }

    [TestMethod]
    [DataRow("cast(1234567890 as float)", "1.23457e+009")]
    [DataRow("cast(1234567 as float)", "1.23457e+006")]
    [DataRow("cast(1000000 as float)", "1e+006")]
    [DataRow("cast(999999 as float)", "999999")]
    [DataRow("cast(999999.4 as float)", "999999")]
    [DataRow("cast(999999.5 as float)", "1e+006")]
    [DataRow("cast(100000 as float)", "100000")]
    [DataRow("cast(0.0001 as float)", "0.0001")]
    [DataRow("cast(0.00009999 as float)", "9.999e-005")]
    [DataRow("cast(0.00001234 as float)", "1.234e-005")]
    [DataRow("cast(-0.000123456 as float)", "-0.000123456")]
    [DataRow("cast(3.14159265358979 as float)", "3.14159")]
    [DataRow("cast(1.0/3 as float)", "0.333333")]
    [DataRow("cast(2 as float)/3", "0.666667")]
    [DataRow("cast(1e20 as float)", "1e+020")]
    [DataRow("cast(-1e20 as float)", "-1e+020")]
    [DataRow("cast(1e300 as float)", "1e+300")]
    [DataRow("cast(1e-300 as float)", "1e-300")]
    [DataRow("cast(1.5 as real)", "1.5")]
    [DataRow("cast(0.1 as real)", "0.1")]
    [DataRow("cast(1234567 as real)", "1.23457e+006")]
    [DataRow("cast(1e20 as real)", "1e+020")]
    [DataRow("cast(1e-20 as real)", "1e-020")]
    public void StylelessStringConversion_UsesConvertStyleZero(string expression, string expected)
        // A conversion to a string type with no style given is style 0 on both
        // types: six significant digits, fixed-point only while the rounded
        // magnitude stays in [1e-4, 1e6) and three-digit scientific otherwise.
        => AreEqual(expected, ExecuteScalar($"select cast({expression} as varchar(60))"));

    [TestMethod]
    [DataRow("convert(varchar(60), f)", "1.23457e+009")]
    [DataRow("'v=' + cast(f as varchar(60))", "v=1.23457e+009")]
    [DataRow("concat('v=', f)", "v=1.23457e+009")]
    [DataRow("concat_ws('-', 'a', f)", "a-1.23457e+009")]
    [DataRow("cast(cast(f as sql_variant) as varchar(60))", "1.23457e+009")]
    [DataRow("cast(json_object('f': f) as varchar(60))", """{"f":1.234567890000000e+009}""")]
    [DataRow("cast(json_array(f) as varchar(60))", "[1.234567890000000e+009]")]
    [DataRow("cast(json_modify('{\"a\":1}', '$.a', f) as varchar(60))", """{"a":1.234567890000000e+009}""")]
    [DataRow("cast((select f as a for json path) as varchar(60))", """[{"a":1.234567890000000e+009}]""")]
    [DataRow("cast((select f as a for xml path('r')) as varchar(60))", "<r><a>1.234567890000000e+009</a></r>")]
    public void StringSurfaces_SplitBetweenStyleZeroAndStyle126(string projection, string expected)
    {
        // Every ordinary string surface renders style 0; the JSON and XML
        // serializers render style 126 (source precision — sixteen significant
        // digits for float, eight for real).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (f float); insert t values (cast(1234567890 as float))");
        AreEqual(expected, sim.ExecuteScalar($"select {projection} from t"));
    }

    [TestMethod]
    public void KeyViolationMessage_RendersStyleZero()
    {
        var ex = new Simulation().AssertSqlError(
            """
            create table t (f float not null primary key);
            insert t values (cast(1e20 as float)), (cast(1e20 as float))
            """,
            2627);
        Assert.Contains("The duplicate key value is (1e+020).", ex.Message);
    }
}
