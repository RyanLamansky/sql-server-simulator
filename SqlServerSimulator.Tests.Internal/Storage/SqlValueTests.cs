using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
[TestClass]
public class SqlValueTests
{
    [TestMethod]
    public void FromInt32_RoundTripsViaAsInt32()
    {
        var v = SqlValue.FromInt32(42);
        IsFalse(v.IsNull);
        AreSame(SqlType.Int32, v.Type);
        AreEqual(42, v.AsInt32);
    }

    [TestMethod]
    public void FromInt64_RoundTripsViaAsInt64()
    {
        var v = SqlValue.FromInt64(long.MaxValue);
        AreSame(SqlType.BigInt, v.Type);
        AreEqual(long.MaxValue, v.AsInt64);
    }

    [TestMethod]
    public void FromInt16_RoundTripsViaAsInt16()
    {
        var v = SqlValue.FromInt16(short.MinValue);
        AreSame(SqlType.SmallInt, v.Type);
        AreEqual(short.MinValue, v.AsInt16);
    }

    [TestMethod]
    public void FromByte_RoundTripsViaAsByte()
    {
        var v = SqlValue.FromByte(200);
        AreSame(SqlType.TinyInt, v.Type);
        AreEqual((byte)200, v.AsByte);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FromBoolean_RoundTripsViaAsBoolean(bool value)
    {
        var v = SqlValue.FromBoolean(value);
        AreSame(SqlType.Bit, v.Type);
        AreEqual(value, v.AsBoolean);
    }

    [TestMethod]
    public void Null_IsNullAndCarriesType()
    {
        var v = SqlValue.Null(SqlType.Int32);
        IsTrue(v.IsNull);
        AreSame(SqlType.Int32, v.Type);
    }

    [TestMethod]
    public void Null_AsInt32Throws() =>
        Throws<InvalidOperationException>(() => SqlValue.Null(SqlType.Int32).AsInt32);

    [TestMethod]
    public void WrongType_AccessorThrows() =>
        Throws<InvalidOperationException>(() => SqlValue.FromInt32(1).AsInt64);

    [TestMethod]
    public void ImplicitInt_LiftsToFromInt32()
    {
        SqlValue v = 7;
        AreSame(SqlType.Int32, v.Type);
        AreEqual(7, v.AsInt32);
    }

    [TestMethod]
    public void Equals_ComparesTypeNullAndPayload()
    {
        AreEqual(SqlValue.FromInt32(1), SqlValue.FromInt32(1));
        AreNotEqual(SqlValue.FromInt32(1), SqlValue.FromInt32(2));
        AreEqual(SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.Int32));
        AreNotEqual(SqlValue.FromInt32(0), SqlValue.Null(SqlType.Int32));

        // Equal numeric value across different types is still not equal — type tag matters.
        AreNotEqual(SqlValue.FromInt32(1), SqlValue.FromInt64(1L));
    }

    [TestMethod]
    public void FromSystemName_RoundTripsViaAsString()
    {
        var v = SqlValue.FromSystemName("dbo");
        IsFalse(v.IsNull);
        AreSame(SqlType.SystemName, v.Type);
        AreEqual("dbo", v.AsString);
    }

    [TestMethod]
    public void FromSystemName_DistinctFromNVarcharSameText()
    {
        // sysname is identity-preserved across system catalogs even though its
        // bytes are encoded identically to nvarchar.
        AreNotEqual(SqlValue.FromSystemName("x"), SqlValue.FromNVarchar("x"));
    }

    [TestMethod]
    public void FromSystemName_RejectsNullArgument() =>
        Throws<ArgumentNullException>(() => SqlValue.FromSystemName(null!));

    [TestMethod]
    public void CoerceTo_SameType_ReturnsSelf()
    {
        var v = SqlValue.FromInt32(42);
        AreEqual(v, v.CoerceTo(SqlType.Int32));
    }

    [TestMethod]
    public void CoerceTo_NullReTypesWithoutOverflow()
    {
        var nullInt = SqlValue.Null(SqlType.Int32);
        var coerced = nullInt.CoerceTo(SqlType.TinyInt);
        IsTrue(coerced.IsNull);
        AreSame(SqlType.TinyInt, coerced.Type);
    }

    [TestMethod]
    [DataRow(0, (byte)0)]
    [DataRow(255, (byte)255)]
    [DataRow(42, (byte)42)]
    public void CoerceTo_Int32ToTinyInt_NarrowsInRange(int input, byte expected)
    {
        var coerced = SqlValue.FromInt32(input).CoerceTo(SqlType.TinyInt);
        AreSame(SqlType.TinyInt, coerced.Type);
        AreEqual(expected, coerced.AsByte);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(256)]
    [DataRow(int.MaxValue)]
    public void CoerceTo_Int32ToTinyInt_OutOfRangeOverflows(int input) =>
        Throws<OverflowException>(() => SqlValue.FromInt32(input).CoerceTo(SqlType.TinyInt));

    [TestMethod]
    public void CoerceTo_TinyIntToInt32_WidensExactly()
    {
        var coerced = SqlValue.FromByte(200).CoerceTo(SqlType.Int32);
        AreSame(SqlType.Int32, coerced.Type);
        AreEqual(200, coerced.AsInt32);
    }

    [TestMethod]
    public void CoerceTo_SmallIntToInt32_PreservesNegative()
    {
        var coerced = SqlValue.FromInt16(-12345).CoerceTo(SqlType.Int32);
        AreEqual(-12345, coerced.AsInt32);
    }

    [TestMethod]
    public void CoerceTo_Int32ToBigInt_Widens()
    {
        var coerced = SqlValue.FromInt32(int.MinValue).CoerceTo(SqlType.BigInt);
        AreEqual(int.MinValue, coerced.AsInt64);
    }

    [TestMethod]
    public void CoerceTo_BigIntToInt32_OutOfRangeOverflows() =>
        Throws<OverflowException>(() => SqlValue.FromInt64(int.MaxValue + 1L).CoerceTo(SqlType.Int32));

    [TestMethod]
    public void CoerceTo_VarcharToInt32_Parses()
    {
        var coerced = SqlValue.FromVarchar("42").CoerceTo(SqlType.Int32);
        AreSame(SqlType.Int32, coerced.Type);
        AreEqual(42, coerced.AsInt32);
    }

    [TestMethod]
    public void Date_AsDate_OnNull_Throws() =>
        Throws<InvalidOperationException>(() => SqlValue.Null(SqlType.Date).AsDate);

    [TestMethod]
    public void Date_AsDate_OnWrongType_Throws() =>
        Throws<InvalidOperationException>(() => SqlValue.FromInt32(1).AsDate);

    [TestMethod]
    public void FromDateTime2_RejectsNonDateTime2Type() =>
        Throws<ArgumentException>(() => SqlValue.FromDateTime2(SqlType.Int32, DateTime.UtcNow));

    [TestMethod]
    public void DateTime2_DifferentPrecisions_AreDistinctTypes()
    {
        // The reference-equality of Type tags isn't observable from SQL — this
        // pins the per-precision singleton invariant Promote/CoerceTo rely on.
        var v3 = SqlValue.FromDateTime2(SqlType.GetDateTime2(3), new DateTime(2026, 5, 4));
        var v7 = SqlValue.FromDateTime2(SqlType.GetDateTime2(7), new DateTime(2026, 5, 4));
        AreNotSame(v3.Type, v7.Type);
        AreNotEqual(v3, v7);
    }

    [TestMethod]
    public void FromTime_RejectsNonTimeType() =>
        Throws<ArgumentException>(() => SqlValue.FromTime(SqlType.Int32, TimeSpan.Zero));

    [TestMethod]
    public void Time_DifferentPrecisions_AreDistinctTypes()
    {
        var v3 = SqlValue.FromTime(SqlType.GetTime(3), new TimeSpan(13, 45, 30));
        var v7 = SqlValue.FromTime(SqlType.GetTime(7), new TimeSpan(13, 45, 30));
        AreNotSame(v3.Type, v7.Type);
        AreNotEqual(v3, v7);
    }

    [TestMethod]
    public void FromDateTimeOffset_RejectsNonDateTimeOffsetType() =>
        Throws<ArgumentException>(() => SqlValue.FromDateTimeOffset(SqlType.Int32, DateTimeOffset.UtcNow));

    [TestMethod]
    public void DateTimeOffset_DifferentPrecisions_AreDistinctTypes()
    {
        var v3 = SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(3), new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero));
        var v7 = SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(7), new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero));
        AreNotSame(v3.Type, v7.Type);
        AreNotEqual(v3, v7);
    }

}
