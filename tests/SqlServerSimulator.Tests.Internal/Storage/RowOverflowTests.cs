using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests for <see cref="RowEncoder"/>'s row-overflow pass:
/// when a row's encoded size would exceed <see cref="Heap.MaxRowSize"/>,
/// the encoder pushes the largest still-inline bounded variable-length
/// column off-row through <see cref="Heap.AllocateLobChain"/> and repeats
/// until the row fits.
/// </summary>
[TestClass]
public sealed class RowOverflowTests
{
    [TestMethod]
    public void EncodeRow_RowFitsWithoutOverflow_NoChainAllocations()
    {
        // 7000 bytes of varchar plus the row header is well under the 8060
        // cap; no value should be pushed off-row.
        var heap = new Heap();
        var schema = new[]
        {
            new HeapColumn("a", SqlType.Varchar, maxLength: 8000, nullable: true),
        };
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromVarchar(new string('a', 7000))], lobStore: heap);
        IsLessThanOrEqualTo(Heap.MaxRowSize, bytes.Length);
        IsEmpty(heap.LobPages);
    }

    [TestMethod]
    public void EncodeRow_TwoMaxBoundedVarchars_PushesLargestThenSecond()
    {
        // Two 8000-byte values can't both fit in 8060; the encoder pushes
        // the first (it's the largest among ties — index order tiebreak),
        // recomputes, and may need to push the second too.
        var heap = new Heap();
        var schema = new[]
        {
            new HeapColumn("a", SqlType.Varchar, maxLength: 8000, nullable: true),
            new HeapColumn("b", SqlType.Varchar, maxLength: 8000, nullable: true),
        };
        var aValue = new string('A', 8000);
        var bValue = new string('B', 8000);
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromVarchar(aValue), SqlValue.FromVarchar(bValue)], lobStore: heap);

        IsLessThanOrEqualTo(Heap.MaxRowSize, bytes.Length);
        IsNotEmpty(heap.LobPages);

        var decoded = RowDecoder.DecodeRow(schema, bytes, lobStore: heap);
        AreEqual(aValue, decoded[0].AsString);
        AreEqual(bValue, decoded[1].AsString);
    }

    [TestMethod]
    public void EncodeRow_MixedSizes_LargestPushedFirst()
    {
        // Short value alongside a value that on its own would push the row
        // over the limit. The short value must remain inline; only the
        // large one goes off-row.
        var heap = new Heap();
        var schema = new[]
        {
            new HeapColumn("small", SqlType.Varchar, maxLength: 8000, nullable: true),
            new HeapColumn("large", SqlType.Varchar, maxLength: 8000, nullable: true),
        };
        var smallValue = new string('s', 50);
        var largeValue = new string('L', 8000);
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromVarchar(smallValue), SqlValue.FromVarchar(largeValue)], lobStore: heap);

        IsLessThanOrEqualTo(Heap.MaxRowSize, bytes.Length);
        IsNotEmpty(heap.LobPages);

        var decoded = RowDecoder.DecodeRow(schema, bytes, lobStore: heap);
        AreEqual(smallValue, decoded[0].AsString);
        AreEqual(largeValue, decoded[1].AsString);
    }

    [TestMethod]
    public void EncodeRow_NullVarColumn_NotPushedOffRow()
    {
        // A NULL var column contributes 0 bytes to the var section and
        // shouldn't appear in the overflow candidate list.
        var heap = new Heap();
        var schema = new[]
        {
            new HeapColumn("a", SqlType.Varchar, maxLength: 8000, nullable: true),
            new HeapColumn("b", SqlType.Varchar, maxLength: 8000, nullable: true),
        };
        var aValue = new string('A', 8000);
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromVarchar(aValue), SqlValue.Null(SqlType.Varchar)], lobStore: heap);

        IsLessThanOrEqualTo(Heap.MaxRowSize, bytes.Length);

        var decoded = RowDecoder.DecodeRow(schema, bytes, lobStore: heap);
        AreEqual(aValue, decoded[0].AsString);
        IsTrue(decoded[1].IsNull);
    }

    [TestMethod]
    public void EncodeRow_ImpossibleRow_RaisesMsg511()
    {
        // 1024 bigint columns = 8192 bytes of fixed section alone — no
        // var-column push can drop us under 8060. CREATE TABLE would have
        // raised Msg 1701 here, but the encoder is reachable independently
        // and must surface Msg 511 in that case.
        //
        // Wording verified against SQL Server 2025: probing 1023×varchar(50)
        // each at 50 bytes yields exactly
        //   "Cannot create a row of size 26734 which is greater than the
        //    allowable maximum row size of 8060."
        // The simulator's pointer width differs from SQL Server's, so the
        // reported size won't match byte-for-byte; the format does.
        var schema = new HeapColumn[1024 + 1];
        var values = new SqlValue[1024 + 1];
        for (var i = 0; i < 1024; i++)
        {
            schema[i] = new HeapColumn($"c{i}", SqlType.BigInt, maxLength: null, nullable: false);
            values[i] = SqlValue.FromInt64(i);
        }
        schema[1024] = new HeapColumn("v", SqlType.Varchar, maxLength: 100, nullable: true);
        values[1024] = SqlValue.FromVarchar("x");

        var heap = new Heap();
        var ex = Throws<SimulatedSqlException>(() => RowEncoder.EncodeRow(schema, values, lobStore: heap));
        AreEqual(511, ex.Number);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        StartsWith("Cannot create a row of size ", ex.Message);
        EndsWith($" which is greater than the allowable maximum row size of {Heap.MaxRowSize}.", ex.Message);
    }

    [TestMethod]
    public void EncodeRow_NoLobStore_DoesNotOverflow()
    {
        // Without a heap, the encoder has nowhere to push values, so the row
        // is emitted as-is regardless of size. (Public-API call sites always
        // supply a heap; this surface is for projection result-set rows
        // that don't go through page storage.)
        var schema = new[]
        {
            new HeapColumn("a", SqlType.Varchar, maxLength: 8000, nullable: true),
            new HeapColumn("b", SqlType.Varchar, maxLength: 8000, nullable: true),
        };
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromVarchar(new string('A', 8000)), SqlValue.FromVarchar(new string('B', 8000))], lobStore: null);
        IsGreaterThan(Heap.MaxRowSize, bytes.Length);
    }
}
