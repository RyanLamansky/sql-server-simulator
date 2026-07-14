using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>FORMATMESSAGE</c>, <c>PWDENCRYPT</c> /
/// <c>PWDCOMPARE</c>, and <c>LOGINPROPERTY</c> scalar built-ins. All expected
/// values probe-confirmed against SQL Server 2025 (2026-07-10).
/// </summary>
[TestClass]
public sealed class FormatMessageAndLoginScalarTests
{
    /// <summary>
    /// The terse in-server formatting-error diagnostic returned (as data, not
    /// thrown) for a recoverable FORMATMESSAGE failure. Byte-exact from the
    /// live server, trailing CRLF included.
    /// </summary>
    private const string TerseError =
        "Error: 50000, Severity: -1, State: 1. (Params:). The error is printed in terse mode because there was error during formatting. Tracing, ETW, notifications etc are skipped.\r\n";

    private static object? Scalar(string sql) => new Simulation().ExecuteScalar(sql);

    private static void AssertMsg(string sql, int errorNumber)
    {
        var ex = Throws<DbException>(() => Scalar(sql));
        AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"], $"expected Msg {errorNumber}");
    }

    // ---- FORMATMESSAGE: core specifiers ----

    [TestMethod]
    public void FormatMessage_StringAndInt()
        => AreEqual("hi world and 42", Scalar("select formatmessage('hi %s and %d', 'world', 42)"));

    [TestMethod]
    public void FormatMessage_IntSynonym()
        => AreEqual("num 42", Scalar("select formatmessage('num %i', 42)"));

    [TestMethod]
    public void FormatMessage_HexLowerUpper()
    {
        AreEqual("hex ff", Scalar("select formatmessage('hex %x', 255)"));
        AreEqual("HEX FF", Scalar("select formatmessage('HEX %X', 255)"));
    }

    [TestMethod]
    public void FormatMessage_Octal()
        => AreEqual("oct 100", Scalar("select formatmessage('oct %o', 64)"));

    [TestMethod]
    public void FormatMessage_UnsignedReinterpretsNegative()
        => AreEqual("uns 4294967295", Scalar("select formatmessage('uns %u', -1)"));

    // ---- FORMATMESSAGE: width / flags / precision ----

    [TestMethod]
    public void FormatMessage_WidthRightAlign()
        => AreEqual("[   42]", Scalar("select formatmessage('[%5d]', 42)"));

    [TestMethod]
    public void FormatMessage_WidthLeftAlign()
        => AreEqual("[42   ]", Scalar("select formatmessage('[%-5d]', 42)"));

    [TestMethod]
    public void FormatMessage_ZeroPad()
        => AreEqual("[00042]", Scalar("select formatmessage('[%05d]', 42)"));

    [TestMethod]
    public void FormatMessage_ForceSign()
    {
        AreEqual("[+42]", Scalar("select formatmessage('[%+d]', 42)"));
        AreEqual("[-42]", Scalar("select formatmessage('[%+d]', -42)"));
    }

    [TestMethod]
    public void FormatMessage_PrecisionZeroPadsInteger()
        => AreEqual("[005]", Scalar("select formatmessage('[%.3d]', 5)"));

    [TestMethod]
    public void FormatMessage_AltFormHexPrefix()
        => AreEqual("[0xff]", Scalar("select formatmessage('[%#x]', 255)"));

    [TestMethod]
    public void FormatMessage_HexWidthZeroPad()
        => AreEqual("[000000FF]", Scalar("select formatmessage('[%08X]', 255)"));

    [TestMethod]
    public void FormatMessage_StarWidthConsumesArgument()
        => AreEqual("[   42]", Scalar("select formatmessage('[%*d]', 5, 42)"));

    // ---- FORMATMESSAGE: length modifiers ----

    [TestMethod]
    public void FormatMessage_LongModifierIgnored()
        => AreEqual("val=[42]", Scalar("select formatmessage('val=[%ld]', 42)"));

    [TestMethod]
    public void FormatMessage_Int64Modifier()
        => AreEqual("val=[9999999999]", Scalar("select formatmessage('val=[%I64d]', cast(9999999999 as bigint))"));

    // ---- FORMATMESSAGE: literal / escape / arg-count ----

    [TestMethod]
    public void FormatMessage_PercentEscape()
        => AreEqual("100% done", Scalar("select formatmessage('100%% done')"));

    [TestMethod]
    public void FormatMessage_TooFewArgsRendersNull()
        => AreEqual("a one b (null)", Scalar("select formatmessage('a %s b %s', 'one')"));

    [TestMethod]
    public void FormatMessage_TooManyArgsIgnored()
        => AreEqual("a one", Scalar("select formatmessage('a %s', 'one', 'two')"));

    // ---- FORMATMESSAGE: NULL handling ----

    [TestMethod]
    public void FormatMessage_NullFormatReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select formatmessage(cast(null as nvarchar(100)), 'x')"));

    [TestMethod]
    public void FormatMessage_NullStringArgRendersNull()
        => AreEqual("val=[(null)]", Scalar("select formatmessage('val=[%s]', cast(null as nvarchar(10)))"));

    [TestMethod]
    public void FormatMessage_NullIntArgRendersNull()
        => AreEqual("val=[(null)]", Scalar("select formatmessage('val=[%d]', cast(null as int))"));

    // ---- FORMATMESSAGE: terse-error recoverable failures ----

    [TestMethod]
    public void FormatMessage_IntIntoStringSpecifier_Terse()
        => AreEqual(TerseError, Scalar("select formatmessage('val=[%s]', 42)"));

    [TestMethod]
    public void FormatMessage_StringIntoIntSpecifier_Terse()
        => AreEqual(TerseError, Scalar("select formatmessage('val=[%d]', 'abc')"));

    [TestMethod]
    public void FormatMessage_BigIntIntoNarrowSpecifier_Terse()
        => AreEqual(TerseError, Scalar("select formatmessage('val=[%d]', cast(9999999999 as bigint))"));

    [TestMethod]
    public void FormatMessage_IntIntoInt64Specifier_Terse()
        => AreEqual(TerseError, Scalar("select formatmessage('val=[%I64d]', 5)"));

    [TestMethod]
    public void FormatMessage_EmptyFormat_Terse()
        => AreEqual(TerseError, Scalar("select formatmessage('')"));

    // ---- FORMATMESSAGE: Msg 2748 disallowed substitution types ----

    [TestMethod]
    public void FormatMessage_FloatArg_Msg2748()
        => AssertMsg("select formatmessage('%d', cast(5 as float))", 2748);

    [TestMethod]
    public void FormatMessage_MoneyArg_Msg2748()
        => AssertMsg("select formatmessage('%d', cast(5 as money))", 2748);

    [TestMethod]
    public void FormatMessage_DateTimeArg_Msg2748()
        => AssertMsg("select formatmessage('%d', cast('2020-01-01' as datetime))", 2748);

    [TestMethod]
    public void FormatMessage_Msg2748_MessageWordingAndParamIndex()
    {
        var ex = Throws<DbException>(() => Scalar("select formatmessage('%d', cast(5 as float))"));
        AreEqual("Cannot specify float data type (parameter 1) as a substitution parameter.", ex.Message);
    }

    // ---- FORMATMESSAGE: msg_id overload + truncation ----

    [TestMethod]
    public void FormatMessage_UnknownMessageId_ReturnsNull()
    {
        AreEqual(DBNull.Value, Scalar("select formatmessage(50000, 'x')"));
        AreEqual(DBNull.Value, Scalar("select formatmessage(99999999)"));
    }

    [TestMethod]
    public void FormatMessage_ResultTruncatesTo2047Chars()
        => AreEqual(4094, Scalar("select datalength(formatmessage('%s', replicate(cast('a' as nvarchar(max)), 5000)))"));

    // ---- PWDENCRYPT / PWDCOMPARE ----

    [TestMethod]
    public void PwdEncrypt_ProducesSeventyByteHash()
    {
        var hash = (byte[])new Simulation().ExecuteScalar("select pwdencrypt('abc')")!;
        HasCount(70, hash);
        AreEqual((byte)0x03, hash[0]);
        AreEqual((byte)0x00, hash[1]);
    }

    [TestMethod]
    public void PwdEncrypt_DataLengthIs70()
        => AreEqual(70, Scalar("select datalength(pwdencrypt('abc'))"));

    [TestMethod]
    public void PwdEncrypt_SamePasswordDiffersBySalt()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var a = (byte[])connection.CreateCommand("select pwdencrypt('abc')").ExecuteScalar()!;
        var b = (byte[])connection.CreateCommand("select pwdencrypt('abc')").ExecuteScalar()!;
        IsFalse(a.AsSpan().SequenceEqual(b), "salt should randomize successive hashes");
    }

    [TestMethod]
    public void PwdCompare_RoundTripRightPassword()
        => AreEqual(1, Scalar("select pwdcompare('abc', pwdencrypt('abc'))"));

    [TestMethod]
    public void PwdCompare_WrongPassword()
        => AreEqual(0, Scalar("select pwdcompare('xyz', pwdencrypt('abc'))"));

    [TestMethod]
    public void PwdCompare_NullClearReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select pwdcompare(null, pwdencrypt('abc'))"));

    [TestMethod]
    public void PwdCompare_NullHashReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select pwdcompare('abc', null)"));

    [TestMethod]
    public void PwdCompare_GarbageShortHashReturnsZero()
        => AreEqual(0, Scalar("select pwdcompare('abc', 0x1234)"));

    [TestMethod]
    public void PwdCompare_EmptyHashReturnsZero()
        => AreEqual(0, Scalar("select pwdcompare('abc', 0x)"));

    [TestMethod]
    public void PwdCompare_ThirdVersionArgumentAcceptedAndIgnored()
    {
        AreEqual(1, Scalar("select pwdcompare('abc', pwdencrypt('abc'), 0)"));
        AreEqual(1, Scalar("select pwdcompare('abc', pwdencrypt('abc'), 1)"));
    }

    // SQL Server caps password-machinery input at 128 characters
    // (probe-confirmed at exactly the 128/129 boundary, 2026-07-14).

    [TestMethod]
    public void PwdEncrypt_128Chars_Succeeds()
        => AreEqual(70, Scalar("select datalength(pwdencrypt(replicate('a', 128)))"));

    [TestMethod]
    public void PwdEncrypt_129Chars_Raises6607()
    {
        var ex = new Simulation().AssertSqlError("select pwdencrypt(replicate('a', 129))", 6607);
        AreEqual("Password Encryption: The value supplied for parameter number 1 is invalid.", ex.Message);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)5, ex.State);
    }

    [TestMethod]
    public void PwdCompare_128CharClear_RoundTrips()
        => AreEqual(1, Scalar("select pwdcompare(replicate('a', 128), pwdencrypt(replicate('a', 128)))"));

    // An oversized clear compares in full rather than truncating — real
    // returns 0 for a 129-char clear against its own 128-char prefix's hash.
    [TestMethod]
    public void PwdCompare_OversizedClear_ReturnsZeroWithoutTruncating()
        => AreEqual(0, Scalar("select pwdcompare(replicate('a', 129), pwdencrypt(replicate('a', 128)))"));

    /// <summary>
    /// Cross-engine fidelity: a <c>0x0300</c> hash generated by the live
    /// SQL Server 2025 reference for password <c>'abc'</c> verifies inside the
    /// simulator (probe-captured 2026-07-10). Proves the simulator reproduces
    /// SQL Server's real PBKDF2-HMAC-SHA512(UTF-16LE, salt, 100000) layout.
    /// </summary>
    [TestMethod]
    public void PwdCompare_VerifiesRealServerGeneratedHash()
    {
        const string realHash = "0x030016EEF69E6272D4880A64A024480DC06B67872BF77647CBA42241A5DD922EDCC7495917C2691CFE6255123A27CFBA9F8DE1AA5F0D500F357B7063EAC9ACFBE94D61B8815D";
        AreEqual(1, Scalar($"select pwdcompare('abc', {realHash})"));
        AreEqual(0, Scalar($"select pwdcompare('wrong', {realHash})"));
    }

    // ---- LOGINPROPERTY ----

    [TestMethod]
    public void LoginProperty_KnownLogin_TimeAndCountProperties()
    {
        AreEqual("2020-01-01 00:00:00.000", Scalar("select loginproperty('dbo', 'PasswordLastSetTime')"));
        AreEqual("0", Scalar("select loginproperty('dbo', 'BadPasswordCount')"));
        AreEqual("1900-01-01 00:00:00.000", Scalar("select loginproperty('dbo', 'BadPasswordTime')"));
        AreEqual("1900-01-01 00:00:00.000", Scalar("select loginproperty('dbo', 'LockoutTime')"));
        AreEqual("0", Scalar("select loginproperty('dbo', 'HistoryLength')"));
    }

    [TestMethod]
    public void LoginProperty_KnownLogin_BooleanFlags()
    {
        AreEqual("0", Scalar("select loginproperty('dbo', 'IsExpired')"));
        AreEqual("0", Scalar("select loginproperty('dbo', 'IsLocked')"));
        AreEqual("0", Scalar("select loginproperty('dbo', 'IsMustChange')"));
    }

    [TestMethod]
    public void LoginProperty_KnownLogin_NullValuedProperties()
    {
        AreEqual(DBNull.Value, Scalar("select loginproperty('dbo', 'DaysUntilExpiration')"));
        AreEqual(DBNull.Value, Scalar("select loginproperty('dbo', 'PasswordHash')"));
        AreEqual(DBNull.Value, Scalar("select loginproperty('dbo', 'PasswordHashAlgorithm')"));
    }

    [TestMethod]
    public void LoginProperty_KnownLogin_NameProperties()
    {
        AreEqual("simulated", Scalar("select loginproperty('dbo', 'DefaultDatabase')"));
        AreEqual("us_english", Scalar("select loginproperty('dbo', 'DefaultLanguage')"));
    }

    [TestMethod]
    public void LoginProperty_CaseInsensitiveLoginAndProperty()
        => AreEqual("0", Scalar("select loginproperty('DBO', 'isexpired')"));

    [TestMethod]
    public void LoginProperty_UnknownLogin_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select loginproperty('no_such_login', 'IsExpired')"));

    [TestMethod]
    public void LoginProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select loginproperty('dbo', 'NoSuchProperty')"));

    [TestMethod]
    public void LoginProperty_NullLogin_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select loginproperty(null, 'IsExpired')"));

    [TestMethod]
    public void LoginProperty_NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select loginproperty('dbo', null)"));
}
