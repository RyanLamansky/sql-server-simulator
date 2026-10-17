using SqlServerSimulator.Network;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A row goes out as an NBCROW — one bit per column, then the non-NULL values
/// alone — exactly when the NULL forms it leaves out outweigh its bitmap, and
/// as a ROW otherwise, a tie included, row by row (captured 2026-10-17 against
/// SQL Server 2025 with a raw TDS client).
/// </summary>
[TestClass]
public sealed class NbcRowTokenTests
{
    private static readonly SqlType NVarcharMax = SqlType.NVarcharMax;

    private static readonly SqlType Varchar5 = VarcharSqlType.Get(5, Collation.Baseline, Coercibility.Implicit);

    /// <summary>One NULL <c>int</c> saves the byte its bitmap costs, so it stays a ROW; two save two.</summary>
    [TestMethod]
    public void NullIntegers_CompressOnceTheyOutweighTheBitmap()
    {
        AreEqual(Tds.TokenRow, Row([SqlType.Int32, SqlType.Int32], [SqlValue.FromInt32(1), SqlValue.Null(SqlType.Int32)])[0]);
        CollectionAssert.AreEqual(new byte[] { Tds.TokenNbcRow, 0x03 }, Row([SqlType.Int32, SqlType.Int32], [SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.Int32)]));
    }

    /// <summary>A NULL <c>varchar</c>'s two bytes outweigh one bitmap byte, but not the two of a nine-column row.</summary>
    [TestMethod]
    public void NullString_WeighsItsUshortLength()
    {
        CollectionAssert.AreEqual(new byte[] { Tds.TokenNbcRow, 0x01 }, Row([Varchar5], [SqlValue.Null(Varchar5)]));
        SqlType[] nine = [.. Enumerable.Repeat(SqlType.Int32, 8), Varchar5];
        AreEqual(Tds.TokenRow, Row(nine, [.. Enumerable.Repeat(SqlValue.FromInt32(1), 8), SqlValue.Null(Varchar5)])[0]);
    }

    /// <summary>A NULL <c>nvarchar(max)</c>'s eight-byte PLP sentinel outweighs a bitmap up to 56 columns.</summary>
    [TestMethod]
    public void NullMaxString_WeighsItsPlpSentinel()
    {
        AreEqual(Tds.TokenNbcRow, Row(WithInts(NVarcharMax, 55), [SqlValue.Null(NVarcharMax), .. Ones(55)])[0]);
        AreEqual(Tds.TokenRow, Row(WithInts(NVarcharMax, 56), [SqlValue.Null(NVarcharMax), .. Ones(56)])[0]);
    }

    /// <summary>A legacy LOB NULL is one byte, as an <c>int</c>'s is.</summary>
    [TestMethod]
    public void NullText_WeighsOneByte() => AreEqual(Tds.TokenRow, Row([SqlType.Text], [SqlValue.Null(SqlType.Text)])[0]);

    /// <summary>The bitmap's bit <c>n % 8</c> of byte <c>n / 8</c> marks column <c>n</c>, and only the other columns' values follow.</summary>
    [TestMethod]
    public void Bitmap_MarksEachNullColumnAndSkipsItsValue()
    {
        SqlType[] schema = [Varchar5, .. Enumerable.Repeat(SqlType.Int32, 7), Varchar5];
        var row = Row(schema, [SqlValue.Null(Varchar5), .. Ones(7), SqlValue.Null(Varchar5)]);
        AreEqual(Tds.TokenNbcRow, row[0]);
        AreEqual(0x01, row[1]);
        AreEqual(0x01, row[2]);
        // Seven length-prefixed ints, then nothing for the trailing NULL.
        HasCount(3 + (7 * 5), row);
        CollectionAssert.AreEqual(new byte[] { 4, 1, 0, 0, 0 }, row[3..8]);
    }

    private static SqlType[] WithInts(SqlType first, int count) => [first, .. Enumerable.Repeat(SqlType.Int32, count)];

    private static IEnumerable<SqlValue> Ones(int count) => Enumerable.Repeat(SqlValue.FromInt32(1), count);

    /// <summary>The token bytes of one row of nullable columns.</summary>
    private static byte[] Row(SqlType[] schema, SqlValue[] values)
    {
        var stream = new MemoryStream();
        var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
        var writer = new TdsTokenWriter(transport);
        var names = schema.Select(static (_, i) => "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var result = new SimulatedSqlResultSet(schema, names, [values]);
        using (var cursor = result.CreateCursor())
        {
            while (cursor.MoveNext())
                TdsTypeCodec.WriteRow(writer, schema, cursor, [.. Enumerable.Repeat(true, schema.Length)]);
        }

        writer.FlushAsync(final: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return stream.ToArray()[Tds.HeaderSize..];
    }
}
