using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
/// <remarks>
/// Covers <see cref="RowDecoder"/>, which the data reader uses to navigate
/// row bytes one column at a time without materializing the whole row.
/// </remarks>
[TestClass]
public sealed class DecodeColumnTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void EachFixedColumn_DecodesIndependently(int ordinal)
    {
        SqlType[] schema = [SqlType.Int32, SqlType.BigInt, SqlType.SmallInt];
        SqlValue[] values = [SqlValue.FromInt32(11), SqlValue.FromInt64(22L), SqlValue.FromInt16(33)];
        var bytes = RowEncoder.EncodeRow(schema, values);

        AreEqual(values[ordinal], RowDecoder.DecodeColumn(schema, bytes, ordinal));
    }

    [TestMethod]
    public void NullColumn_DecodesAsTypedNull()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Int32];
        SqlValue[] values = [SqlValue.FromInt32(1), SqlValue.Null(SqlType.Int32)];
        var bytes = RowEncoder.EncodeRow(schema, values);

        var decoded = RowDecoder.DecodeColumn(schema, bytes, 1);
        IsTrue(decoded.IsNull);
        AreEqual(SqlType.Int32, decoded.Type);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void MixedFixedAndVarColumns_DecodeIndependently(int ordinal)
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Varchar, SqlType.Int32];
        SqlValue[] values = [SqlValue.FromInt32(1), SqlValue.FromVarchar("hello"), SqlValue.FromInt32(2)];
        var bytes = RowEncoder.EncodeRow(schema, values);

        AreEqual(values[ordinal], RowDecoder.DecodeColumn(schema, bytes, ordinal));
    }

    [TestMethod]
    public void TwoVarColumns_SecondVarColumn_DecodesCorrectly()
    {
        SqlType[] schema = [SqlType.Varchar, SqlType.Varchar];
        SqlValue[] values = [SqlValue.FromVarchar("alpha"), SqlValue.FromVarchar("omega")];
        var bytes = RowEncoder.EncodeRow(schema, values);

        AreEqual(values[0], RowDecoder.DecodeColumn(schema, bytes, 0));
        AreEqual(values[1], RowDecoder.DecodeColumn(schema, bytes, 1));
    }

    [TestMethod]
    public void NullVarColumn_BetweenTwoNonNullVarColumns_DecodesCorrectly()
    {
        SqlType[] schema = [SqlType.Varchar, SqlType.Varchar, SqlType.Varchar];
        SqlValue[] values = [SqlValue.FromVarchar("a"), SqlValue.Null(SqlType.Varchar), SqlValue.FromVarchar("c")];
        var bytes = RowEncoder.EncodeRow(schema, values);

        AreEqual(values[0], RowDecoder.DecodeColumn(schema, bytes, 0));
        IsTrue(RowDecoder.DecodeColumn(schema, bytes, 1).IsNull);
        AreEqual(values[2], RowDecoder.DecodeColumn(schema, bytes, 2));
    }

    [TestMethod]
    public void OutOfRange_Throws()
    {
        SqlType[] schema = [SqlType.Int32];
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromInt32(1)]);

        _ = Throws<ArgumentOutOfRangeException>(() => RowDecoder.DecodeColumn(schema, bytes, 1));
        _ = Throws<ArgumentOutOfRangeException>(() => RowDecoder.DecodeColumn(schema, bytes, -1));
    }

    /// <summary>
    /// The type-only schema's <see cref="HeapColumn"/>[] conversion must be
    /// cached by schema-array identity: a fresh array per call would defeat
    /// <see cref="RowLayout"/>'s identity-keyed cache, re-laying-out the row
    /// geometry on every single-column read (measured at a third of
    /// result-drain CPU before the conversion was cached).
    /// </summary>
    [TestMethod]
    public void ColumnsFor_SameSchemaArray_ReturnsCachedInstance()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Varchar];

        AreSame(RowDecoder.ColumnsFor(schema), RowDecoder.ColumnsFor(schema));
        AreNotSame(RowDecoder.ColumnsFor(schema), RowDecoder.ColumnsFor([SqlType.Int32, SqlType.Varchar]));
    }
}
