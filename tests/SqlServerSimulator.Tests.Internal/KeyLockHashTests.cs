using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="KeyLockHash"/> over a non-unique nonclustered index of a heap,
/// whose key real hashes with the row's RID: the descriptions
/// <c>%%lockres%%</c> gave for <c>create index ix_b on h (b)</c> on SQL Server
/// 2025 (probed 2026-10-17), the rows on page 696 as <c>%%physloc%%</c>
/// reported, a page id no simulated table reaches.
/// </summary>
[TestClass]
public sealed class KeyLockHashTests
{
    [TestMethod]
    [DataRow(10, 0, "(102c82108f60)")]
    [DataRow(10, 1, "(7b068bcf0133)")]
    [DataRow(20, 2, "(b26c46bba335)")]
    [DataRow(null, 3, "(eb91be559d3d)")]
    [DataRow(30, 0, "(a3d5753fbb6a)")]
    public void HeapIndexKey_HashesTheRid(int? key, int slot, string expected)
        => AreEqual(expected, KeyLockHash.Describe(
            new Parser.SqlValueKey([key is { } value ? SqlValue.FromInt32(value) : SqlValue.Null(SqlType.Int32)]),
            [SqlType.Int32],
            KeyLockUniquifier.None,
            (696, slot)));
}
