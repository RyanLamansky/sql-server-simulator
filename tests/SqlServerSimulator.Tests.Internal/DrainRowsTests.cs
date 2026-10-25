using System.Runtime.CompilerServices;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A variable-assigning <c>SELECT</c> runs its rows through
/// <see cref="SimulatedSqlResultSet.DrainRows"/>, which must let each row go
/// once the next is produced: kept, the rows of <c>SELECT @v = @v + s FROM t</c>
/// hold every prefix of the string it builds.
/// </summary>
[TestClass]
public sealed class DrainRowsTests
{
    private static IEnumerable<SqlValue[]> TrackedRows(List<WeakReference> produced)
    {
        for (var i = 0; i < 3; i++)
        {
            var row = new[] { SqlValue.FromInt32(i) };
            produced.Add(new WeakReference(row));
            yield return row;
        }
    }

    // Not inlined, so no caller frame keeps a row reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SimulatedSqlResultSet Drained(List<WeakReference> produced)
    {
        var results = new SimulatedSqlResultSet([SqlType.Int32], ["a"], TrackedRows(produced));
        AreEqual(3, results.DrainRows());
        return results;
    }

    [TestMethod]
    public void DrainRows_CountsTheRowsWithoutKeepingThem()
    {
        List<WeakReference> produced = [];
        var results = Drained(produced);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The producer itself still holds the row it yielded last.
        IsFalse(produced[0].IsAlive);
        IsFalse(produced[1].IsAlive);
        GC.KeepAlive(results);
    }
}
