using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The arithmetic a <c>WAITFOR TIME</c> waits by, held to a fixed clock: the
/// public tests read the live one, which never sits beside midnight on demand
/// and can't wait out a day.
/// </summary>
[TestClass]
public sealed class WaitForTimeDelayTests
{
    [TestMethod]
    [DataRow("10:00:00", "10:00:00.300", 300L)]
    [DataRow("10:00:00", "10:00:00", 0L)]
    [DataRow("10:00:00.005", "10:00:00", 86_399_995L)]
    [DataRow("23:59:59.900", "00:00:00.100", 200L)]
    [DataRow("00:00:00.100", "00:00:00", 86_399_900L)]
    [DataRow("12:00:00", "00:00:00", 43_200_000L)]
    public void WaitsUntilTheNextTimeTheClockReadsTheTarget(string now, string target, long expectedMilliseconds)
    {
        var clock = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.Parse(now, CultureInfo.InvariantCulture);
        var delay = Simulation.WaitForTimeDelay(TimeSpan.Parse(target, CultureInfo.InvariantCulture), clock);
        AreEqual(expectedMilliseconds, (long)delay.TotalMilliseconds);
    }
}
