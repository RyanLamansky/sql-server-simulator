using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The database key store: the master key, certificates, symmetric keys and
/// the functions over them. A key from a <c>KEY_SOURCE</c> and
/// <c>IDENTITY_VALUE</c> is the key real derives from them, pinned by
/// decrypting cipher texts real wrote. All probed 2026-10-15 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class EncryptionKeyTests
{
    private const string MasterKey = "create master key encryption by password = 'Mk!12345678';";

    [TestMethod]
    [DataRow("aes_128", "src", "idv", "0x001F066BBD9DD24BC0FF3025815A92B102000000D7EF6A6A4A82334DC59C7C3E574FFD80AF5B7295A25E6E56CED865CD091CFC78", "cast(decryptbykey(@c) as varchar(10))", "hello")]
    [DataRow("aes_192", "Ünïcode src", "id192", "0x00CD5D02C4A6832E8D7794480E23B87102000000B25DFB6FC36D868AEF5E9C17FE2B0BC3E6055A9E65D513E90061F7365351D4DBC89EE595AD3793C16F2367FC63DA313EF9ADA00CF6EA781347D2DE52FBAFFF72", "cast(decryptbykey(@c, 1, 'au') as nvarchar(10))", "héllo")]
    [DataRow("aes_256", "src256", "id256", "0x00A70EA60AAD98773C1A94B379760CA1020000002675EC14ED5B8FAF045FAD71602FBF0268C3C4F7F26E26884913B3E555C0C976", "convert(varchar(10), decryptbykey(@c), 1)", "0x0102")]
    public void DecryptByKey_ReadsRealsCipherText(string algorithm, string keySource, string identityValue, string cipher, string decrypt, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create symmetric key k with algorithm = {algorithm}, key_source = N'{keySource}', identity_value = '{identityValue}' encryption by password = 'Abcdefgh12';
            open symmetric key k decryption by password = 'Abcdefgh12';
            declare @c varbinary(8000) = {cipher};
            select {decrypt}
            """));

    [TestMethod]
    public void IdentityValue_GivesRealsKeyGuid()
        => AreEqual("6B061F00-9DBD-4BD2-C0FF-3025815A92B1|025DCD00-A6C4-2E83-8D77-94480E23B871", new Simulation().ExecuteScalar("""
            create symmetric key k1 with algorithm = aes_128, identity_value = 'idv' encryption by password = 'Abcdefgh12';
            create symmetric key k2 with algorithm = aes_128, identity_value = 'id192' encryption by password = 'Abcdefgh12';
            select concat(key_guid('k1'), '|', key_guid('k2'))
            """));

    [TestMethod]
    public void EncryptByKey_RoundTripsWhileOpen()
        => AreEqual("52|k|hello|84|52|7988|NULL|abc|NULL|NULL|NULL|NULL", new Simulation().ExecuteScalar("""
            create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            declare @closed varbinary(8000) = encryptbykey(key_guid('k'), 'x');
            open symmetric key k decryption by password = 'Abcdefgh12';
            declare @c varbinary(8000) = encryptbykey(key_guid('k'), 'hello'), @a varbinary(8000) = encryptbykey(key_guid('k'), 'abc', 1, 'auth');
            declare @r varchar(200) = concat_ws('|', datalength(@c), key_name(@c), cast(decryptbykey(@c) as varchar(10)),
                datalength(encryptbykey(key_guid('k'), 'hello', 1, 'au')), datalength(encryptbykey(key_guid('k'), '')),
                datalength(encryptbykey(key_guid('k'), replicate(cast('a' as varchar(max)), 7943))),
                isnull(cast(datalength(encryptbykey(key_guid('k'), replicate(cast('a' as varchar(max)), 7944))) as varchar), 'NULL'),
                cast(decryptbykey(@a, 1, 'auth') as varchar(10)), isnull(cast(decryptbykey(@a) as varchar(10)), 'NULL'),
                isnull(cast(decryptbykey(@a, 1, 'xx') as varchar(10)), 'NULL'), isnull(cast(@closed as varchar(10)), 'NULL'));
            close symmetric key k;
            select concat(@r, '|', isnull(cast(decryptbykey(@c) as varchar(10)), 'NULL'))
            """));

    [TestMethod]
    public void Catalog_ShowsKeysAndTheirEncryptions()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(MasterKey + """
            create certificate c1 with subject = 'one';
            create symmetric key s1 with algorithm = aes_256 encryption by certificate c1;
            create symmetric key s2 with algorithm = aes_128 encryption by password = 'Abcdefgh12', password = 'Abcdefgh12';
            open symmetric key s2 decryption by password = 'Abcdefgh12';
            create symmetric key s3 with algorithm = aes_192 encryption by symmetric key s2;
            """);
        AreEqual("##MS_DatabaseMasterKey##:101:256:A3:AES_256|s1:256:256:A3:AES_256|s2:257:128:A1:AES_128|s3:258:192:A2:AES_192", simulation.ExecuteScalar(
            "select string_agg(concat_ws(':', name, symmetric_key_id, key_length, key_algorithm, algorithm_desc) collate database_default, '|') within group (order by symmetric_key_id) from sys.symmetric_keys"));
        AreEqual("101:1:ESKM:48|101:ESP2:48|256:20:C256:384|257:ESP2:32|257:ESP2:32|258:16:ESKS:32", simulation.ExecuteScalar(
            "select string_agg(concat_ws(':', key_id, datalength(thumbprint), crypt_type, datalength(crypt_property)) collate database_default, '|') from sys.key_encryptions"));
        AreEqual("c1:256:MK:ENCRYPTED_BY_MASTER_KEY:one:one:3072:1:32:20:59", simulation.ExecuteScalar("""
            select concat_ws(':', name, certificate_id, pvt_key_encryption_type, pvt_key_encryption_type_desc, subject collate Latin1_General_CI_AS_KS_WS,
                issuer_name collate Latin1_General_CI_AS_KS_WS, key_length, is_active_for_begin_dialog, datalength(sid), datalength(thumbprint), len(cert_serial_number))
            from sys.certificates
            """));
    }

    [TestMethod]
    public void OpenKeys_ListTheSessionsKeysInEveryDatabase()
        => AreEqual("simulated:k:1|0", new Simulation().ExecuteScalar("""
            create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            open symmetric key k decryption by password = 'Abcdefgh12';
            open symmetric key k decryption by password = 'Abcdefgh12';
            use master;
            declare @open varchar(100) = (select string_agg(concat_ws(':', database_name, key_name, status), ',') from sys.openkeys);
            use simulated;
            drop symmetric key k;
            select concat(@open, '|', (select count(*) from sys.openkeys))
            """));

    [TestMethod]
    public void OpenKeys_LeaveWithTheirDatabase()
        => AreEqual("1|0", new Simulation().ExecuteBatchesScalar(
            "create database d",
            """
            use d;
            create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            open symmetric key k decryption by password = 'Abcdefgh12';
            use simulated;
            declare @before int = (select count(*) from sys.openkeys);
            drop database d;
            select concat(@before, '|', (select count(*) from sys.openkeys))
            """));

    [TestMethod]
    [DataRow("close symmetric key k", 15315, 7)]
    [DataRow("close symmetric key nope", 15151, 1)]
    [DataRow("open symmetric key k decryption by password = 'wrong'", 15313, 8)]
    [DataRow("open symmetric key nope decryption by password = 'x'", 15151, 1)]
    [DataRow("open symmetric key c decryption by certificate pc", 15334, 5)]
    [DataRow("open symmetric key c decryption by certificate pc with password = 'wrong'", 15466, 9)]
    [DataRow("open symmetric key c decryption by certificate mc with password = 'x'", 15465, 10)]
    [DataRow("open symmetric key c decryption by certificate nope", 15151, 1)]
    [DataRow("open symmetric key c decryption by asymmetric key nope", 15151, 1)]
    [DataRow("open symmetric key c decryption by symmetric key k", 15315, 6)]
    [DataRow("open symmetric key k decryption by certificate mc", 15313, 8)]
    [DataRow("create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12'", 15282, 1)]
    [DataRow("create symmetric key x with algorithm = aes_256, identity_value = 'same' encryption by password = 'Abcdefgh12'", 15282, 1)]
    [DataRow("create symmetric key x with algorithm = aes_256 encryption by symmetric key k", 15315, 1)]
    [DataRow("create symmetric key x with algorithm = aes_256 encryption by certificate nope", 15151, 1)]
    [DataRow("create symmetric key x with key_source = 'a' encryption by password = 'Abcdefgh12'", 15314, 1)]
    [DataRow("create symmetric key x authorization nobody with algorithm = aes_256 encryption by password = 'Abcdefgh12'", 15151, 1)]
    [DataRow("create symmetric key x with algorithm = aes_256 encryption by password = 'short'", 33062, 2)]
    [DataRow("create symmetric key x with algorithm = aes_256 encryption by password = 'abcdefgh'", 33064, 2)]
    [DataRow("alter symmetric key k add encryption by password = 'Zz!12345678'", 15315, 2)]
    [DataRow("alter symmetric key nope add encryption by password = 'Zz!12345678'", 15151, 1)]
    [DataRow("drop symmetric key k", 15352, 1)]
    [DataRow("drop symmetric key nope", 15151, 1)]
    [DataRow("drop certificate mc", 15352, 1)]
    [DataRow("drop certificate nope", 15151, 1)]
    [DataRow("create certificate mc with subject = 'x'", 15232, 1)]
    [DataRow("create certificate x with subject = 'a,b'", 15297, 46)]
    [DataRow("create certificate x with subject = 'y', start_date = '2019-01-01', expiry_date = '2018-01-01'", 15297, 45)]
    [DataRow("create certificate x with subject = ''", 15212, 1)]
    [DataRow("create master key encryption by password = 'Mk!12345678'", 15578, 1)]
    [DataRow("drop master key", 15580, 1)]
    [DataRow("open master key decryption by password = 'wrong'", 15313, 11)]
    [DataRow("alter master key add encryption by password = 'Mk!12345678'", 15579, 1)]
    [DataRow("alter master key drop encryption by password = 'Mk!12345678'", 15558, 1)]
    [DataRow("alter master key drop encryption by password = 'Nope!12345678'", 15558, 1)]
    public void KeyStoreRefusals(string statement, int number, int state)
        => AreEqual((byte)state, new Simulation().AssertSqlError(MasterKey + """
            create certificate mc with subject = 'master-protected';
            create certificate pc encryption by password = 'Cp!12345678' with subject = 'password-protected';
            create symmetric key k with algorithm = aes_256, identity_value = 'same' encryption by password = 'Abcdefgh12';
            create symmetric key c with algorithm = aes_256 encryption by certificate mc, certificate pc;
            open symmetric key k decryption by password = 'Abcdefgh12';
            create symmetric key nested with algorithm = aes_256 encryption by symmetric key k;
            close symmetric key k;
            """ + statement, number).State);

    [TestMethod]
    public void AlterSymmetricKey_AddsAndDropsEncryptions()
    {
        var simulation = new Simulation();
        var refusals = simulation.AssertSqlError(MasterKey + """
            create certificate c1 with subject = 'one';
            create symmetric key k with algorithm = aes_256 encryption by certificate c1;
            open symmetric key k decryption by certificate c1;
            alter symmetric key k add encryption by password = 'Zz!12345678';
            alter symmetric key k add encryption by password = 'Zz!12345678';
            alter symmetric key k drop encryption by password = 'Nope!12345678';
            alter symmetric key k drop encryption by certificate c1;
            alter symmetric key k drop encryption by password = 'Zz!12345678';
            """, 15579);
        CollectionAssert.AreEqual(new[] { 15579, 15313, 15558 }, refusals.Errors.Cast<SimulatedError>().Select(error => error.Number).ToArray());
        AreEqual("ESP2", simulation.ExecuteScalar("select string_agg(crypt_type, ',') from sys.key_encryptions where key_id = key_id('k')"));
        AreEqual("Cannot drop encryption by password '********'.", refusals.Errors[2].Message);
    }

    [TestMethod]
    public void MasterKey_NeedsOpeningOnceTheServiceMasterKeyNoLongerProtectsIt()
    {
        var refusals = new Simulation().AssertSqlError("""
            close master key;
            create certificate c0 with subject = 'none';
            create master key encryption by password = 'Mk!12345678';
            alter master key drop encryption by service master key;
            create certificate c1 with subject = 'one';
            open master key decryption by password = 'Mk!12345678';
            create certificate c1 with subject = 'one';
            create symmetric key k with algorithm = aes_256 encryption by certificate c1;
            close master key;
            open symmetric key k decryption by certificate c1;
            alter master key add encryption by password = 'Ad!12345678';
            open master key decryption by password = 'Mk!12345678';
            alter master key add encryption by service master key;
            close master key;
            open symmetric key k decryption by certificate c1;
            """, 15581);
        CollectionAssert.AreEqual(new byte[] { 5, 1, 7, 7, 7 }, refusals.Errors.Cast<SimulatedError>().Select(error => error.State).ToArray());
    }

    [TestMethod]
    public void RegeneratedMasterKey_HasANewGuidAndOnePassword()
        => AreEqual("new|ESKM,ESP2", new Simulation().ExecuteScalar(MasterKey + """
            alter master key add encryption by password = 'Second!12345';
            declare @g uniqueidentifier = (select key_guid from sys.symmetric_keys);
            alter master key regenerate with encryption by password = 'Re!12345678';
            select concat((select case when key_guid = @g then 'same' else 'new' end from sys.symmetric_keys), '|',
                (select string_agg(crypt_type, ',') from sys.key_encryptions))
            """));

    [TestMethod]
    [DataRow("one", "one")]
    [DataRow("a=b", "\"\"\"a=b\"\"\"")]
    [DataRow("CN=Foo", "\"\"\"CN=Foo\"\"\"")]
    [DataRow("O=Bar, CN=Foo", "\"\"\"O=Bar\"\", Foo\"")]
    [DataRow(" lead trail ", "lead trail")]
    [DataRow("ü€", "ü€")]
    public void CertificateSubject_ReadsAsAnX500Name(string subject, string shown)
        => AreEqual(shown, new Simulation().ExecuteScalar(MasterKey + $"create certificate c with subject = N'{subject}'; select subject from sys.certificates"));

    [TestMethod]
    public void CertificateDates_DefaultToAYearFromNowAndKeepWholeSeconds()
        => AreEqual("2020-01-02 03:04:05.000|2021-02-03 00:00:00.000|2041-01-01 00:00:00.000|1", new Simulation().ExecuteScalar(MasterKey + """
            create certificate c1 with subject = 'one', start_date = '2020-01-02 03:04:05.678', expiry_date = '2021-02-03';
            create certificate c2 with subject = 'two', start_date = '2040-01-01';
            create certificate c3 with subject = 'three';
            select concat_ws('|', convert(varchar(23), certproperty(cert_id('c1'), 'Start_Date'), 121), convert(varchar(23), certproperty(cert_id('c1'), 'expiry_date'), 121),
                (select convert(varchar(23), expiry_date, 121) from sys.certificates where name = 'c2'),
                (select datediff(year, start_date, expiry_date) from sys.certificates where name = 'c3'))
            """));

    [TestMethod]
    public void CertificateCreatedOutsideItsValidity_Warns()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand(MasterKey + """
            create certificate c1 with subject = 'y', start_date = '2019-01-01', expiry_date = '2020-01-01';
            create certificate c2 with subject = 'y', start_date = '2040-01-01';
            create certificate c3 with subject = 'y';
            """).ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 15214, 15215 }, messages);
    }

    [TestMethod]
    public void InvalidCertificateDate_StopsTheBatchWhileCompiling()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError(MasterKey + "if 1 = 0 create certificate c with subject = 'x', start_date = 'garbage'", 190, "An invalid date or time was specified in the statement.");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.symmetric_keys"));
    }

    [TestMethod]
    public void MasterKeyWithoutPassword_StopsTheBatchWhileCompiling()
        => new Simulation().AssertSqlError("if 1 = 0 create master key", 33161, "Database master keys without password are not supported in this version of SQL Server.");

    [TestMethod]
    [DataRow("des")]
    [DataRow("triple_des")]
    [DataRow("rc4")]
    public void RetiredAlgorithms_AreSyntaxErrors(string algorithm)
        => new Simulation().AssertSqlError($"create symmetric key k with algorithm = {algorithm} encryption by password = 'Abcdefgh12'", 102, $"Incorrect syntax near '{algorithm}'.");

    [TestMethod]
    public void DecryptByKeyAutoCert_KeepsTheKeyOpenForTheRestOfTheStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(MasterKey + """
            create certificate c1 with subject = 'one';
            create certificate c2 encryption by password = 'Cp!12345678' with subject = 'two';
            create symmetric key k with algorithm = aes_256 encryption by certificate c1;
            create table t (c varbinary(200));
            open symmetric key k decryption by certificate c1;
            insert t values (encryptbykey(key_guid('k'), 'hey'));
            """);
        AreEqual("hey|NULL|hey", simulation.ExecuteScalar("""
            select concat_ws('|', cast(decryptbykeyautocert(cert_id('c1'), null, c) as varchar(10)), isnull(cast(decryptbykey(c) as varchar(10)), 'NULL'),
                cast(decryptbykeyautocert(cert_id('c2'), N'bad', c) as varchar(10)))
            from t
            """));
        AreEqual("NULL|NULL|NULL|0", simulation.ExecuteScalar("""
            select concat_ws('|', isnull(cast(decryptbykeyautocert(cert_id('c2'), N'Cp!12345678', c) as varchar(10)), 'NULL'),
                isnull(cast(decryptbykeyautocert(cert_id('c1'), N'x', c) as varchar(10)), 'NULL'),
                isnull(cast(decryptbykeyautocert(999, null, c) as varchar(10)), 'NULL'), (select count(*) from sys.openkeys))
            from t
            """));
    }

    [TestMethod]
    public void CertificateFunctions_RoundTrip()
        => AreEqual("388|hello|NULL|NULL|0x00010001|388|NULL|NULL|hey|NULL|392|1|0|0|0|NULL|392|0x0100070204000000|det", new Simulation().ExecuteScalar(MasterKey + """
            create certificate c1 with subject = 'one';
            create certificate c2 encryption by password = 'Cp!12345678' with subject = 'two';
            declare @id int = cert_id('c1'), @id2 int = cert_id('c2');
            declare @e varbinary(8000) = encryptbycert(@id, 'hello'), @e2 varbinary(8000) = encryptbycert(@id2, N'hey'), @s varbinary(8000) = signbycert(@id, 'data');
            select concat_ws('|', datalength(@e), cast(decryptbycert(@id, @e) as varchar(10)), isnull(cast(decryptbycert(@id, @e, N'x') as varchar(10)), 'NULL'),
                isnull(cast(decryptbycert(@id2, @e) as varchar(10)), 'NULL'), convert(varchar(10), substring(@e, 385, 4), 1),
                datalength(encryptbycert(@id, replicate('a', 318))), isnull(cast(datalength(encryptbycert(@id, replicate('a', 319))) as varchar), 'NULL'),
                isnull(cast(decryptbycert(@id2, @e2) as nvarchar(10)), 'NULL'), cast(decryptbycert(@id2, @e2, N'Cp!12345678') as nvarchar(10)),
                isnull(cast(decryptbycert(@id2, @e2, N'bad') as nvarchar(10)), 'NULL'),
                datalength(@s), verifysignedbycert(@id, 'data', @s), verifysignedbycert(@id, 'datx', @s), verifysignedbycert(@id2, 'data', @s),
                verifysignedbycert(@id, 'data', 0x00), isnull(cast(datalength(signbycert(@id2, 'data')) as varchar), 'NULL'),
                datalength(signbycert(@id2, 'data', N'Cp!12345678')), convert(varchar(20), substring(@s, 1, 8), 1),
                case when signbycert(@id, 'data') = @s then 'det' else 'rand' end)
            """));

    [TestMethod]
    public void CertProperty_AnswersRealsProperties()
        => AreEqual("datetime|nvarchar:8000|nvarchar:256|varbinary|nvarchar:256|int|one|NULL|NULL|1|1", new Simulation().ExecuteScalar(MasterKey + """
            create certificate c1 with subject = 'one';
            declare @id int = cert_id('c1');
            select concat_ws('|', cast(sql_variant_property(certproperty(@id, 'Expiry_Date'), 'BaseType') as sysname),
                concat(cast(sql_variant_property(certproperty(@id, 'Subject'), 'BaseType') as sysname), ':', cast(sql_variant_property(certproperty(@id, 'Issuer_Name'), 'MaxLength') as int)),
                concat(cast(sql_variant_property(certproperty(@id, 'Cert_Serial_Number'), 'BaseType') as sysname), ':', cast(sql_variant_property(certproperty(@id, 'Cert_Serial_Number'), 'MaxLength') as int)),
                cast(sql_variant_property(certproperty(@id, 'SID'), 'BaseType') as sysname),
                concat(cast(sql_variant_property(certproperty(@id, 'String_SID'), 'BaseType') as sysname), ':', cast(sql_variant_property(certproperty(@id, 'String_SID'), 'MaxLength') as int)),
                cast(sql_variant_property(certproperty(@id, 'key_length'), 'BaseType') as sysname), cast(certproperty(@id, 'SUBJECT') as nvarchar(10)),
                isnull(cast(certproperty(@id, 'Thumbprint') as varchar(10)), 'NULL'), isnull(cast(symkeyproperty(101, 'algorithm_desc') as varchar(10)), 'NULL'),
                (select count(*) from sys.certificates where sid = certproperty(@id, 'SID') and string_sid = cast(certproperty(@id, 'String_SID') as nvarchar(128))),
                (select count(*) from sys.certificates where sid = 0x010600000000000901000000 + thumbprint))
            """));

    [TestMethod]
    [DataRow("key_id('k')", "256")]
    [DataRow("key_id('[k]')", "256")]
    [DataRow("key_id('K')", "256")]
    [DataRow("key_id('dbo.k')", "NULL")]
    [DataRow("key_id('##MS_DatabaseMasterKey##')", "NULL")]
    [DataRow("key_name(newid())", "NULL")]
    [DataRow("key_name(key_guid('k'))", "k")]
    [DataRow("cert_id('c')", "256")]
    public void KeyLookups(string call, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(MasterKey + $"""
            create certificate c with subject = 'c';
            create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            select isnull(cast({call} as varchar(50)), 'NULL')
            """));

    [TestMethod]
    public void KeyDdl_RollsBackButOpeningAndClosingDoNot()
        => AreEqual("0|0|1", new Simulation().ExecuteScalar("""
            begin tran; create symmetric key t with algorithm = aes_256 encryption by password = 'Abcdefgh12'; rollback;
            declare @created int = (select count(*) from sys.symmetric_keys);
            create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            open symmetric key k decryption by password = 'Abcdefgh12';
            begin tran; close all symmetric keys; rollback;
            declare @closed int = (select count(*) from sys.openkeys);
            begin tran; open symmetric key k decryption by password = 'Abcdefgh12'; rollback;
            select concat_ws('|', @created, @closed, (select count(*) from sys.openkeys))
            """));

    [TestMethod]
    public void UserWithoutPermission_SeesOnlyTheKeysItOwns()
    {
        var refusals = new Simulation().AssertSqlError(MasterKey + """
            create user u without login;
            create symmetric key p1 with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            create symmetric key p2 authorization u with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            execute as user = 'u';
            create certificate u1 with subject = 'x';
            create symmetric key u2 with algorithm = aes_256 encryption by password = 'Abcdefgh12';
            open symmetric key p1 decryption by password = 'Abcdefgh12';
            open symmetric key p2 decryption by password = 'Abcdefgh12';
            drop master key;
            if (select count(*) from sys.symmetric_keys) = 1 and key_id('p1') is null and key_id('p2') = 257 and (select count(*) from sys.openkeys) = 1
                drop symmetric key p2;
            revert;
            """, 15247);
        CollectionAssert.AreEqual(new[] { 15247, 15247, 15151, 15151 }, refusals.Errors.Cast<SimulatedError>().Select(error => error.Number).ToArray());
    }

    [TestMethod]
    public void OpenAndCloseStillReachCursors()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            declare symmetric cursor for select 1;
            open symmetric;
            fetch next from symmetric;
            close symmetric;
            deallocate symmetric;
            select 1
            """));

    [TestMethod]
    public void BackupOfADatabaseWithKeys_RaisesNotSupported()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create symmetric key k with algorithm = aes_256 encryption by password = 'Abcdefgh12'");
        _ = Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("backup database simulated to disk = 'k.bak'"));
    }

    [TestMethod]
    [DataRow("create certificate c from file = 'c.cer'")]
    [DataRow("create symmetric key #k with algorithm = aes_256 encryption by password = 'Abcdefgh12'")]
    [DataRow("create certificate c with subject = 'c'; select certencoded(cert_id('c'))")]
    [DataRow("create certificate c with subject = 'c'; select certprivatekey(cert_id('c'), N'Abcdefgh12')")]
    public void UnbuiltKeySources_RaiseNotSupported(string statement)
        => _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(MasterKey + statement));
}
