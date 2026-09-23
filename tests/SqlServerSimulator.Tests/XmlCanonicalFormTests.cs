using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A value converted to <c>xml</c> is stored in the canonical form real
/// serializes it as, so reading it back as text — or through the wire —
/// answers what real answers. Every expectation here was compared against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlCanonicalFormTests
{
    [TestMethod]
    [DataRow("'<a></a>'", "<a/>")]
    [DataRow("'<a b=''x''/>'", "<a b=\"x\"/>")]
    [DataRow("'<a>&#65;&lt;&gt;&amp;&quot;&apos;</a>'", "<a>A&lt;&gt;&amp;\"'</a>")]
    [DataRow("'<a b=''&lt;&gt;&amp;&quot;&apos;&#9;&#10;&#13;''/>'", "<a b=\"&lt;&gt;&amp;&quot;'&#x09;&#x0A;&#x0D;\"/>")]
    [DataRow("'<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>'", "<a/>")]
    [DataRow("'<a>  <b/>  </a>'", "<a><b/></a>")]
    [DataRow("'<a> x </a>'", "<a> x </a>")]
    [DataRow("'<![CDATA[<x>]]>'", "&lt;x&gt;")]
    [DataRow("'<a  b = \"1\"   c=''2''/>'", "<a b=\"1\" c=\"2\"/>")]
    [DataRow("'<!--c--><?p d?>'", "<!--c--><?p d?>")]
    [DataRow("'<p:a xmlns:p=\"u\" p:x=\"1\"/>'", "<p:a xmlns:p=\"u\" p:x=\"1\"/>")]
    [DataRow("'<a>x</a >'", "<a>x</a>")]
    [DataRow("'<a>' + char(13) + char(10) + 'b' + char(13) + 'c</a>'", "<a>\nb\nc</a>")]
    [DataRow("'<a b=''x' + char(9) + 'y''/>'", "<a b=\"x y\"/>")]
    [DataRow("'<a b=''x' + char(13) + char(10) + 'y''/>'", "<a b=\"x y\"/>")]
    [DataRow("'  <a/>  '", "<a/>")]
    [DataRow("'x <a/> y'", "x <a/> y")]
    [DataRow("'<a>&#xD;</a>'", "<a>&#x0D;</a>")]
    [DataRow("'<a b=\"1\" xmlns:p=\"u\" c=\"2\"/>'", "<a xmlns:p=\"u\" b=\"1\" c=\"2\"/>")]
    [DataRow("'<a xmlns=\"u\"><b xmlns=\"u\"/></a>'", "<a xmlns=\"u\"><b xmlns=\"u\"/></a>")]
    [DataRow("'<a><![CDATA[ ]]></a>'", "<a>&#x20;</a>")]
    [DataRow("'<a>x<![CDATA[y]]>z</a>'", "<a>xyz</a>")]
    [DataRow("'<a>&#9;&#10;&#13;&#x20;</a>'", "<a>\t\n&#x0D; </a>")]
    [DataRow("'<a>&#9;</a>'", "<a>&#x09;</a>")]
    [DataRow("'<?p  d  ?>'", "<?p d  ?>")]
    [DataRow("'<?p?>'", "<?p ?>")]
    [DataRow("'<a b=''&#x20;''/>'", "<a b=\" \"/>")]
    [DataRow("'<a>&#x1F600;</a>'", "<a>\U0001F600</a>")]
    [DataRow("'<a>' + char(10) + '  x' + char(10) + '</a>'", "<a>\n  x\n</a>")]
    [DataRow("'<a><b>  </b></a>'", "<a><b/></a>")]
    [DataRow("'<a xml:space=''preserve''>  </a>'", "<a xml:space=\"preserve\"> &#x20;</a>")]
    [DataRow("'<a xml:space=''preserve''><b/>  </a>'", "<a xml:space=\"preserve\"><b/> &#x20;</a>")]
    [DataRow("'<a>]]&gt;&gt;</a>'", "<a>]]&gt;&gt;</a>")]
    [DataRow("'<a b=''>''/>'", "<a b=\"&gt;\"/>")]
    public void Conversion_StoresTheCanonicalForm(string source, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast(cast({source} as xml) as nvarchar(max))"));

    /// <summary>
    /// CONVERT style 1 keeps whitespace-only text, writing its last character
    /// as a reference so the text survives being parsed again.
    /// </summary>
    [TestMethod]
    [DataRow("'<a> </a>'", "<a>&#x20;</a>")]
    [DataRow("'<a>' + char(9) + '</a>'", "<a>&#x09;</a>")]
    [DataRow("'<a>' + char(10) + '</a>'", "<a>&#x0A;</a>")]
    [DataRow("'<a>   </a>'", "<a>  &#x20;</a>")]
    [DataRow("'<a> x </a>'", "<a> x </a>")]
    [DataRow("'<a>  <b/>  </a>'", "<a> &#x20;<b/> &#x20;</a>")]
    [DataRow("'<a> ' + char(9) + char(10) + ' </a>'", "<a> \t\n&#x20;</a>")]
    [DataRow("'  <a/>  '", " &#x20;<a/> &#x20;")]
    [DataRow("'<a>' + char(13) + char(10) + '</a>'", "<a>&#x0A;</a>")]
    public void ConvertStyle1_KeepsWhitespace(string source, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast(convert(xml, {source}, 1) as nvarchar(max))"));

    /// <summary>
    /// A binary source is read in the encoding its bytes announce — a
    /// byte-order mark, an unmarked UTF-16 <c>&lt;</c>, or the declaration —
    /// and as UTF-8 otherwise.
    /// </summary>
    [TestMethod]
    [DataRow("0xEFBBBF3C612F3E", "<a/>")]
    [DataRow("0x3C613EC3A93C2F613E", "<a>\u00E9</a>")]
    [DataRow("0xFFFE3C0061002F003E00", "<a/>")]
    [DataRow("0x3C0061002F003E00", "<a/>")]
    [DataRow("0xFEFF003C0061002F003E", "<a/>")]
    [DataRow("0x003C0061002F003E", "<a/>")]
    [DataRow("0x3C3F786D6C2076657273696F6E3D22312E302220656E636F64696E673D2277696E646F77732D31323532223F3E3C613EE93C2F613E", "<a>\u00E9</a>")]
    public void BinarySource_DecodesByItsEncoding(string bytes, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast(cast({bytes} as xml) as nvarchar(max))"));

    /// <summary>A byte the encoding can't read is the illegal character real reports there.</summary>
    [TestMethod]
    public void BinarySource_InvalidUtf8_RaisesMsg9420()
        => new Simulation().AssertSqlError("select cast(0x3C613EE93C2F613E as xml)", 9420, "XML parsing: line 1, character 4, illegal xml character");

    /// <summary>A column or variable stores the same canonical text.</summary>
    [TestMethod]
    [DataRow("create table t (x xml); insert t values ('<a  b=''1''></a>'); select cast(x as nvarchar(max)) from t")]
    [DataRow("declare @x xml = '<a  b=''1''></a>'; select cast(@x as nvarchar(max))")]
    public void StoredValue_IsCanonical(string sql)
        => AreEqual("<a b=\"1\"/>", new Simulation().ExecuteScalar(sql));

    /// <summary>
    /// A kept declaration naming an 8-bit encoding would reach the client as
    /// UTF-16 text claiming that encoding, which SqlClient refuses to read;
    /// the canonical form drops it.
    /// </summary>
    [TestMethod]
    public void Declaration_IsDropped()
        => AreEqual("<a/>", new Simulation().ExecuteScalar("select cast(cast('<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>' as xml) as nvarchar(max))"));
}
