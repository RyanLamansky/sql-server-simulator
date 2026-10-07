using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class BulkInsertTests
{
    /// <summary>A simulation whose bulk loads read <paramref name="files"/>, each UTF-8 text, from memory.</summary>
    internal static Simulation WithFiles(params (string Path, string Text)[] files) =>
        WithFileBytes([.. files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Text)))]);

    /// <summary>A simulation whose bulk loads read <paramref name="files"/> from memory.</summary>
    internal static Simulation WithFileBytes(params (string Path, byte[] Bytes)[] files)
    {
        var map = files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
        return new Simulation { OpenBulkFile = path => map.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : null };
    }

    [TestMethod]
    public void NoFileDelegate_EveryPathIsMissing()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            bulk insert t from '/data/t.txt'
            """, 4860, "Cannot bulk load. The file \"/data/t.txt\" does not exist or you don't have file access rights.");

    [TestMethod]
    public void MissingFile_EndsTheBatchUncaughtByTry()
    {
        var sim = WithFiles();
        _ = sim.ExecuteNonQuery("create table t (id int); create table log (step varchar(10))");
        var ex = sim.AssertSqlError("""
            begin try
                bulk insert t from 'nope.txt';
            end try
            begin catch
                insert log values ('caught');
            end catch
            insert log values ('after')
            """, 4860);
        AreEqual(1, ex.State);
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    public void MissingFile_InProcedure_EndsOnlyTheProcedure()
    {
        var sim = WithFiles();
        sim.ExecuteBatches(
            "create table t (id int)",
            "create procedure p as begin bulk insert t from 'nope.txt'; select 'in proc'; end");
        _ = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("create table log (s varchar(9)); exec p; insert log values ('after')"));
        AreEqual("after", sim.ExecuteScalar("select s from log"));
    }

    [TestMethod]
    public void DefaultTerminators_TabAndLineFeed_KeepCarriageReturn()
        => AreEqual("alpha\r|beta\r", WithFiles(("f.txt", "1\talpha\r\n2\tbeta\r\n")).ExecuteScalar("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.txt';
            select string_agg(name, '|') within group (order by id) from t
            """));

    [TestMethod]
    public void RowTerminatorEscapesAndHex()
        => AreEqual("alpha|beta", WithFiles(("f.txt", "1,alpha\r\n2,beta\r\n")).ExecuteScalar("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = '0x2c', rowterminator = '\r\n');
            select string_agg(name, '|') within group (order by id) from t
            """));

    [TestMethod]
    public void LastFieldWithoutTerminator_Loads_EmptyOneDropsTheRow()
    {
        var sim = WithFiles(("f.txt", "1,a\n3,gamma"), ("g.txt", "1,a\n2,"));
        _ = sim.ExecuteNonQuery("create table t (id int, name varchar(20)); create table u (id int, name varchar(20))");
        AreEqual("1:a,3:gamma", sim.ExecuteScalar("""
            bulk insert t from 'f.txt' with (fieldterminator = ',');
            select string_agg(concat(id, ':', name), ',') within group (order by id) from t
            """));
        AreEqual(1, sim.ExecuteScalar("bulk insert u from 'g.txt' with (fieldterminator = ','); select count(*) from u"));
    }

    [TestMethod]
    public void RowTooLong_LastFieldTakesTheRest()
        => AreEqual("a,extra", WithFiles(("f.txt", "1,a,extra\n")).ExecuteScalar("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = ',');
            select name from t
            """));

    [TestMethod]
    public void FileEndingInsideARow_IsUnexpectedEndOfFile()
    {
        var ex = WithFiles(("f.txt", "1,a\n2")).AssertSqlError("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = ',')
            """, 4832);
        CollectionAssert.AreEqual(new[] { 4832, 7399, 7330 }, ex.Errors.Select(e => e.Number).ToArray());
    }

    [TestMethod]
    public void FirstRowAndLastRow_SelectFileRows()
        => AreEqual("2,3,4", WithFiles(("f.txt", "1|a\n2|b\n3|c\n4|d\n5|e\n")).ExecuteScalar("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = '|', firstrow = 2, lastrow = 4);
            select string_agg(id, ',') within group (order by id) from t
            """));

    [TestMethod]
    public void FirstRowPastLastRow_IsRefused()
        => WithFiles(("f.txt", "1\n")).AssertSqlError("""
            create table t (id int);
            bulk insert t from 'f.txt' with (firstrow = 3, lastrow = 2)
            """, 4880);

    [TestMethod]
    public void Csv_QuotesEscapesAndEmbeddedNewlines()
    {
        var sim = WithFiles(("f.csv", "id,name,note\n1,\"a,b\",\"x \"\"q\"\" y\"\n2,\"multi\nline\",plain\n3,,\"\"\n"));
        _ = sim.ExecuteNonQuery("""
            create table t (id int, name varchar(20), note varchar(20));
            bulk insert t from 'f.csv' with (format = 'CSV', firstrow = 2)
            """);
        AreEqual("a,b", sim.ExecuteScalar("select name from t where id = 1"));
        AreEqual("x \"q\" y", sim.ExecuteScalar("select note from t where id = 1"));
        AreEqual("multi\nline", sim.ExecuteScalar("select name from t where id = 2"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where id = 3 and name is null and note is null"));
    }

    [TestMethod]
    public void Csv_MalformedRecord_EndsTheLoad()
    {
        var ex = WithFiles(("f.csv", "1,a\n2,b,x\n")).AssertSqlError("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.csv' with (format = 'CSV')
            """, 4879);
        AreEqual("Bulk load failed due to invalid column value in CSV data file f.csv in row 2, column 2.", ex.Errors[0].Message);
    }

    [TestMethod]
    public void Csv_MalformedRecordFirstRowSkips_IsColumnsInfoError()
        => WithFiles(("f.csv", "hdr\n1,a\n")).AssertSqlError("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.csv' with (format = 'CSV', firstrow = 2)
            """, 7301);

    [TestMethod]
    public void ConversionError_ReportsSkipsTheRowAndSetsError()
    {
        var sim = WithFiles(("f.txt", "x,alpha\n2,beta\n3,gamma\n"));
        _ = sim.ExecuteNonQuery("create table t (id int, name varchar(20)); create table seen (rc int, err int)");
        var ex = sim.AssertSqlError("bulk insert t from 'f.txt' with (fieldterminator = ','); insert seen select @@rowcount, @@error", 4864);
        AreEqual("Bulk load data conversion error (type mismatch or invalid character for the specified codepage) for row 1, column 1 (id).", ex.Errors[0].Message);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
        AreEqual("2:4864", sim.ExecuteScalar("select concat(rc, ':', err) from seen"));
    }

    [TestMethod]
    public void RowErrorsInsideTry_AreSwallowed()
        => AreEqual("nocatch:2:0", WithFiles(("f.txt", "x,a\n2,b\n3,c\n")).ExecuteScalar("""
            create table t (id int, name varchar(20));
            declare @r varchar(20);
            begin try
                bulk insert t from 'f.txt' with (fieldterminator = ',');
                set @r = concat('nocatch:', @@rowcount, ':', @@error);
            end try
            begin catch
                set @r = 'catch';
            end catch
            select @r
            """));

    [TestMethod]
    public void MaxErrorsExceeded_FailsTheLoadAndCatchReadsTheLast()
    {
        var file = string.Concat(Enumerable.Range(0, 11).Select(i => $"x{i},n\n")) + "1,ok\n";
        AreEqual("7330:0", WithFiles(("f.txt", file)).ExecuteScalar("""
            create table t (id int, name varchar(20));
            declare @r varchar(20);
            begin try
                bulk insert t from 'f.txt' with (fieldterminator = ',');
            end try
            begin catch
                set @r = concat(error_number(), ':', (select count(*) from t));
            end catch
            select @r
            """));
    }

    [TestMethod]
    public void MaxErrorsExceeded_ChainsTheProviderErrors()
    {
        var ex = WithFiles(("f.txt", "x,a\ny,b\n")).AssertSqlError("""
            create table t (id int, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = ',', maxerrors = 1)
            """, 4864);
        CollectionAssert.AreEqual(new[] { 4864, 4864, 4865, 7399, 7330 }, ex.Errors.Select(e => e.Number).ToArray());
        AreEqual("Cannot bulk load because the maximum number of errors (1) was exceeded.", ex.Errors[2].Message);
    }

    [TestMethod]
    public void MaxErrorsExceededInTransaction_RollsItBack()
    {
        var sim = WithFiles(("f.txt", "x,a\n1,b\n"));
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int, name varchar(20)); insert t values (0, 'kept')").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert t values (9, 'undone'); bulk insert t from 'f.txt' with (fieldterminator = ',', maxerrors = 0)").ExecuteNonQuery());
        AreEqual("0:1", connection.CreateCommand("select concat(@@trancount, ':', count(*)) from t").ExecuteScalar());
    }

    [TestMethod]
    public void ConversionRules_AreTheBulkProvidersNotCasts()
    {
        var sim = WithFiles(
            ("i.txt", " 12 \n- 3\n0012\n1.0\n1e2\n"),
            ("b.txt", "1\n0\ntrue\n 1\n"),
            ("m.txt", "1,000.50\n$5\n"),
            ("x.txt", "0a0B\n0x0A\nA\n"));
        _ = sim.ExecuteNonQuery("""
            create table i (v int); create table b (v bit); create table m (v money); create table x (v varbinary(4));
            """);
        _ = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("""
            bulk insert i from 'i.txt';
            bulk insert b from 'b.txt';
            bulk insert m from 'm.txt';
            bulk insert x from 'x.txt'
            """));
        AreEqual("-3,12,12", sim.ExecuteScalar("select string_agg(v, ',') within group (order by v) from i"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from b"));
        AreEqual(1000.5m, sim.ExecuteScalar("select v from m"));
        CollectionAssert.AreEqual(new byte[] { 0x0A, 0x0B }, (byte[])sim.ExecuteScalar("select v from x")!);
    }

    [TestMethod]
    public void TooLongStrings_VarcharTruncationNvarcharTypeMismatch()
    {
        var ex = WithFiles(("f.txt", "abcd\tabcd\n")).AssertSqlError("""
            create table t (a varchar(3), b nvarchar(10));
            create table u (a varchar(10), b nvarchar(3));
            bulk insert t from 'f.txt';
            bulk insert u from 'f.txt'
            """, 4863);
        AreEqual(4864, ex.Errors[1].Number);
    }

    [TestMethod]
    public void OutOfRange_IsOverflow()
        => WithFiles(("f.txt", "256\n")).AssertSqlError("""
            create table t (v tinyint);
            bulk insert t from 'f.txt'
            """, 4867);

    [TestMethod]
    public void IdentityField_IgnoredUnlessKeepIdentity()
    {
        var sim = WithFiles(("f.txt", "10,a\n20,b\n"));
        AreEqual("1,2:2", sim.ExecuteScalar("""
            create table t (id int identity, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = ',');
            select concat(string_agg(id, ',') within group (order by id), ':', scope_identity()) from t
            """));
        AreEqual("10,20:21", sim.ExecuteScalar("""
            create table k (id int identity, name varchar(20));
            bulk insert k from 'f.txt' with (fieldterminator = ',', keepidentity);
            insert k (name) values ('z');
            select concat(string_agg(case when id < 21 then id end, ',') within group (order by id), ':', max(id)) from k
            """));
    }

    [TestMethod]
    public void EmptyFields_TakeDefaultsUnlessKeepNulls()
    {
        var sim = WithFiles(("f.txt", "1,,\n"));
        AreEqual("1|dflt|nd", sim.ExecuteScalar("""
            create table t (id int, name varchar(20) default 'dflt', note varchar(5) default 'nd');
            bulk insert t from 'f.txt' with (fieldterminator = ',');
            select concat_ws('|', id, name, note) from t
            """));
        AreEqual(1, sim.ExecuteScalar("""
            create table k (id int, name varchar(20) default 'dflt', note varchar(5) default 'nd');
            bulk insert k from 'f.txt' with (fieldterminator = ',', keepnulls);
            select count(*) from k where name is null and note is null
            """));
    }

    [TestMethod]
    public void EmptyFieldIntoNotNull_IsARowError()
    {
        var sim = WithFiles(("f.txt", "1,a\n,b\n3,c\n"));
        _ = sim.ExecuteNonQuery("create table t (id int not null, name varchar(20))");
        var ex = sim.AssertSqlError("bulk insert t from 'f.txt' with (fieldterminator = ',')", 4869);
        AreEqual("The bulk load failed. Unexpected NULL value in data file row 2, column 1. The destination column (id) is defined as NOT NULL.", ex.Errors[0].Message);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void ComputedAndRowversionColumns_ConsumeAFieldEach()
        => AreEqual("1:2:beta", WithFiles(("f.txt", "1,alpha\n2,beta\n")).ExecuteScalar("""
            create table t (id int, c as id * 2, name varchar(20));
            bulk insert t from 'f.txt' with (fieldterminator = ',');
            select concat(id, ':', c, ':', name) from t
            """));

    [TestMethod]
    public void CheckConstraints_UncheckedAndUntrustedByDefault()
        => AreEqual("2:1", WithFiles(("f.txt", "1,5\n2,-5\n")).ExecuteScalar("""
            create table t (id int, v int constraint ck check (v > 0));
            bulk insert t from 'f.txt' with (fieldterminator = ',');
            select concat(count(*), ':', (select cast(is_not_trusted as int) from sys.check_constraints where name = 'ck')) from t
            """));

    [TestMethod]
    public void CheckConstraintsOption_Enforces()
        => WithFiles(("f.txt", "1,-5\n")).AssertSqlError("""
            create table t (id int, v int check (v > 0));
            bulk insert t from 'f.txt' with (fieldterminator = ',', check_constraints)
            """, 547);

    [TestMethod]
    public void Triggers_FireOnlyUnderFireTriggers()
    {
        var sim = WithFiles(("f.txt", "1\ta\n2\tb\n"));
        sim.ExecuteBatches(
            "create table t (id int, name varchar(20)); create table log (n int)",
            "create trigger tr on t after insert as insert log select count(*) from inserted");
        _ = sim.ExecuteNonQuery("bulk insert t from 'f.txt'");
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
        _ = sim.ExecuteNonQuery("bulk insert t from 'f.txt' with (fire_triggers)");
        AreEqual(2, sim.ExecuteScalar("select n from log"));
    }

    [TestMethod]
    public void InsteadOfTrigger_BypassedUnlessFireTriggers()
    {
        var sim = WithFiles(("f.txt", "1\ta\n2\tb\n"));
        sim.ExecuteBatches(
            "create table t (id int, name varchar(20)); create table log (n int)",
            "create trigger tr on t instead of insert as insert log select count(*) from inserted");
        _ = sim.ExecuteNonQuery("bulk insert t from 'f.txt'");
        _ = sim.ExecuteNonQuery("bulk insert t from 'f.txt' with (fire_triggers)");
        AreEqual("2:2", sim.ExecuteScalar("select concat((select count(*) from t), ':', (select n from log))"));
    }

    [TestMethod]
    public void View_TargetMapsFieldsToItsColumns()
        => AreEqual("1:a", WithFiles(("f.txt", "a\t1\n")).ExecuteBatchesScalar(
            "create table t (id int, x int, name varchar(20))",
            "create view v as select name, id from t",
            "bulk insert v from 'f.txt'; select concat(id, ':', name) from t"));

    [TestMethod]
    public void BatchSize_CommitsEachBatch_AConstraintEndsTheRest()
    {
        var sim = WithFiles(("f.txt", "1|a\n2|b\n3|c\n4|d\n5|e\n"));
        _ = sim.ExecuteNonQuery("create table t (id int primary key, name varchar(20)); insert t values (3, 'x')");
        _ = sim.AssertSqlError("bulk insert t from 'f.txt' with (fieldterminator = '|', batchsize = 2)", 2627);
        AreEqual("1,2,3", sim.ExecuteScalar("select string_agg(id, ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void BatchSize_EachFullBatchReportsItsOwnCount()
    {
        var sim = WithFiles(("f.txt", "1|a\n2|b\n3|c\n4|d\n5|e\n"));
        _ = sim.ExecuteNonQuery("create table t (id int, name varchar(20))");
        // Two batches of two close with DONE counts of their own ahead of the
        // statement's total, and ExecuteNonQuery sums them as SqlClient does.
        AreEqual(9, sim.ExecuteNonQuery("bulk insert t from 'f.txt' with (fieldterminator = '|', batchsize = 2)"));
        AreEqual(5, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Permission_NeedsAdministerBulkOperations()
    {
        var sim = WithFiles(("f.txt", "1\n"));
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            create login bl with password = 'Xx!12345678'; create user bl for login bl; grant insert on t to bl
            """);
        _ = sim.AssertSqlError("execute as login = 'bl'; bulk insert t from 'f.txt'", 4834);
    }

    [TestMethod]
    public void Permission_BulkadminWithoutControlServer_ReachesNoFile()
    {
        var sim = WithFiles(("f.txt", "1\n"));
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            create login bl with password = 'Xx!12345678'; create user bl for login bl; grant insert on t to bl;
            alter server role bulkadmin add member bl
            """);
        AreEqual(75, sim.AssertSqlError("execute as login = 'bl'; bulk insert t from 'f.txt'", 4860).State);
    }

    [TestMethod]
    public void Permission_ControlServerLoads()
    {
        var sim = WithFiles(("f.txt", "1\n"));
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            create login bl with password = 'Xx!12345678'; create user bl for login bl;
            use master; grant control server to bl; use simulated
            """);
        AreEqual(1, sim.ExecuteScalar("execute as login = 'bl'; bulk insert t from 'f.txt'; revert; select count(*) from t"));
    }

    [TestMethod]
    public void CodePage_IsRefusedOnLinux()
        => WithFiles(("f.txt", "1\n")).AssertSqlError("""
            create table t (id int);
            bulk insert t from 'f.txt' with (codepage = '65001')
            """, 16202, "Keyword or statement option 'codepage' is not supported on the 'Linux' platform.");

    [TestMethod]
    public void OptionGrammar()
    {
        var sim = WithFiles(("f.txt", "1\n"));
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("bulk insert t from 'f.txt' with (firstrow = 1, firstrow = 2)", 4130);
        sim.ValidateSyntaxError("bulk insert t from 'f.txt' with (datafiletype = 'bogus')", "datafiletype");
        sim.ValidateSyntaxError("bulk insert t from 'f.txt' with (firstrow = '2')", "firstrow");
        sim.ValidateSyntaxError("bulk insert t from 'f.txt' with firstrow = 2", "firstrow");
        sim.ValidateSyntaxError("declare @f varchar(9) = 'f.txt'; bulk insert t from @f", "@f");
    }

    [TestMethod]
    public void ErrorFile_IsAFileTheServerCannotCreate()
    {
        var ex = WithFiles(("f.txt", "1\n"), ("exists.txt", "")).AssertSqlError("""
            create table t (id int);
            bulk insert t from 'f.txt' with (errorfile = 'exists.txt')
            """, 4861);
        AreEqual("Cannot bulk load because the file \"exists.txt\" could not be opened. Operating system error code 80(The file exists.).", ex.Errors[0].Message);
        AreEqual("Cannot bulk load because the file \"exists.txt.Error.Txt\" could not be opened. Operating system error code 5(Access is denied.).", ex.Errors[1].Message);
    }

    [TestMethod]
    public void MissingTarget_IsInvalidObjectAtState160()
        => AreEqual(160, WithFiles(("f.txt", "1\n")).AssertSqlError("bulk insert nowhere from 'f.txt'", 208).State);

    [TestMethod]
    public void Widechar_WithAndWithoutByteOrderMark()
    {
        var wide = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("1\tä€\n")).ToArray();
        var sim = WithFileBytes(("w.txt", wide));
        _ = sim.ExecuteNonQuery("create table t (id int, name nvarchar(20))");
        AreEqual("ä€", sim.ExecuteScalar("bulk insert t from 'w.txt' with (datafiletype = 'widechar'); select name from t"));

        // A char load of a file marked UTF-16 reads it as widechar, and says so twice.
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand("bulk insert t from 'w.txt'").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 4830, 4830 }, messages);
    }

    [TestMethod]
    public void Native_ReadsTheBcpLayout()
    {
        // bcp -n of (i int not null, j int null, v varchar(10) null, nv nvarchar(10) not null, d decimal(5, 2) null, b bit not null)
        byte[] row =
        [
            1, 0, 0, 0,             // i
            0xFF,                   // j NULL
            2, 0, (byte)'a', (byte)'b',
            2, 0, 0xE9, 0,          // nv 'é'
            19, 5, 2, 1, 0x96, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // d 1.50
            1, 1,                   // b
        ];
        AreEqual("1||ab|é|1.50|1", WithFileBytes(("n.dat", row)).ExecuteScalar("""
            create table t (i int not null, j int null, v varchar(10) null, nv nvarchar(10) not null, d decimal(5, 2) null, b bit not null);
            bulk insert t from 'n.dat' with (datafiletype = 'native');
            select concat_ws('|', i, isnull(cast(j as varchar), ''), v, nv, d, cast(b as int)) from t
            """));
    }

    [TestMethod]
    public void FormatFile_SkipsAndReordersFields()
        => AreEqual("1:n1,2:n2", WithFiles(
            ("d.txt", "n1,zz,1\r\nn2,yy,2\r\n"),
            ("f.fmt", "14.0\n3\n1 SQLCHAR 0 10 \",\" 2 name \"\"\n2 SQLCHAR 0 50 \",\" 0 skip \"\"\n3 SQLCHAR 0 20 \"\\r\\n\" 1 id \"\"\n"))
            .ExecuteScalar("""
                create table t (id int, name varchar(20));
                bulk insert t from 'd.txt' with (formatfile = 'f.fmt');
                select string_agg(concat(id, ':', name), ',') within group (order by id) from t
                """));

    [TestMethod]
    public void XmlFormatFile_MapsColumnsInRowOrder()
        => AreEqual("1:alpha,2:beta", WithFiles(
            ("d.csv", "1,alpha\n2,beta\n"),
            ("f.xml", """
                <?xml version="1.0"?>
                <BCPFORMAT xmlns="http://schemas.microsoft.com/sqlserver/2004/bulkload/format" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                 <RECORD>
                  <FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," MAX_LENGTH="10"/>
                  <FIELD ID="2" xsi:type="CharTerm" TERMINATOR="\n" MAX_LENGTH="20"/>
                 </RECORD>
                 <ROW>
                  <COLUMN SOURCE="1" NAME="id" xsi:type="SQLINT"/>
                  <COLUMN SOURCE="2" NAME="name" xsi:type="SQLVARYCHAR"/>
                 </ROW>
                </BCPFORMAT>
                """))
            .ExecuteScalar("""
                create table t (id int, name varchar(20));
                bulk insert t from 'd.csv' with (formatfile = 'f.xml');
                select string_agg(concat(id, ':', name), ',') within group (order by id) from t
                """));

    /// <summary>
    /// A bulk-loaded row's ROW START is its transaction's system time, as any
    /// other write's is, so a later UPDATE in the transaction finds no period
    /// ending before it began (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SystemVersionedTarget_StampsTheTransactionsSystemTime()
        => AreEqual(1, WithFiles(("f.txt", "1\t\n2\t\n")).ExecuteScalar("""
            create table t (id int primary key, v int null,
                ts datetime2 generated always as row start hidden, te datetime2 generated always as row end hidden,
                period for system_time (ts, te)) with (system_versioning = on);
            begin tran;
            waitfor delay '00:00:00.050';
            bulk insert t from 'f.txt';
            update t set v = 1;
            select count(distinct ts) from t
            """));

    [TestMethod]
    public void CharacterFields_ConvertToDecimalFloatTemporalAndGuidColumns()
    {
        var sim = WithFiles(("num.txt",
            "1.005\t2.5e3\t2024-01-02 03:04:05.1234567\t2024-01-02\t6F9619FF-8B86-D011-B42D-00C04FC964FF\t1.5\n"
            + "2\tinf\tnotadate\t2024-13-01\tnotaguid\t1e40\n"
            + "-0.5\t-1E-3\t2024-01-02T03:04:05\t20240102\t6f9619ff-8b86-d011-b42d-00c04fc964ff\t-2\n"
            + "999.995\t1,5\t2024-01-02 03:04:05\t2024-01-02 10:00\t{6F9619FF-8B86-D011-B42D-00C04FC964FF}\t3.4\n"));
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (d decimal(5, 2), f float, dt datetime, da date, g uniqueidentifier, r real)").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("bulk insert t from 'num.txt' with (maxerrors = 100)").ExecuteNonQuery());
        AreEqual("Bulk load data conversion error (type mismatch or invalid character for the specified codepage) for row 2, column 2 (f).", ex.Errors[0].Message);
        AreEqual("Bulk load data conversion error (truncation) for row 4, column 1 (d).", ex.Errors[1].Message);
        AreEqual("1.01|2500|2024-01-02 03:04:05.123|2024-01-02|6F9619FF-8B86-D011-B42D-00C04FC964FF|1.5,-0.50|-0.001|2024-01-02 03:04:05.000|2024-01-02|6F9619FF-8B86-D011-B42D-00C04FC964FF|-2",
            connection.CreateCommand("select string_agg(concat_ws('|', d, f, convert(varchar(23), dt, 121), da, g, r), ',') within group (order by d desc) from t").ExecuteScalar());
    }

    [TestMethod]
    public void WidecharLoadOfAnUnmarkedFile_ReadsItAsChar_SayingSoTwice()
    {
        var sim = WithFiles(("p.txt", "1\tab\n"));
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        AreEqual(1, connection.CreateCommand("create table p (i int, s varchar(10)); bulk insert p from 'p.txt' with (datafiletype = 'widechar')").ExecuteNonQuery());
        CollectionAssert.AreEqual(new[] { 4831, 4831 }, messages);
    }

    [TestMethod]
    public void OrderHint_NamingNoColumn_Says4817AndLoads()
    {
        var sim = WithFiles(("p.txt", "1\tab\n"));
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Message));
        AreEqual(1, connection.CreateCommand("create table p (i int, s varchar(10)); bulk insert p from 'p.txt' with (order (nope asc, i desc))").ExecuteNonQuery());
        CollectionAssert.AreEqual(new[] { "Could not bulk load. The sorted column 'nope' is not valid. The ORDER hint is ignored." }, messages);
    }

    [TestMethod]
    [DataRow("order (i")]
    [DataRow("order i")]
    [DataRow("order (i, )")]
    [DataRow("order (1)")]
    public void OrderHint_Malformed_Raises102(string option)
        => _ = WithFiles(("p.txt", "1\n")).AssertSqlError($"create table p (i int); bulk insert p from 'p.txt' with ({option})", 102);

    [TestMethod]
    public void OrderHint_TwoDirections_Raises156()
        => WithFiles(("p.txt", "1\n")).AssertSqlError("create table p (i int); bulk insert p from 'p.txt' with (order (i asc desc))", 156,
            "Incorrect syntax near the keyword 'desc'.");

    [TestMethod]
    public void RunTimeOptionRefusals()
    {
        var sim = WithFiles(("p.txt", "1\tab\n"));
        _ = sim.ExecuteNonQuery("create table p (i int, s varchar(10))");
        var ex = sim.AssertSqlError("bulk insert p from 'p.txt' with (data_source = 'nods')", 12703);
        AreEqual("Referenced external data source \"nods\" not found.", ex.Errors[0].Message);
        AreEqual((byte)2, ex.State);
        sim.AssertSqlError("bulk insert p from 'p.txt' with (format = 'csv', datafiletype = 'native')", 5339,
            "CSV format option is supported for char and widechar datafiletype options.");
        sim.AssertSqlError("bulk insert p from 'p.txt' with (format = 'csv', fieldquote = 'ab')", 4878,
            "Invalid quote character specified for bulk load. Quote character can be one single byte or Unicode character.");
    }

    [TestMethod]
    public void XmlFormatFile_NativeFixedAndPrefixedFields()
    {
        var row = new List<byte>();
        row.AddRange(BitConverter.GetBytes(7));
        row.AddRange(BitConverter.GetBytes((ushort)2));
        row.AddRange("hi"u8.ToArray());
        row.Add(8);
        row.AddRange(BitConverter.GetBytes(1.25));
        row.AddRange(BitConverter.GetBytes(8));
        row.AddRange(BitConverter.GetBytes(ushort.MaxValue));
        row.Add(0xFF);
        var sim = WithFileBytes(("n.dat", [.. row]), ("n.xml", Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <BCPFORMAT xmlns="http://schemas.microsoft.com/sqlserver/2004/bulkload/format" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
             <RECORD>
              <FIELD ID="1" xsi:type="NativeFixed" LENGTH="4"/>
              <FIELD ID="2" xsi:type="NativePrefix" PREFIX_LENGTH="2" MAX_LENGTH="20"/>
              <FIELD ID="3" xsi:type="NativePrefix" PREFIX_LENGTH="1"/>
             </RECORD>
             <ROW>
              <COLUMN SOURCE="1" NAME="i" xsi:type="SQLINT"/>
              <COLUMN SOURCE="2" NAME="v" xsi:type="SQLVARYCHAR"/>
              <COLUMN SOURCE="3" NAME="f" xsi:type="SQLFLT8"/>
             </ROW>
            </BCPFORMAT>
            """)));
        AreEqual("7|hi|1.25,8||", sim.ExecuteScalar("""
            create table n (i int, v varchar(20), f float);
            bulk insert n from 'n.dat' with (formatfile = 'n.xml');
            select string_agg(concat_ws('|', i, isnull(v, ''), isnull(cast(f as varchar), '')), ',') within group (order by i) from n
            """));
    }

    [TestMethod]
    public void NonXmlFormatFile_NativeHostType_LoadsAndReadsAsARowset()
    {
        byte[] data = [.. BitConverter.GetBytes(5), .. BitConverter.GetBytes((ushort)3), .. "abc"u8.ToArray()];
        var sim = WithFileBytes(("n.dat", data), ("n.fmt", Encoding.UTF8.GetBytes("14.0\n2\n1 SQLINT 0 4 \"\" 1 i \"\"\n2 SQLCHAR 2 20 \"\" 2 v \"\"\n")));
        AreEqual("5:abc", sim.ExecuteScalar("create table n (i int, v varchar(20)); bulk insert n from 'n.dat' with (formatfile = 'n.fmt'); select concat(i, ':', v) from n"));
        AreEqual("5:abc", sim.ExecuteScalar("select concat(i, ':', v) from openrowset(bulk 'n.dat', formatfile = 'n.fmt') x"));
    }
}
