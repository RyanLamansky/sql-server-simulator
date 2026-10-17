using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="RowAddressMap"/> answers as an identity dictionary whose later
/// record of an instance wins, whichever of its two lookup paths — the newest
/// record or the lazily built index — a lookup takes.
/// </summary>
[TestClass]
public sealed class RowAddressMapTests
{
    [TestMethod]
    public void NewestRecordAnswersWithoutTheIndex()
    {
        var map = new RowAddressMap();
        byte[] row = [1];
        map.Record(row, 3, 4);
        IsTrue(map.TryGet(row, out var address));
        AreEqual((3, 4), address);
        IsFalse(map.TryGet([1], out _));
    }

    [TestMethod]
    public void OlderRecordsAnswerAcrossSegmentsAndLaterRecords()
    {
        var map = new RowAddressMap();
        var rows = new byte[10_000][];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = [(byte)i];
            map.Record(rows[i], i / 100, i % 100);
            if (i % 997 == 0)
            {
                IsTrue(map.TryGet(rows[i / 2], out var early));
                AreEqual((i / 2 / 100, i / 2 % 100), early);
            }
        }

        for (var i = rows.Length - 1; i >= 0; i -= 7)
        {
            IsTrue(map.TryGet(rows[i], out var address));
            AreEqual((i / 100, i % 100), address);
        }
    }

    [TestMethod]
    public void ALaterRecordOfOneInstanceWins()
    {
        var map = new RowAddressMap();
        byte[] repeated = [1];
        byte[] other = [2];
        map.Record(repeated, 1, 1);
        map.Record(other, 2, 2);
        map.Record(repeated, 3, 3);
        IsTrue(map.TryGet(other, out var otherAddress));
        AreEqual((2, 2), otherAddress);
        map.Record(other, 4, 4);
        IsTrue(map.TryGet(repeated, out var repeatedAddress));
        AreEqual((3, 3), repeatedAddress);
        IsTrue(map.TryGet(other, out otherAddress));
        AreEqual((4, 4), otherAddress);
    }
}
