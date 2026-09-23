using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Converting a value to <c>xml</c> parses it: a malformed one raises real's
/// own XML parsing error, positioned where real's parser stopped, and behaves
/// as an error under <c>SET XACT_ABORT ON</c> does whatever the option says.
/// Every expectation here was compared against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlWellFormednessTests
{
    private static void AssertXmlError(string sql, int number, int line, int character, string detail)
    {
        var ex = new Simulation().AssertSqlError(sql, number);
        AreEqual($"XML parsing: line {line}, character {character}, {detail}", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    [TestMethod]
    [DataRow("<a>", 9400, 1, 3, "unexpected end of input")]
    [DataRow("<a><b></a>", 9436, 1, 10, "end tag does not match start tag")]
    [DataRow("<a x=1/>", 9413, 1, 6, "A string literal was expected")]
    [DataRow("<a>&foo;</a>", 9448, 1, 8, "well formed check: undeclared entity")]
    [DataRow("</a>", 9455, 1, 2, "illegal qualified name character")]
    [DataRow("  </a>", 9455, 1, 4, "illegal qualified name character")]
    [DataRow("text</a>", 9436, 1, 8, "end tag does not match start tag")]
    [DataRow("<a><![CDATA[x</a>", 9423, 1, 17, "incorrect CDATA section syntax")]
    [DataRow("<!-- x", 9424, 1, 6, "incorrect comment syntax")]
    [DataRow("<a/><?xml version=\"1.0\"?>", 9438, 1, 10, "text/xmldecl not at the beginning of input")]
    [DataRow(" <?xml version=\"1.0\"?><a/>", 9438, 1, 7, "text/xmldecl not at the beginning of input")]
    [DataRow("<a>&#0;</a>", 9420, 1, 7, "illegal xml character")]
    [DataRow("<a x=\"1\" x=\"2\"/>", 9437, 1, 16, "duplicate attribute")]
    [DataRow("<a x=\"1\"y=\"2\"/>", 9410, 1, 9, "whitespace expected")]
    [DataRow("<a x=\"<\"/>", 9415, 1, 7, "well formed check: no '<' in attribute value")]
    [DataRow("<a>&</a>", 9421, 1, 5, "illegal name character")]
    [DataRow("<a>&amp</a>", 9411, 1, 8, "semicolon expected")]
    [DataRow("<a>&#x;</a>", 9416, 1, 7, "hexadecimal digit expected")]
    [DataRow("<a>&#a;</a>", 9417, 1, 6, "decimal digit expected")]
    [DataRow("<a>]]></a>", 9454, 1, 4, "no ']]>' in element content")]
    [DataRow("<a><!-- a -- b --></a>", 9412, 1, 13, "'>' expected")]
    [DataRow("<a></a", 9412, 1, 6, "'>' expected")]
    [DataRow("<a x", 9414, 1, 4, "equal expected")]
    [DataRow("<a><!DOCTYPE a></a>", 9422, 1, 6, "incorrect document syntax")]
    [DataRow("<?pi x", 9451, 1, 6, "incorrect processing instruction syntax")]
    [DataRow("<?XML version=\"1.0\"?><a/>", 9439, 1, 6, "namespaces beginning with \"xml\" are reserved")]
    [DataRow("<?xml version=\"2.0\"?><a/>", 9441, 1, 19, "incorrect xml declaration syntax")]
    [DataRow("<?xml?><a/>", 9441, 1, 7, "incorrect xml declaration syntax")]
    [DataRow("<?xml version=\"1.0\" encoding=\"bogus\"?><a/>", 9401, 1, 38, "unrecognized encoding")]
    [DataRow("<?xml version=\"1.0\" encoding=\"utf-16\"?><a/>", 9402, 1, 39, "unable to switch the encoding")]
    [DataRow("<a:b:c xmlns:a=\"u\"/>", 9456, 1, 5, "multiple colons in qualified name")]
    [DataRow("<a xmlns=\"u\" xmlns=\"v\"/>", 9458, 1, 24, "redeclared prefix")]
    [DataRow("<a b:c=\"1\" b:c=\"2\"/>", 9459, 1, 20, "undeclared prefix")]
    [DataRow("<a xmlns:p=\"u\" xmlns:q=\"u\" p:x=\"1\" q:x=\"2\"/>", 9437, 1, 44, "duplicate attribute")]
    [DataRow("<a xmlns:p=\"\"/>", 9460, 1, 15, "non default namespace with empty uri")]
    [DataRow("<a xmlns:xmlns=\"u\"/>", 9465, 1, 20, "XML namespace prefix 'xmlns' is reserved for use by XML.")]
    public void MalformedValue_RaisesRealsParseError(string xml, int number, int line, int character, string detail)
        => AssertXmlError($"select cast('{xml.Replace("'", "''", StringComparison.Ordinal)}' as xml)", number, line, character, detail);

    /// <summary>
    /// Lines break at LF, CR LF and a lone CR alike, and a character is
    /// counted once even when it takes two UTF-16 units.
    /// </summary>
    [TestMethod]
    [DataRow("'<a>' + char(10) + '<b>' + char(10) + '</a>'", 9436, 3, 4, "end tag does not match start tag")]
    [DataRow("'<a>' + char(13) + char(10) + '  <b x=1/>'", 9413, 2, 8, "A string literal was expected")]
    [DataRow("'<a>' + char(13) + '<b x=1/>'", 9413, 2, 6, "A string literal was expected")]
    [DataRow("N'<a>' + nchar(55357) + nchar(56832) + N'</a'", 9412, 1, 7, "'>' expected")]
    [DataRow("N'<a>' + nchar(55296) + N'</a>'", 9420, 1, 4, "illegal xml character")]
    public void Position_CountsLinesAndCharacters(string expression, int number, int line, int character, string detail)
        => AssertXmlError($"select cast({expression} as xml)", number, line, character, detail);

    /// <summary>
    /// An instance is content rather than a document, and the declaration
    /// may name any encoding the source's width can switch to.
    /// </summary>
    [TestMethod]
    [DataRow("''")]
    [DataRow("'   '")]
    [DataRow("'x<a/>'")]
    [DataRow("'<a/><b/>tail'")]
    [DataRow("'<![CDATA[x]]>'")]
    [DataRow("'<a>&#x10FFFF;&lt;&#65;</a>'")]
    [DataRow("'<a></a >'")]
    [DataRow("'<?xml version=\"1.0\" encoding=\"windows-1252\"?><a/>'")]
    [DataRow("N'<?xml version=\"1.0\" encoding=\"utf-16\"?><a/>'")]
    [DataRow("'<?xml-stylesheet href=\"x\"?><a xml:lang=\"en\"/>'")]
    [DataRow("'<p:a xmlns:p=\"u\"><p:b p:c=\"1\"/></p:a>'")]
    public void WellFormedValue_Converts(string expression)
        => IsNotNull(new Simulation().ExecuteScalar($"select cast({expression} as xml)"));

    [TestMethod]
    public void NationalSourceCantSwitchToAnEightBitEncoding()
        => AssertXmlError("select cast(N'<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>' as xml)", 9402, 1, 38, "unable to switch the encoding");

    [TestMethod]
    public void DocumentType_RaisesMsg6359()
        => new Simulation().AssertSqlError(
            "select cast('<!DOCTYPE a [<!ELEMENT a ANY>]><a/>' as xml)",
            6359,
            "Parsing XML with internal subset DTDs not allowed. Use CONVERT with style option 2 to enable limited internal subset DTD support.");

    /// <summary>Every conversion to xml parses, not just CAST.</summary>
    [TestMethod]
    [DataRow("declare @x xml = '<a>'")]
    [DataRow("declare @x xml; set @x = '<a>'")]
    [DataRow("create table t (x xml); insert t values ('<a>')")]
    [DataRow("select convert(xml, '<a>', 1)")]
    [DataRow("select cast(0x3C613E as xml)")]
    public void EveryConversionParses(string sql)
        => AssertXmlError(sql, 9400, 1, 3, "unexpected end of input");

    [TestMethod]
    public void TryCast_AnswersNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select try_cast('<a>' as xml)"));

    /// <summary>
    /// A CONVERT style reads as whitespace and DTD handling for an xml
    /// target, not as a binary-to-text layout.
    /// </summary>
    [TestMethod]
    public void ConvertWithStyle_BinarySourceIsParsedNotRenderedAsHex()
        => AreEqual("<a/>", new Simulation().ExecuteScalar("select cast(convert(xml, 0x3C612F3E, 1) as nvarchar(max))"));

    /// <summary>
    /// Uncaught, the error ends the batch and rolls the transaction back
    /// with XACT_ABORT off.
    /// </summary>
    [TestMethod]
    public void Uncaught_EndsTheBatchAndRollsBack()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (i int)").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert t values (1); select cast('<a>' as xml); insert t values (2)").ExecuteNonQuery());
        using var reader = connection.CreateCommand("select @@trancount, (select count(*) from t)").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
    }

    [TestMethod]
    public void Caught_DoomsTheTransaction()
    {
        using var reader = new Simulation().ExecuteReader("""
            begin tran;
            begin try select cast('<a>' as xml) end try
            begin catch select xact_state(), @@trancount, error_number() end catch
            rollback
            """);
        while (reader.FieldCount != 3)
            IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual((short)-1, reader.GetInt16(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(9400, reader.GetInt32(2));
    }
}
