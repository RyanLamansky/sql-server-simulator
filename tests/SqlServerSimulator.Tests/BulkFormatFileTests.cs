using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What real refuses in a bcp format file, and when: reading one as it
/// compiles the statement naming it, each check in real's order (probed
/// 2026-10-25 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class BulkFormatFileTests
{
    private const string Data = "1,alpha\n2,beta\n";

    private const string Fields = """<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," MAX_LENGTH="10"/><FIELD ID="2" xsi:type="CharTerm" TERMINATOR="\n" MAX_LENGTH="20"/>""";

    private const string Columns = """<COLUMN SOURCE="1" NAME="id" xsi:type="SQLINT"/><COLUMN SOURCE="2" NAME="name" xsi:type="SQLVARYCHAR"/>""";

    private const string FormatRoot = """<BCPFORMAT xmlns="http://schemas.microsoft.com/sqlserver/2004/bulkload/format" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">""";

    /// <summary>An XML format file whose fields sit on line 3 and columns on line 6.</summary>
    private static string Xml(string fields = Fields, string columns = Columns) =>
        $"{FormatRoot}\n <RECORD>\n  {fields}\n </RECORD>\n <ROW>\n  {columns}\n </ROW>\n</BCPFORMAT>\n";

    /// <summary>A simulation holding <see cref="Data"/> as <c>d.txt</c>, <paramref name="format"/> as <c>f.fmt</c>, and a table <c>t</c> to load.</summary>
    private static Simulation WithFormat(string format)
    {
        var sim = BulkInsertTests.WithFiles(("d.txt", Data), ("f.fmt", format));
        _ = sim.ExecuteNonQuery("create table t (id int, name varchar(20)); create table log (step varchar(10))");
        return sim;
    }

    /// <summary>
    /// The non-XML form is a stream of tokens, read and refused one at a
    /// time; two fields loading one column and a collation that isn't one are
    /// refused once every field has read.
    /// </summary>
    [TestMethod]
    [DataRow("14.0x 1 1 SQLCHAR 0 10 \"\\n\" 1 id \"\"", 4820, "Cannot bulk load. Unknown version of format file \"f.fmt\".")]
    [DataRow("1e1 1 1 SQLCHAR 0 10 \"\\n\" 1 id \"\"", 4820, "Cannot bulk load. Unknown version of format file \"f.fmt\".")]
    [DataRow("14.0", 4822, "Cannot bulk load. Invalid number of columns in the format file \"f.fmt\".")]
    [DataRow("14.0 0", 4822, "Cannot bulk load. Invalid number of columns in the format file \"f.fmt\".")]
    [DataRow("14.0 abc 1 SQLCHAR 0 10 \"\\n\" 1 id \"\"", 4822, "Cannot bulk load. Invalid number of columns in the format file \"f.fmt\".")]
    [DataRow("14.0 2 2 SQLCHAR 0 10 \",\" 1 id \"\" 1 SQLCHAR 0 20 \"\\n\" 2 name \"\"", 4823, "Cannot bulk load. Invalid column number in the format file \"f.fmt\".")]
    [DataRow("14.0 1 1 sqlchar 0 10 \"\\n\" 1 id \"\"", 4824, "Cannot bulk load. Invalid data type for column number 1 in the format file \"f.fmt\".")]
    [DataRow("14.0 1 1 SQLCHAR 3 10 \"\\n\" 1 id \"\"", 4825, "Cannot bulk load. Invalid prefix for column number 1 in the format file \"f.fmt\".")]
    [DataRow("14.0 1 1 SQLCHAR abc abc \"\\n\" 1 id \"\"", 4825, "Cannot bulk load. Invalid prefix for column number 1 in the format file \"f.fmt\".")]
    [DataRow("14.0 1 1 SQLCHAR 0 -1 \"\\n\" 1 id \"\"", 4826, "Cannot bulk load. Invalid column length for column number 1 in the format file \"f.fmt\".")]
    [DataRow("14.0 1 1 SQLCHAR 0 99999999999 \"\\n\" 1 id \"\"", 4826, "Cannot bulk load. Invalid column length for column number 1 in the format file \"f.fmt\".")]
    [DataRow("14.0 1 1 SQLCHAR 0 10 \"\\n\" -1 id \"\"", 4828, "Cannot bulk load. Invalid destination table column number for source column 1 in the format file \"f.fmt\".")]
    [DataRow("14.0 3 1 SQLCHAR 0 1 \"\" 1 a \"\" 2 SQLCHAR 0 1 \"\" 1 b \"\" 3 SQLFOO 0 9 \"\\n\" 2 c \"\"", 4824, "Cannot bulk load. Invalid data type for column number 3 in the format file \"f.fmt\".")]
    [DataRow("14.0 3 1 SQLCHAR 0 1 \"\" 1 a \"\" 2 SQLCHAR 0 1 \"\" 2 b \"\" 3 SQLCHAR 0 9 \"\\n\" 2 c bogus", 4828, "Cannot bulk load. Invalid destination table column number for source column 2 in the format file \"f.fmt\".")]
    [DataRow("14.0 2 1 SQLCHAR 0 10 \",\" 1 id \"\" 2 SQLCHAR 0 20 \"\\n\" 2 name raw", 4839, "Cannot perform the bulk load. Invalid collation name for source column 2 in the format file \"f.fmt\".")]
    [DataRow("14.0 3 1 SQLCHAR 0 10 \",\" 1 id \"\" 2 SQLCHAR 0 20 \"\\n\" 2 name \"\"", 4862, "Cannot bulk load because the file \"f.fmt\" is incomplete or could not be read. Operating system error code (null).")]
    [DataRow("14.0 1 1 SQLCHAR 0 10 \"\\n\" 1", 4862, "Cannot bulk load because the file \"f.fmt\" is incomplete or could not be read. Operating system error code (null).")]
    [DataRow("", 4862, "Cannot bulk load because the file \"f.fmt\" is incomplete or could not be read. Operating system error code 38(Reached the end of the file.).")]
    public void NonXml_RefusesWhatRealRefuses(string format, int number, string message)
        => WithFormat(format).AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", number, message);

    [TestMethod]
    public void NonXml_ACollationRefusedOnTheFirstField_IsFollowedByMsg4822()
    {
        var ex = WithFormat("14.0 2 1 SQLCHAR 0 10 \",\" 1 id DATABASE_DEFAULT 2 SQLCHAR 0 20 \"\\n\" 2 name \"\"")
            .AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", 4839);
        AreEqual("4839,4822", string.Join(",", ex.Errors.Select(error => error.Number)));
    }

    /// <summary>
    /// Versions 6.0, 7.0 and 17.0 lay a field out in seven tokens, without the
    /// collation 8.0 to 16.0 end it with; host types and <c>RAW</c> are
    /// case-sensitive while a collation's name isn't.
    /// </summary>
    [TestMethod]
    [DataRow("17.0 2 1 SQLCHAR 0 10 \",\" 1 id 2 SQLCHAR 0 20 \"\\n\" 2 name", "1:alpha,2:beta")]
    [DataRow("6.0 2 1 SQLCHAR 0 10 \",\" 1 id 2 SQLCHAR 0 20 \"\\n\" 2 name", "1:alpha,2:beta")]
    [DataRow("9.0 2 1 SQLCHAR 0 10 \",\" 1 id RAW 2 SQLCHAR 0 20 \"\\n\" 2 name latin1_general_ci_as", "1:alpha,2:beta")]
    public void NonXml_LoadsEachVersionsLayout(string format, string loaded)
        => AreEqual(loaded, WithFormat(format).ExecuteScalar("""
            bulk insert t from 'd.txt' with (formatfile = 'f.fmt');
            select string_agg(concat(id, ':', name), ',') within group (order by id) from t
            """));

    [TestMethod]
    public void NonXml_SeventeenWithACollation_IsAFieldShort()
        => WithFormat("17.0 2 1 SQLCHAR 0 10 \",\" 1 id \"\" 2 SQLCHAR 0 20 \"\\n\" 2 name \"\"")
            .AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", 4823, "Cannot bulk load. Invalid column number in the format file \"f.fmt\".");

    /// <summary>
    /// A file whose first token doesn't lead with a number above zero is XML
    /// to real, refused where its text runs out or meets markup — at the line
    /// and character after the last one read — or where no root element
    /// follows what may precede one.
    /// </summary>
    [TestMethod]
    [DataRow("abc", 9422, "XML parsing: line 1, character 3, incorrect document syntax")]
    [DataRow("0\n2\n", 9422, "XML parsing: line 3, character 0, incorrect document syntax")]
    [DataRow("<?xml version=\"1.0\"?>\nabc <BCPFORMAT/>", 9422, "XML parsing: line 2, character 5, incorrect document syntax")]
    [DataRow("   \n  ", 9435, "XML parsing: line 2, character 2, one root element")]
    [DataRow("<?xml version=\"1.0\"?>\n<!-- c -->\n", 9435, "XML parsing: line 3, character 0, one root element")]
    [DataRow("<foo/>", 4856, "Line 1 in format file \"f.fmt\": unexpected info item.")]
    [DataRow("<bcpformat xmlns=\"http://schemas.microsoft.com/sqlserver/2004/bulkload/format\"/>", 4855, "Line 1 in format file \"f.fmt\": unexpected element \"bcpformat\".")]
    public void Xml_RefusesADocumentWithoutAFormatRoot(string format, int number, string message)
    {
        var sim = WithFormat(format);
        sim.AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", number, message);
        var ex = sim.AssertSqlError("select * from openrowset(bulk 'd.txt', formatfile = 'f.fmt') x", number);
        AreEqual(number is 9422 or 9435 ? 48 : 1, ex.State);
    }

    /// <summary>
    /// The root holds a <c>RECORD</c> then a <c>ROW</c>; a misplaced one of
    /// those is an unexpected element, anything else out of place among the
    /// root's children an unexpected info item, and what follows the root's
    /// end is never read.
    /// </summary>
    [TestMethod]
    [DataRow("{0}\n <ROW>\n  {2}\n </ROW>\n</BCPFORMAT>\n", 4855, "Line 2 in format file \"f.fmt\": unexpected element \"ROW\".")]
    [DataRow("{0}\n <FOO/>\n</BCPFORMAT>\n", 4856, "Line 2 in format file \"f.fmt\": unexpected info item.")]
    [DataRow("{0}\n <RECORD>\n  {1}\n </RECORD>\n</BCPFORMAT>\n", 4856, "Line 5 in format file \"f.fmt\": unexpected info item.")]
    [DataRow("{0}\n <RECORD>\n  {1}\n </RECORD>\n <ROW>\n  {2}\n </ROW>\n <FOO/>\n</BCPFORMAT>\n", 4856, "Line 8 in format file \"f.fmt\": unexpected info item.")]
    [DataRow("{0}\n <RECORD>\n  {1}\n </RECORD>\n <ROW>\n </ROW>\n</BCPFORMAT>\n", 4822, "Cannot bulk load. Invalid number of columns in the format file \"f.fmt\".")]
    public void Xml_RefusesTheRootsChildrenOutOfPlace(string layout, int number, string message)
        => WithFormat(string.Format(System.Globalization.CultureInfo.InvariantCulture, layout, FormatRoot, Fields, Columns))
            .AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", number, message);

    [TestMethod]
    public void Xml_WhatFollowsTheRootIsNeverRead()
        => AreEqual("1:alpha,2:beta", WithFormat(Xml() + "<x/> text <unclosed>").ExecuteScalar("""
            bulk insert t from 'd.txt' with (formatfile = 'f.fmt');
            select string_agg(concat(id, ':', name), ',') within group (order by id) from t
            """));

    /// <summary>
    /// A <c>FIELD</c>'s attributes go through in written order — a name no
    /// field takes, a value that doesn't read, an <c>ID</c> taken twice — and
    /// then the element: its type, its <c>ID</c>, what its type requires, and
    /// the attributes its type doesn't take in a fixed order of their own.
    /// </summary>
    [TestMethod]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," FOO="1"/>""", 4857, "Attribute \"FOO\" could not be specified for this type.")]
    [DataRow("""<FIELD xmlns:z="urn:z" ID="1" xsi:type="CharTerm" TERMINATOR=","/>""", 4857, "Attribute \"\" could not be specified for this type.")]
    [DataRow("""<FIELD ID="1" FOO="1" xsi:type="Bad"/>""", 4857, "Attribute \"FOO\" could not be specified for this type.")]
    [DataRow("""<FIELD ID="1" xsi:type="charterm" TERMINATOR=","/>""", 4858, "bad value charterm for attribute \"xsi:type\".")]
    [DataRow("""<FIELD ID="1" MAX_LENGTH="0" xsi:type="CharTerm" TERMINATOR=","/>""", 4858, "bad value 0 for attribute \"MAX_LENGTH\".")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," MAX_LENGTH=" 10 "/>""", 4858, "bad value  10  for attribute \"MAX_LENGTH\".")]
    [DataRow("""<FIELD ID="1" xsi:type="CharPrefix" PREFIX_LENGTH="3"/>""", 4858, "bad value 3 for attribute \"PREFIX_LENGTH\".")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR=""/>""", 4858, "bad value  for attribute \"TERMINATOR\".")]
    [DataRow("""<FIELD ID="2" xsi:type="CharTerm" TERMINATOR=","/><FIELD ID="2" xsi:type="Bad" TERMINATOR="\n"/>""", 4872, "duplicate element id \"2\".")]
    [DataRow("""<FIELD ID="2" xsi:type="CharTerm" TERMINATOR=","/><FIELD xsi:type="Bad" ID="2" TERMINATOR="\n"/>""", 4858, "bad value Bad for attribute \"xsi:type\".")]
    [DataRow("""<FIELD TERMINATOR=","/>""", 4859, "required attribute \"xsi:type\" is missing.")]
    [DataRow("""<FIELD xsi:type="CharTerm"/>""", 4859, "required attribute \"ID\" is missing.")]
    [DataRow("""<FIELD ID="" xsi:type="CharTerm" TERMINATOR=","/>""", 4859, "required attribute \"ID\" is missing.")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" PREFIX_LENGTH="2"/>""", 4859, "required attribute \"TERMINATOR\" is missing.")]
    [DataRow("""<FIELD ID="1" xsi:type="CharFixed"/>""", 4859, "required attribute \"LENGTH\" is missing.")]
    [DataRow("""<FIELD ID="1" xsi:type="NativeFixed" LENGTH="4" COLLATION="Latin1_General_CI_AS" TERMINATOR=","/>""", 4857, "Attribute \"TERMINATOR\" could not be specified for this type.")]
    [DataRow("""<FIELD ID="1" xsi:type="NativeFixed" LENGTH="4" TERMINATOR="," MAX_LENGTH="4"/>""", 4857, "Attribute \"MAX_LENGTH\" could not be specified for this type.")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR=","><FOO/></FIELD>""", 4855, "unexpected element \"FOO\".")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR=","/><FOO/>""", 4855, "unexpected element \"FOO\".")]
    public void Xml_RefusesAFieldAsRealReadsIt(string fields, int number, string message)
        => WithFormat(Xml(fields)).AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", number, "Line 3 in format file \"f.fmt\": " + message);

    /// <summary>
    /// A <c>COLUMN</c> reads as a <c>FIELD</c> does: its <c>SOURCE</c> must
    /// name a field no earlier column named; <c>SCALE</c>, <c>PRECISION</c>
    /// and <c>LENGTH</c> are refused in that order where its type doesn't
    /// take them; a time type's <c>SCALE</c> runs to 7, and a decimal one
    /// without a <c>PRECISION</c> at least as large is a bad value reported
    /// as empty.
    /// </summary>
    [TestMethod]
    [DataRow("""<COLUMN SOURCE="9" NAME="id" xsi:type="BAD"/>""", 4873, "referencing non-existing element id \"9\".")]
    [DataRow("""<COLUMN xsi:type="BAD" SOURCE="9" NAME="id"/>""", 4858, "bad value BAD for attribute \"xsi:type\".")]
    [DataRow("""<COLUMN SOURCE="1"/><COLUMN SOURCE="1" xsi:type="BAD"/>""", 4874, "duplicate element id reference \"1\".")]
    [DataRow("""<COLUMN NAME="id" LENGTH="5"/>""", 4859, "required attribute \"SOURCE\" is missing.")]
    [DataRow("""<COLUMN SOURCE="1" LENGTH="5"/>""", 4857, "Attribute \"LENGTH\" could not be specified for this type.")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLINT" PRECISION="5" SCALE="2"/>""", 4857, "Attribute \"SCALE\" could not be specified for this type.")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLINT" LENGTH="5" PRECISION="5"/>""", 4857, "Attribute \"PRECISION\" could not be specified for this type.")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLTIME" SCALE="9" PRECISION="5"/>""", 4857, "Attribute \"PRECISION\" could not be specified for this type.")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLTIME" SCALE="8"/>""", 4858, "bad value 8 for attribute \"SCALE\".")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLDECIMAL" SCALE="2"/>""", 4858, "bad value  for attribute \"SCALE\".")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLDECIMAL" PRECISION="5" SCALE="6"/>""", 4858, "bad value  for attribute \"SCALE\".")]
    [DataRow("""<COLUMN SOURCE="1" xsi:type="SQLDECIMAL" PRECISION="38" SCALE="39"/>""", 4858, "bad value 39 for attribute \"SCALE\".")]
    [DataRow("""<COLUMN SOURCE="1" NULLABLE="yes"/>""", 4858, "bad value yes for attribute \"NULLABLE\".")]
    public void Xml_RefusesAColumnAsRealReadsIt(string columns, int number, string message)
        => WithFormat(Xml(columns: columns)).AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", number, "Line 6 in format file \"f.fmt\": " + message);

    [TestMethod]
    public void Xml_ACollationIsCheckedOnceTheWalkEnds()
    {
        var ex = WithFormat(Xml("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," COLLATION="bogus"/>""", """<COLUMN SOURCE="1"/><COLUMN SOURCE="9"/>"""))
            .AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", 4873);
        AreEqual(1, ex.Errors.Count);
        ex = WithFormat(Xml("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," COLLATION="bogus"/>""", "")).AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", 4839);
        AreEqual("4839,4822", string.Join(",", ex.Errors.Select(error => error.Number)));
    }

    /// <summary>
    /// A rowset's column without an <c>xsi:type</c> takes its field's type, a
    /// time type's <c>SCALE</c> sets its fractional digits, and a decimal
    /// column with a zero <c>PRECISION</c> is refused as the non-XML form's
    /// decimal fields are.
    /// </summary>
    [TestMethod]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," MAX_LENGTH="12"/>""", """<COLUMN SOURCE="1"/>""", "varchar(12)")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR=","/>""", """<COLUMN SOURCE="1"/>""", "varchar(8000)")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR="," MAX_LENGTH="9000"/>""", """<COLUMN SOURCE="1"/>""", "varchar(max)")]
    [DataRow("""<FIELD ID="1" xsi:type="NCharTerm" TERMINATOR="," MAX_LENGTH="12"/>""", """<COLUMN SOURCE="1"/>""", "nvarchar(6)")]
    [DataRow("""<FIELD ID="1" xsi:type="NCharTerm" TERMINATOR=","/>""", """<COLUMN SOURCE="1"/>""", "nvarchar(4000)")]
    [DataRow("""<FIELD ID="1" xsi:type="NativePrefix" PREFIX_LENGTH="2" MAX_LENGTH="30"/>""", """<COLUMN SOURCE="1"/>""", "varbinary(30)")]
    [DataRow("""<FIELD ID="1" xsi:type="NativeFixed" LENGTH="4"/>""", """<COLUMN SOURCE="1"/>""", "varbinary(4)")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR=","/>""", """<COLUMN SOURCE="1" xsi:type="SQLDATETIME2" SCALE="3"/>""", "datetime2(3)")]
    [DataRow("""<FIELD ID="1" xsi:type="CharTerm" TERMINATOR=","/>""", """<COLUMN SOURCE="1" xsi:type="SQLTIME"/>""", "time(7)")]
    public void Xml_RowsetColumnTypes(string field, string column, string type)
    {
        var format = Xml(field + """<FIELD ID="2" xsi:type="CharTerm" TERMINATOR="\n"/>""", column + """<COLUMN SOURCE="2" NAME="name" xsi:type="SQLVARYCHAR"/>""");
        AreEqual(type, WithFormat(format).ExecuteScalar("select system_type_name from sys.dm_exec_describe_first_result_set(N'select * from openrowset(bulk ''d.txt'', formatfile = ''f.fmt'') x', null, 0) where column_ordinal = 1"));
    }

    [TestMethod]
    public void Xml_ADecimalColumnWithAZeroPrecision_IsRefusedInARowset()
    {
        var sim = WithFormat(Xml(columns: """<COLUMN SOURCE="1" NAME="id" xsi:type="SQLNUMERIC" PRECISION="0"/><COLUMN SOURCE="2" NAME="name"/>"""));
        sim.AssertSqlError("select * from openrowset(bulk 'd.txt', formatfile = 'f.fmt') x", 4838, "The bulk data source does not support the SQLNUMERIC or SQLDECIMAL data types.");
        AreEqual(2, sim.ExecuteScalar("bulk insert t from 'd.txt' with (formatfile = 'f.fmt'); select count(*) from t"));
    }

    /// <summary>
    /// Real reads the format file compiling the statement, so a refusal fails
    /// the batch before anything in it runs — an untaken branch's included —
    /// and rolls back the transaction a batch before it opened.
    /// </summary>
    [TestMethod]
    [DataRow("insert log values ('before'); bulk insert t from 'd.txt' with (formatfile = 'f.fmt')")]
    [DataRow("insert log values ('before'); if 1 = 0 bulk insert t from 'd.txt' with (formatfile = 'f.fmt')")]
    [DataRow("insert log values ('before'); select * from openrowset(bulk 'd.txt', formatfile = 'f.fmt') x")]
    [DataRow("insert log values ('before'); if 1 = 0 select * from openrowset(bulk 'd.txt', formatfile = 'f.fmt') x")]
    public void ARefusal_FailsTheBatchAsItCompiles_AndRollsTheTransactionBack(string batch)
    {
        var sim = WithFormat("14.0 1 1 SQLFOO 0 10 \"\\n\" 1 id \"\"");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran; insert log values ('tran')").ExecuteNonQuery();
        AreEqual(4824, Throws<SimulatedSqlException>(() => connection.CreateCommand(batch).ExecuteNonQuery()).Number);
        AreEqual("0:0", connection.CreateCommand("select concat(@@trancount, ':', (select count(*) from log))").ExecuteScalar());
    }

    /// <summary>
    /// A statement the compile deferred reads the format file as it runs,
    /// where the refusal ends the batch uncaught by its <c>TRY</c>; a missing
    /// data file is what a statement reports however its format file reads.
    /// </summary>
    [TestMethod]
    public void ADeferredStatementsRefusal_EndsTheBatchUncaught()
    {
        var sim = WithFormat("14.0 1 1 SQLFOO 0 10 \"\\n\" 1 id \"\"");
        _ = sim.AssertSqlError("""
            create table u (id int);
            begin try
                bulk insert u from 'd.txt' with (formatfile = 'f.fmt');
            end try
            begin catch
                insert log values ('caught');
            end catch
            insert log values ('after')
            """, 4824);
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
        AreEqual(1, sim.AssertSqlError("bulk insert t from 'nope.txt' with (formatfile = 'f.fmt')", 4860).State);
        AreEqual(4, sim.AssertSqlError("select * from openrowset(bulk 'nope.txt', formatfile = 'f.fmt') x", 4860).State);
    }

    /// <summary>
    /// A refusal joins the batch's binder report and ends it: an error before
    /// it is reported with it, none after it.
    /// </summary>
    [TestMethod]
    public void ARefusal_EndsTheBindersReport()
    {
        var sim = WithFormat("14.0 1 1 SQLFOO 0 10 \"\\n\" 1 id \"\"");
        var ex = sim.AssertSqlError("select nosuch from t; bulk insert t from 'd.txt' with (formatfile = 'f.fmt'); select nosuch from t", 207);
        AreEqual("207,4824", string.Join(",", ex.Errors.Select(error => error.Number)));
        ex = sim.AssertSqlError("bulk insert t from 'd.txt' with (formatfile = 'f.fmt'); select nosuch from t", 4824);
        AreEqual(1, ex.Errors.Count);
    }

    /// <summary>
    /// A procedure's body names the format file as it's created, which fails
    /// on a refusal; one created before the file read meets it compiling at
    /// its first run, which ends the caller's batch unless its <c>TRY</c>
    /// catches it.
    /// </summary>
    [TestMethod]
    public void AProceduresRefusal_FailsItsCreate_OrEndsItsCallersBatch()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["d.txt"] = System.Text.Encoding.UTF8.GetBytes(Data) };
        var sim = new Simulation { OpenBulkFile = path => files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : null };
        _ = sim.ExecuteNonQuery("create table t (id int, name varchar(20)); create table log (step varchar(10))");
        sim.ExecuteBatches("create procedure p as begin insert log values ('in p'); bulk insert t from 'd.txt' with (formatfile = 'f.fmt'); end");
        files["f.fmt"] = System.Text.Encoding.UTF8.GetBytes("14.0 1 1 SQLFOO 0 10 \"\\n\" 1 id \"\"");
        _ = sim.AssertSqlError("create procedure q as bulk insert t from 'd.txt' with (formatfile = 'f.fmt')", 4824);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.procedures where name = 'q'"));

        _ = sim.AssertSqlError("exec p; insert log values ('after')", 4824);
        AreEqual("", sim.ExecuteScalar("select isnull(string_agg(step, ','), '') from log"));
        AreEqual("caught 4824", sim.ExecuteScalar("begin try exec p; end try begin catch select concat('caught ', error_number()); end catch"));
    }
}
