using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for fixed-length string and binary types (<c>char(N)</c>,
/// <c>nchar(N)</c>, <c>binary(N)</c>): right-padding, silent CAST truncation,
/// INSERT-time truncation errors (Msg 2628 / 8152), trailing-space-aware comparison,
/// and width-validation error variants.
/// </summary>
[TestClass]
public sealed class FixedLengthStringTests
{
    [TestMethod]
    public void Cast_String_PadsToDeclaredLength() => AreEqual("abc  ", ExecuteScalar("select cast('abc' as char(5))"));

    [TestMethod]
    public void Cast_NString_PadsNcharToDeclaredCodeUnits() => AreEqual("abc  ", ExecuteScalar("select cast(N'abc' as nchar(5))"));

    [TestMethod]
    public void Cast_Varbinary_PadsBinaryWithZeros()
    {
        var value = ExecuteScalar("select cast(0xCAFE as binary(5))") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0, 0 }, value);
    }

    // CAST silently truncates without raising — error is reserved for INSERT/UPDATE.
    [TestMethod]
    public void Cast_OversizeString_SilentlyTruncatesInCast()
        => AreEqual("hello", ExecuteScalar("select cast('hello world' as char(5))"));

    [TestMethod]
    public void Cast_OversizeBinary_SilentlyTruncatesInCast()
    {
        var value = ExecuteScalar("select cast(0x0102030405060708 as binary(5))") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, value);
    }

    [TestMethod]
    public void Cast_EmptyString_PadsToFullWidth()
        => AreEqual("   ", ExecuteScalar("select cast('' as char(3))"));

    // ANSI trailing-space padding shared by every string-family type.
    [TestMethod]
    public void Comparison_CharWithVarchar_StripsTrailingSpaces()
    {
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = 'abc'"));
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = 'abc  '"));
    }

    [TestMethod]
    public void Comparison_DifferentDeclaredLengths_StillEqualByContent()
        => AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = cast('abc' as char(10))"));

    [TestMethod]
    public void CreateTable_InsertSelect_RoundTripsPaddedValue()
        => AreEqual("hi   ", new Simulation().ExecuteScalar("""
            create table t (c char(5));
            insert t values ('hi');
            select c from t
            """));

    [TestMethod]
    public void CreateTable_NCharRoundTrip_PadsToDeclaredCodeUnits()
        => AreEqual("hi   ", new Simulation().ExecuteScalar("""
            create table t (c nchar(5));
            insert t values (N'hi');
            select c from t
            """));

    [TestMethod]
    public void CreateTable_BinaryRoundTrip_PadsWithZeros()
    {
        var value = new Simulation().ExecuteScalar("""
            create table t (b binary(5));
            insert t values (0xCAFE);
            select b from t
            """) as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0, 0 }, value);
    }

    [TestMethod]
    public void Insert_OversizeString_RaisesTruncationError()
        => new Simulation().AssertSqlError("""
            create table t (c char(5));
            insert t values ('toolong')
            """, 2628,
            "String or binary data would be truncated in table 't', column 'c'. Truncated value: 'toolo'.");

    [TestMethod]
    public void Insert_OversizeNChar_RaisesTruncationError()
        => new Simulation().AssertSqlError("""
            create table t (c nchar(5));
            insert t values (N'toolong')
            """, 2628,
            "String or binary data would be truncated in table 't', column 'c'. Truncated value: 'toolo'.");

    [TestMethod]
    public void Insert_OversizeBinary_RaisesTruncationError()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (b binary(5));
            insert t values (0x010203040506)
            """, 2628);
        Assert.StartsWith("String or binary data would be truncated in table 't', column 'b'.", ex.Message);
    }

    [TestMethod]
    public void Cast_CharZero_RaisesMsg1001()
        => AssertSqlMessage("select cast('a' as char(0))", "Line 1: Length or precision specification 0 is invalid.");

    [TestMethod]
    public void Cast_CharOversize_RaisesMsg131WithTypeWording()
        => AssertSqlMessage("select cast('a' as char(8001))",
            "The size (8001) given to the type 'char' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void Cast_NCharOversize_RaisesMsg131WithConvertSpecificationWording()
        => AssertSqlMessage("select cast(N'a' as nchar(4001))",
            "The size (4001) given to the convert specification 'nchar' exceeds the maximum allowed for any data type (4000).");

    [TestMethod]
    public void Cast_BinaryOversize_RaisesMsg131WithBinaryWording()
        => AssertSqlMessage("select cast(0xab as binary(8001))",
            "The size (8001) given to the type 'binary' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void CreateTable_CharOversize_RaisesMsg131WithColumnWording()
        => new Simulation().AssertSqlError("create table t (c char(8001))", 131,
            "The size (8001) given to the column 'c' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void CreateTable_NCharOversize_RaisesMsg2717ParameterWording()
        => new Simulation().AssertSqlError("create table t (c nchar(4001))", 2717,
            "The size (4001) given to the parameter 'c' exceeds the maximum allowed (4000).");

    // CAST without parens defaults to 30 (column declaration default is 1).
    [TestMethod]
    public void Cast_Default_NoParensInCastIs30()
        => AreEqual(30, ExecuteScalar("select datalength(cast('abc' as char))"));

    // Column declaration default is 1.
    [TestMethod]
    public void CreateTable_DefaultIsOne()
    {
        var simulation = new Simulation();
        AreEqual("a", simulation.ExecuteScalar("""
            create table t (c char);
            insert t values ('a');
            select c from t
            """));
        _ = simulation.AssertSqlError("insert t values ('hello')", 2628);
    }

    [TestMethod]
    public void Cast_UidToCharBelow36_RaisesMsg8170()
        => AssertSqlMessage("select cast(NEWID() as char(35))",
            "Insufficient result space to convert uniqueidentifier value to char.");

    // nchar's overflow message names "nvarchar" — same shared text path.
    [TestMethod]
    public void Cast_UidToNCharBelow36_RaisesMsg8115WithNvarcharText()
        => AssertSqlMessage("select cast(NEWID() as nchar(35))",
            "Arithmetic overflow error converting expression to data type nvarchar.");

    [TestMethod]
    public void Cast_VarcharToNvarchar_PreservesValue()
        => AreEqual("abc", ExecuteScalar("select cast('abc' as nvarchar(10))"));

    [TestMethod]
    public void Insert_WithLegacyCompatibility_RaisesMsg8152NoColumnDetail()
        => new Simulation().AssertSqlError("""
            alter database current set compatibility_level = 150;
            create table t (c char(5));
            insert t values ('toolong')
            """, 8152, "String or binary data would be truncated.");

    // Padded form is part of the value; varchar comparison still strips via ANSI padding.
    [TestMethod]
    public void Cast_CharToVarchar_TrailingSpacesPreservedInString()
        => AreEqual(1, ExecuteScalar("select 1 where cast(cast('abc' as char(5)) as varchar(10)) = 'abc'"));
}
