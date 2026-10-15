using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The cryptographic built-ins. <c>ENCRYPTBYPASSPHRASE</c> /
/// <c>DECRYPTBYPASSPHRASE</c> are byte-compatible with SQL Server 2025's
/// version-2 format, pinned by decrypting cipher texts real wrote; every key
/// and certificate function answers as real does for a key that doesn't
/// exist (the key store itself is in <see cref="EncryptionKeyTests"/>). All
/// probed 2026-10-15 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CryptographicFunctionTests
{
    [TestMethod]
    [DataRow("'pw', 0x020000006973D0378F5356B4DF6BE7C1EF6ECBC121812CD2A698E68719933CAFA064A83A", "hello", "varchar(20)")]
    [DataRow("'pw', 0x0200000058C5FA9C5595BADEA9189B29E9BD292EA604284985DD98A613D60532B11C0DF1FA251FC83417D7DAD76897A30AF17164501ADAF1F7AC5E46AF966CFA3893D57A, 1, 'auth'", "hello", "varchar(20)")]
    [DataRow("'pw', 0x0200000058C5FA9C5595BADEA9189B29E9BD292EA604284985DD98A613D60532B11C0DF1FA251FC83417D7DAD76897A30AF17164501ADAF1F7AC5E46AF966CFA3893D57A, 1, N'auth'", null, "varchar(20)")]
    [DataRow("'pw', 0x0200000058C5FA9C5595BADEA9189B29E9BD292EA604284985DD98A613D60532B11C0DF1FA251FC83417D7DAD76897A30AF17164501ADAF1F7AC5E46AF966CFA3893D57A", null, "varchar(20)")]
    [DataRow("'pé', 0x02000000111CC1698A25115CF81E8B00BCE8C20587FC4D4E9B0F0F55DC4370325709B7786F4B23FBB44E43C08BD2812F98D99909", "héllo", "nvarchar(20)")]
    [DataRow("'pw', 0x02000000C132354C611948AC6A0950C24ECE887DF13B9EA233F7F7222E5324F655A1FAD7, 1, NULL", "hello", "varchar(20)")]
    [DataRow("'pw', 0x02000000C132354C611948AC6A0950C24ECE887DF13B9EA233F7F7222E5324F655A1FAD7", null, "varchar(20)")]
    [DataRow("'PW', 0x020000006973D0378F5356B4DF6BE7C1EF6ECBC121812CD2A698E68719933CAFA064A83A", null, "varchar(20)")]
    [DataRow("'pw', 0x0102", null, "varchar(20)")]
    public void DecryptByPassPhrase_ReadsRealsCipherText(string arguments, string? expected, string type)
        => AreEqual(expected ?? (object)DBNull.Value, new Simulation().ExecuteScalar($"select cast(decryptbypassphrase({arguments}) as {type})"));

    [TestMethod]
    [DataRow("'pw', 'hello'", "'pw', @c", "hello")]
    [DataRow("'pw', 'hello', 1, 'auth'", "'pw', @c, 1, 'auth'", "hello")]
    [DataRow("'pw', 'hello', 1, 'auth'", "'pw', @c, 1, 'other'", null)]
    [DataRow("'pw', 'hello', 2, 'auth'", "'pw', @c", "hello")]
    [DataRow("0x70007700, 'hello'", "N'pw', @c", "hello")]
    [DataRow("0x707700, 'hello'", "0x70770000, @c", "hello")]
    [DataRow("cast('pw' as char(5)), 'hello'", "'pw', @c", null)]
    public void PassPhrase_RoundTrips(string encrypt, string decrypt, string? expected)
        => AreEqual(expected ?? (object)DBNull.Value, new Simulation().ExecuteScalar($"""
            declare @c varbinary(8000) = encryptbypassphrase({encrypt});
            select cast(decryptbypassphrase({decrypt}) as varchar(20))
            """));

    [TestMethod]
    public void EncryptByPassPhrase_Lengths()
        => AreEqual("36|36|52|52|68|7988|NULL", new Simulation().ExecuteScalar("""
            select concat_ws('|',
                datalength(encryptbypassphrase('pw', '')),
                datalength(encryptbypassphrase('pw', 'aaaaaaa')),
                datalength(encryptbypassphrase('pw', 'aaaaaaaa')),
                datalength(encryptbypassphrase('pw', N'hello')),
                datalength(encryptbypassphrase('pw', 'hello', 1, 'auth')),
                datalength(encryptbypassphrase('pw', replicate(cast('a' as varchar(max)), 7959))),
                isnull(cast(datalength(encryptbypassphrase('pw', replicate(cast('a' as varchar(max)), 7960))) as varchar), 'NULL'))
            """));

    [TestMethod]
    [DataRow("encryptbypassphrase('pw', replicate(cast('a' as varchar(max)), 8001))", 10)]
    [DataRow("encryptbypassphrase(replicate(cast('p' as varchar(max)), 5000), 'x')", 4)]
    [DataRow("key_id(replicate(cast('a' as varchar(max)), 9000))", 10)]
    public void OversizeArgument_RaisesMsg8152(string call, int state)
        => AreEqual((byte)state, new Simulation().AssertSqlError($"select {call}", 8152).State);

    [TestMethod]
    public void DecryptByPassPhrase_VersionOne_RaisesNotSupported()
        => _ = Throws<NotSupportedException>(() => new Simulation().ExecuteScalar("select decryptbypassphrase('p', 0x0100000011223344556677881122334455667788)"));

    [TestMethod]
    public void KeyStoreFunctions_AnswerForAKeyThatDoesNotExist()
        => AreEqual("1|0|-1", new Simulation().ExecuteScalar("""
            select concat_ws('|',
                case when key_id('k') is null and key_guid('k') is null and key_name(0x00) is null and cert_id('c') is null
                    and asymkey_id('a') is null and certproperty(1, 'Subject') is null and asymkeyproperty(1, 'algorithm') is null
                    and symkeyproperty(1, 'algorithm_desc') is null and encryptbykey(newid(), 'x') is null and decryptbykey(0x00) is null
                    and decryptbykeyautocert(1, null, 0x00) is null and decryptbykeyautoasymkey(1, null, 0x00) is null
                    and encryptbycert(1, 'x') is null and decryptbycert(1, 0x00) is null and encryptbyasymkey(1, 'x') is null
                    and decryptbyasymkey(1, 0x00) is null and signbycert(1, 'x') is null and signbyasymkey(1, 'x') is null
                    and is_objectsigned('OBJECT', 1, 'certificate', 0x00) is null then 1 else 0 end,
                verifysignedbycert(1, 'x', 0x00),
                isnull(verifysignedbyasymkey(cast(null as int), 'x', 0x00), -1))
            """));

    [TestMethod]
    public void ResultTypes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            select key_id('k') a, key_guid('k') b, key_name(0x00) c, certproperty(1, 'Subject') d,
                encryptbypassphrase('p', 'x') e, verifysignedbycert(1, 'x', 0x00) f
            into t
            """);
        AreEqual(
            "a int 4|b uniqueidentifier 16|c nvarchar 8000|d sql_variant 8016|e varbinary 8000|f int 4",
            simulation.ExecuteScalar("select string_agg(concat(c.name, ' ', t.name, ' ', c.max_length), '|') within group (order by c.column_id) from sys.columns c join sys.types t on t.user_type_id = c.user_type_id where c.object_id = object_id('t')"));
    }

    [TestMethod]
    [DataRow("key_id()", 174, "The Key_Id function requires 1 argument(s).")]
    [DataRow("asymkey_id('a', 'b')", 174, "The AsymKey_Id function requires 1 argument(s).")]
    [DataRow("certproperty(1)", 174, "The CertProperty function requires 2 argument(s).")]
    [DataRow("decryptbykey()", 189, "The DecryptByKey function requires 1 to 3 arguments.")]
    [DataRow("decryptbykeyautoasymkey(1, null)", 189, "The DecryptByKeyAutoAsymKey function requires 3 to 5 arguments.")]
    [DataRow("encryptbypassphrase('pw')", 189, "The EncryptByPassPhrase function requires 2 to 4 arguments.")]
    [DataRow("is_objectsigned('OBJECT', 1)", 174, "The is_objectsigned function requires 4 argument(s).")]
    [DataRow("key_id(1)", 8116, "Argument data type int is invalid for argument 1 of Key_Id function.")]
    [DataRow("key_id(null)", 8116, "Argument data type NULL is invalid for argument 1 of Key_Id function.")]
    [DataRow("key_name(cast(0x00 as binary(4)))", 8116, "Argument data type binary is invalid for argument 1 of Key_Name function.")]
    [DataRow("encryptbykey('k', 'x')", 8116, "Argument data type varchar is invalid for argument 1 of EncryptByKey function.")]
    [DataRow("decryptbycert(1, 0x00, 'p')", 8116, "Argument data type varchar is invalid for argument 3 of DecryptByCert function.")]
    [DataRow("encryptbypassphrase('pw', 1)", 8116, "Argument data type int is invalid for argument 2 of EncryptByPassPhrase function.")]
    [DataRow("encryptbypassphrase('pw', 'x', cast(1 as bigint), 'a')", 8116, "Argument data type bigint is invalid for argument 3 of EncryptByPassPhrase function.")]
    [DataRow("encryptbypassphrase('pw', cast('x' as text))", 8116, "Argument data type text is invalid for argument 2 of EncryptByPassPhrase function.")]
    [DataRow("verifysignedbycert(1, 'x', 'sig')", 8116, "Argument data type varchar is invalid for argument 3 of VerifySignedByCert function.")]
    public void ArgumentRefusals_StopTheBatchWhileCompiling(string call, int number, string message)
    {
        var simulation = new Simulation();
        simulation.AssertSqlError($"create table t (a int); if 1 = 0 select {call}", number, message);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
    }

    [TestMethod]
    public void NullableSlots_TakeABareNull()
        => AreEqual(36, new Simulation().ExecuteScalar("select datalength(encryptbypassphrase('pw', 'x', null, null)) + isnull(datalength(decryptbycert(1, 0x00, null)), 0)"));
}
