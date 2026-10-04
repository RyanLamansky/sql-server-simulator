using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class HostClockAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<HostClockAnalyzer, DefaultVerifier>
        { TestCode = source }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task LocalClockProperties_Report() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static object[] Read() =>
                [
                    {|SSS015:DateTime.Now|},
                    {|SSS015:DateTime.Today|},
                    {|SSS015:DateTimeOffset.Now|},
                    {|SSS015:TimeZoneInfo.Local|},
                ];
            }
            """);

    [TestMethod]
    public Task ToLocalTime_Reports() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static DateTime Convert(DateTime utc) => {|SSS015:utc.ToLocalTime()|};
                public static DateTimeOffset Convert(DateTimeOffset utc) => {|SSS015:utc.ToLocalTime()|};
            }
            """);

    // The UTC clock and a time zone looked up by name are what the rule points to.
    [TestMethod]
    public Task UtcClockAndNamedZones_DoNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
                public static object[] Read() =>
                [
                    DateTime.UtcNow,
                    DateTimeOffset.UtcNow,
                    TimeZoneInfo.Utc,
                    TimeZoneInfo.FindSystemTimeZoneById("UTC"),
                    DateTime.UtcNow.ToUniversalTime(),
                ];
            }
            """);

    // A same-named member of another type is not the host clock.
    [TestMethod]
    public Task SameNamedMembersElsewhere_DoNotReport() =>
        RunAsync("""
            internal sealed class Clock
            {
                public System.DateTime Now => default;
                public static System.DateTime Today => default;
                public Clock ToLocalTime() => this;
                public object Read() => (this.Now, Today, this.ToLocalTime());
            }
            """);

    [TestMethod]
    public Task Suppressed_DoesNotReport() =>
        RunAsync("""
            using System;
            internal static class Holder
            {
            #pragma warning disable SSS015 // the host's wall clock, for a log line
                public static DateTime Read() => DateTime.Now;
            #pragma warning restore SSS015
            }
            """);
}
