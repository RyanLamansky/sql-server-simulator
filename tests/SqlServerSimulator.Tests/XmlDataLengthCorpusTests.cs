using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>DATALENGTH</c> over <c>xml</c> reports the size of real's parsed binary
/// form, not of the text. The rules live in <c>Storage/XmlBinarySize.cs</c>;
/// this is the corpus that keeps them honest — every expected value below was
/// read from SQL Server 2025, and the set deliberately includes shapes the
/// rules were <em>not</em> derived from (nested and re-pointed namespaces, a
/// prefix declared on a descendant, mixed default and prefixed bindings) plus
/// the boundaries where the length prefix widens.
/// </summary>
[TestClass]
public sealed class XmlDataLengthCorpusTests
{
    [TestMethod]
    [DataRow("<a/>", 16)]
    [DataRow("<a></a>", 16)]
    [DataRow("<a>x</a>", 20)]
    [DataRow("<a>hello</a>", 28)]
    [DataRow("<root><a/><b/></root>", 44)]
    [DataRow("<root attr=\"1\"/>", 43)]
    [DataRow("<abcdefgh/>", 30)]
    [DataRow("<a>12345678</a>", 34)]
    [DataRow("<r><a/><b/><c/><d/></r>", 60)]
    [DataRow("<r><a/><a/><a/><a/></r>", 36)]
    [DataRow("<r><aaaaaaaa/><aaaaaaaa/></r>", 44)]
    [DataRow("<a><b><c><d/></c></b></a>", 49)]
    [DataRow("<a x=\"1\" y=\"2\" z=\"3\" w=\"4\"/>", 73)]
    [DataRow("<a a=\"1\"/>", 23)]
    [DataRow("<r><a/><b a=\"1\"/></r>", 45)]
    [DataRow("<r><a x=\"1\"/><b x=\"2\"/></r>", 60)]
    [DataRow("<a x=\"\"/>", 29)]
    [DataRow("<a><!--c--></a>", 20)]
    [DataRow("<a><?pi v?></a>", 27)]
    [DataRow("<a><![CDATA[hello]]></a>", 28)]
    [DataRow("<a>ab<b/>cd</a>", 39)]
    [DataRow("<root><a>hello</a><b attr=\"1\">world</b></root>", 89)]
    [DataRow("<a xmlns=\"u\"/>", 43)]
    [DataRow("<a xmlns=\"uuuu\"/>", 55)]
    [DataRow("<a xmlns:p=\"u\"/>", 43)]
    [DataRow("<a xmlns:ppp=\"u\"/>", 47)]
    [DataRow("<p:a xmlns:p=\"u\"/>", 51)]
    [DataRow("<ppp:a xmlns:ppp=\"u\"/>", 59)]
    [DataRow("<p:aaa xmlns:p=\"u\"/>", 55)]
    [DataRow("<a xmlns:p=\"u\" xmlns:q=\"v\"/>", 69)]
    [DataRow("<a xmlns:p=\"u\" p:x=\"1\"/>", 65)]
    [DataRow("<a xmlns:p=\"u\"><p:b/><p:c/></a>", 73)]
    [DataRow("<a xmlns:p=\"u\"><p:b/><b/></a>", 69)]
    [DataRow("<a xmlns:p=\"u\"><b/><p:b/></a>", 69)]
    [DataRow("<a xmlns:p=\"u\"><b/><b/></a>", 57)]
    [DataRow("<a xmlns:p=\"u\"><p:b/><p:b/></a>", 65)]
    [DataRow("<a xmlns=\"u\"><b/><c/></a>", 65)]
    [DataRow("<a xmlns=\"u\"><b xmlns=\"v\"/></a>", 65)]
    [DataRow("<p:a xmlns:p=\"u\"><q:b xmlns:q=\"v\"/></p:a>", 97)]
    [DataRow("<a xmlns:p=\"urn:long:namespace:name\"><p:b p:c=\"1\"/></a>", 165)]
    [DataRow("<a xmlns=\"u\" x=\"1\"><b>t</b></a>", 72)]
    [DataRow("<p:a xmlns:p=\"u\" xmlns=\"v\"><b/><p:c/></p:a>", 99)]
    [DataRow("<a><b xmlns:p=\"u\"><p:c/></b><p:d xmlns:p=\"u\"/></a>", 91)]
    [DataRow("<a>  </a>", 16)]
    [DataRow("<a> t </a>", 24)]
    [DataRow("<a>é</a>", 20)]
    [DataRow("<a>😀</a>", 22)]
    [DataRow("<a b=\"é\"/>", 31)]
    [DataRow("<root><item id=\"1\"><name>x</name></item><item id=\"2\"><name>y</name></item></root>", 94)]
    public void MatchesTheReferenceServer(string xml, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select datalength(cast(N'{xml.Replace("'", "''")}' as xml))"));

    [TestMethod]
    [DataRow(126, 281)]
    [DataRow(127, 283)]
    [DataRow(128, 286)]   // the length prefix widens to two bytes here
    [DataRow(129, 288)]
    public void AttributeValueLengthPrefixWidensAt128(int length, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            $"select datalength(cast('<a x=\"' + replicate('v', {length}) + '\"/>' as xml))"));

    [TestMethod]
    [DataRow(126, 270)]
    [DataRow(127, 272)]
    [DataRow(128, 275)]
    [DataRow(129, 277)]
    public void TextLengthPrefixWidensAt128(int length, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            $"select datalength(cast('<a>' + replicate('t', {length}) + '</a>' as xml))"));

    [TestMethod]
    [DataRow(16383, 32785)]
    [DataRow(16384, 32788)]   // and again here — it is a 7-bit varint
    public void TextLengthPrefixWidensAgainAt16384(int length, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            declare @t varchar(max) = replicate(cast('t' as varchar(max)), {length});
            select datalength(cast('<a>' + @t + '</a>' as xml))
            """));

    [TestMethod]
    public void IdenticalParsesReportIdenticalSizes()
    {
        // The measure is of the parsed form, so spelling doesn't reach it.
        var sim = new Simulation();
        AreEqual(
            sim.ExecuteScalar("select datalength(cast('<a/>' as xml))"),
            sim.ExecuteScalar("select datalength(cast('<a></a>' as xml))"));
        AreEqual(
            sim.ExecuteScalar("select datalength(cast('<a>hello</a>' as xml))"),
            sim.ExecuteScalar("select datalength(cast('<a><![CDATA[hello]]></a>' as xml))"));
    }

    [TestMethod]
    public void TextIsCountedInUtf16CodeUnits()
    {
        // An astral character is two units and costs as two.
        var sim = new Simulation();
        AreEqual(
            sim.ExecuteScalar("select datalength(cast(N'<a>ab</a>' as xml))"),
            sim.ExecuteScalar("select datalength(cast(N'<a>\U0001F600</a>' as xml))"));
    }
}
