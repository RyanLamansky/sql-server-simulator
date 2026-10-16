using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class RuntimeDependentNumericsAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<RuntimeDependentNumericsAnalyzer, DefaultVerifier>
        { TestCode = source, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task BinaryFloatToDecimal_Reports() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static decimal[] Convert(double d, float f) =>
                [
                    {|SSS019:(decimal)d|},
                    {|SSS019:(decimal)f|},
                    checked({|SSS019:(decimal)d|}),
                    {|SSS019:System.Convert.ToDecimal(d)|},
                    {|SSS019:System.Convert.ToDecimal(f)|},
                    {|SSS019:new decimal(d)|},
                    {|SSS019:decimal.CreateChecked(d)|},
                    {|SSS019:decimal.CreateTruncating(f)|},
                ];
            }
            """);

    [TestMethod]
    public Task DigitRoundingAndAngles_Report() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static object[] Compute(double d, float f) =>
                [
                    {|SSS019:Math.Round(d, 2)|},
                    {|SSS019:Math.Round(d, 2, MidpointRounding.AwayFromZero)|},
                    {|SSS019:MathF.Round(f, 2)|},
                    {|SSS019:double.Round(d, 2)|},
                    {|SSS019:double.RadiansToDegrees(d)|},
                    {|SSS019:float.DegreesToRadians(f)|},
                ];
            }
            """);

    // Rounding to an integer, decimal sources, and an integer or object into
    // Convert.ToDecimal read the same on every runtime.
    [TestMethod]
    public Task StableOperations_DoNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static object[] Compute(double d, decimal m, int i, object o) =>
                [
                    Math.Round(d),
                    Math.Round(d, MidpointRounding.AwayFromZero),
                    Math.Round(m, 2),
                    (decimal)i,
                    (double)m,
                    Convert.ToDecimal(i),
                    Convert.ToDecimal(o),
                    new decimal(i),
                    d * 180 / Math.PI,
                ];
            }
            """);

    [TestMethod]
    public Task Suppressed_DoesNotReport() =>
        RunAsync("""
            internal static class Holder
            {
            #pragma warning disable SSS019 // the runtime's own reading is the point
                public static decimal Convert(double d) => (decimal)d;
            #pragma warning restore SSS019
            }
            """);
}
