using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class CultureSensitiveInterpolationAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<CultureSensitiveInterpolationAnalyzer, DefaultVerifier>
        { TestCode = source, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync(this.TestContext.CancellationToken);

    // Month names, separators and the calendar all come from the culture.
    [TestMethod]
    public Task DatesAndTimes_Report() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static string[] Format(DateTime dt, DateOnly d, TimeOnly t, DateTimeOffset o, TimeSpan s) =>
                [
                    $"{|SSS014:{dt:dd MMM yyyy}|}",
                    $"x {|SSS014:{d:MM/dd/yy}|} y",
                    $"{|SSS014:{t:HH:mm:ss}|}",
                    $"{|SSS014:{o}|}",
                    $"{|SSS014:{s:g}|}",
                    $"{|SSS014:{dt}|}",
                ];
            }
            """);

    [TestMethod]
    public Task FloatingPointAndDecimal_Report() =>
        RunAsync("""
            internal static class Holder
            {
                public static string[] Format(double d, float f, decimal m, double? n) =>
                [
                    $"{|SSS014:{d}|}",
                    $"{|SSS014:{f:F2}|}",
                    $"{|SSS014:{m}|}",
                    $"{|SSS014:{n}|}",
                ];
            }
            """);

    // An integer's format draws from the culture only through a separator or symbol.
    [TestMethod]
    public Task IntegersWithCultureFormats_Report() =>
        RunAsync("""
            internal static class Holder
            {
                public static string[] Format(int i, long l) =>
                [
                    $"{|SSS014:{i:N0}|}",
                    $"{|SSS014:{l:#,0}|}",
                    $"{|SSS014:{i:P}|}",
                ];
            }
            """);

    [TestMethod]
    public Task CultureFreeHoles_DoNotReport() =>
        RunAsync("""
            using System;
            internal enum Kind { A }
            internal static class Holder
            {
                public static string[] Format(int i, long l, byte b, string s, char c, bool f, Kind k, Guid g, TimeSpan t, object o) =>
                [
                    $"{i} {l:00} {b:X2} {i:x} {i:D4}",
                    $"{s} {c} {f} {k} {g}",
                    $"{t} {t:c}",
                    $"{o}",
                ];
            }
            """);

    // The caller chose the provider: an invariant handler, or a FormattableString.
    [TestMethod]
    public Task ProviderChosenByCaller_DoesNotReport() =>
        RunAsync("""
            using System;
            using System.Globalization;
            using System.Text;
            internal static class Holder
            {
                public static object[] Format(DateTime dt, double d, StringBuilder sb) =>
                [
                    string.Create(CultureInfo.InvariantCulture, $"{dt:dd MMM yyyy} {d}"),
                    sb.Append(CultureInfo.InvariantCulture, $"{d}"),
                    FormattableString.Invariant($"{d}"),
                    (IFormattable)$"{d}",
                ];
            }
            """);

    // A handler with no provider formats with the current culture all the same.
    [TestMethod]
    public Task HandlerWithoutProvider_Reports() =>
        RunAsync("""
            using System.Text;
            internal static class Holder
            {
                public static StringBuilder Format(double d, StringBuilder sb) => sb.Append($"{|SSS014:{d}|}");
            }
            """);
}
