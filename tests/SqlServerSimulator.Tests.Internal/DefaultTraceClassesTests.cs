using SqlServerSimulator.Trace;

namespace SqlServerSimulator;

/// <summary>
/// The default trace's event classes are written into
/// <see cref="TraceEvents.DefaultTraceClasses"/> so a firing site asks without
/// loading the trace catalog; they must stay the catalog's own.
/// </summary>
[TestClass]
public sealed class DefaultTraceClassesTests
{
    [TestMethod]
    public void DefaultTraceClasses_AreTheCatalogsDefaultTraceEvents() =>
        CollectionAssert.AreEqual(
            TraceCatalog.Instance.DefaultTraceEvents.Select(pair => (short)pair.EventId).Distinct().Order().ToArray(),
            TraceEvents.DefaultTraceClasses);
}
