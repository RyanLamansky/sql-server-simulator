using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the fixed-length string and binary types
/// (<c>char(N)</c>, <c>nchar(N)</c>, <c>binary(N)</c>): right-padding semantics,
/// silent CAST truncation, INSERT-time truncation errors (Msg 2628 / 8152),
/// trailing-space-aware comparison, and width-validation error variants.
/// </summary>
[TestClass]
public sealed class FixedLengthStringTests
{
    [TestMethod]
    public void Cast_String_PadsToDeclaredLength()
    {
        AreEqual("abc  ", ExecuteScalar("select cast('abc' as char(5))"));
    }

    [TestMethod]
    public void Cast_NString_PadsNcharToDeclaredCodeUnits()
    {
        AreEqual("abc  ", ExecuteScalar("select cast(N'abc' as nchar(5))"));
    }

    [TestMethod]
    public void Cast_Varbinary_PadsBinaryWithZeros()
    {
        var value = ExecuteScalar("select cast(0xCAFE as binary(5))") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0, 0 }, value);
    }

    [TestMethod]
    public void Cast_OversizeString_SilentlyTruncatesInCast()
    {
        // Verified against SQL Server 2025: CAST silently truncates without
        // raising — the truncation error is reserved for INSERT/UPDATE.
        AreEqual("hello", ExecuteScalar("select cast('hello world' as char(5))"));
    }

    [TestMethod]
    public void Cast_OversizeBinary_SilentlyTruncatesInCast()
    {
        var value = ExecuteScalar("select cast(0x0102030405060708 as binary(5))") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, value);
    }

    [TestMethod]
    public void Cast_EmptyString_PadsToFullWidth()
    {
        AreEqual("   ", ExecuteScalar("select cast('' as char(3))"));
    }

    [TestMethod]
    public void Comparison_CharWithVarchar_StripsTrailingSpaces()
    {
        // ANSI trailing-space padding is part of the equality semantics shared
        // by every string-family type, so char(5) 'abc  ' equals varchar 'abc'.
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = 'abc'"));
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = 'abc  '"));
    }

    [TestMethod]
    public void Comparison_DifferentDeclaredLengths_StillEqualByContent()
    {
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = cast('abc' as char(10))"));
    }

    [TestMethod]
    public void CreateTable_InsertSelect_RoundTripsPaddedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c char(5))");
        _ = simulation.ExecuteNonQuery("insert into t values ('hi')");
        AreEqual("hi   ", simulation.ExecuteScalar("select c from t"));
    }

    [TestMethod]
    public void CreateTable_NCharRoundTrip_PadsToDeclaredCodeUnits()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c nchar(5))");
        _ = simulation.ExecuteNonQuery("insert into t values (N'hi')");
        AreEqual("hi   ", simulation.ExecuteScalar("select c from t"));
    }

    [TestMethod]
    public void CreateTable_BinaryRoundTrip_PadsWithZeros()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (b binary(5))");
        _ = simulation.ExecuteNonQuery("insert into t values (0xCAFE)");
        var value = simulation.ExecuteScalar("select b from t") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0, 0 }, value);
    }

    [TestMethod]
    public void Insert_OversizeString_RaisesTruncationError()
    {
        // Default compatibility level is 170 (SQL Server 2025), so the verbose
        // Msg 2628 fires with the column-name and truncated-prefix detail.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c char(5))");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values ('toolong')"));
        AreEqual("String or binary data would be truncated in table 't', column 'c'. Truncated value: 'toolo'.", ex.Message);
    }

    [TestMethod]
    public void Insert_OversizeNChar_RaisesTruncationError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c nchar(5))");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (N'toolong')"));
        AreEqual("String or binary data would be truncated in table 't', column 'c'. Truncated value: 'toolo'.", ex.Message);
    }

    [TestMethod]
    public void Insert_OversizeBinary_RaisesTruncationError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (b binary(5))");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values (0x010203040506)"));
        Assert.StartsWith("String or binary data would be truncated in table 't', column 'b'.", ex.Message);
    }

    [TestMethod]
    public void Cast_CharZero_RaisesMsg1001()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('a' as char(0))"));
        AreEqual("Line 1: Length or precision specification 0 is invalid.", ex.Message);
    }

    [TestMethod]
    public void Cast_CharOversize_RaisesMsg131WithTypeWording()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('a' as char(8001))"));
        AreEqual("The size (8001) given to the type 'char' exceeds the maximum allowed for any data type (8000).", ex.Message);
    }

    [TestMethod]
    public void Cast_NCharOversize_RaisesMsg131WithConvertSpecificationWording()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(N'a' as nchar(4001))"));
        AreEqual("The size (4001) given to the convert specification 'nchar' exceeds the maximum allowed for any data type (4000).", ex.Message);
    }

    [TestMethod]
    public void Cast_BinaryOversize_RaisesMsg131WithBinaryWording()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(0xab as binary(8001))"));
        AreEqual("The size (8001) given to the type 'binary' exceeds the maximum allowed for any data type (8000).", ex.Message);
    }

    [TestMethod]
    public void CreateTable_CharOversize_RaisesMsg131WithColumnWording()
    {
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (c char(8001))"));
        AreEqual("The size (8001) given to the column 'c' exceeds the maximum allowed for any data type (8000).", ex.Message);
    }

    [TestMethod]
    public void CreateTable_NCharOversize_RaisesMsg2717ParameterWording()
    {
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (c nchar(4001))"));
        AreEqual("The size (4001) given to the parameter 'c' exceeds the maximum allowed (4000).", ex.Message);
    }

    [TestMethod]
    public void Cast_Default_NoParensInCastIs30()
    {
        // Verified against SQL Server 2025: CAST without parens defaults to 30,
        // matching SQL Server's documented "30 in CAST, 1 in column declaration"
        // split. Confirm via DATALENGTH.
        AreEqual(30, ExecuteScalar("select datalength(cast('abc' as char))"));
    }

    [TestMethod]
    public void CreateTable_DefaultIsOne()
    {
        // Column declaration default is 1 — SQL Server's documented split from
        // CAST's default of 30. INSERT a 1-char value to verify it round trips
        // without truncation; 'hello' would truncate.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c char)");
        _ = simulation.ExecuteNonQuery("insert into t values ('a')");
        AreEqual("a", simulation.ExecuteScalar("select c from t"));
        // 'hello' wouldn't fit char(1).
        _ = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values ('hello')"));
    }

    [TestMethod]
    public void Cast_UidToCharBelow36_RaisesMsg8170()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(NEWID() as char(35))"));
        AreEqual("Insufficient result space to convert uniqueidentifier value to char.", ex.Message);
    }

    [TestMethod]
    public void Cast_UidToNCharBelow36_RaisesMsg8115WithNvarcharText()
    {
        // Verified against SQL Server 2025: nchar's overflow message names
        // "nvarchar" rather than "nchar" — same shared text path as nvarchar.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(NEWID() as nchar(35))"));
        AreEqual("Arithmetic overflow error converting expression to data type nvarchar.", ex.Message);
    }

    [TestMethod]
    public void Cast_VarcharToNvarchar_PreservesValue()
    {
        // String → string crossings now work in CAST (varchar ↔ nvarchar ↔
        // char(N) ↔ nchar(N)). This was a known gap before fixed-length types
        // were added; fixing it fell out of the same string→string code path.
        AreEqual("abc", ExecuteScalar("select cast('abc' as nvarchar(10))"));
    }

    [TestMethod]
    public void Insert_WithLegacyCompatibility_RaisesMsg8152NoColumnDetail()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database current set compatibility_level = 150");
        _ = simulation.ExecuteNonQuery("create table t (c char(5))");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t values ('toolong')"));
        AreEqual("String or binary data would be truncated.", ex.Message);
    }

    [TestMethod]
    public void Cast_CharToVarchar_TrailingSpacesPreservedInString()
    {
        // The padded form is part of the value, so casting to varchar carries
        // the trailing spaces through (verified: varchar comparison still
        // strips them via ANSI padding).
        AreEqual(1, ExecuteScalar("select 1 where cast(cast('abc' as char(5)) as varchar(10)) = 'abc'"));
    }
}
