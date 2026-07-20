using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>TEXTPTR(column)</c> and
/// <c>TEXTVALID('table.column', ptr)</c> over the deprecated
/// <c>text</c> / <c>ntext</c> / <c>image</c> types. The simulator fabricates a
/// 16-byte pointer carrying column identity so <c>TEXTVALID</c> can accept a
/// pointer against its source column and reject it against any other column or
/// against arbitrary bytes. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TextPointerScalarTests
{
    private const string Table = """
        create table dbo.tp (
            id int not null,
            t text null, nt ntext null, im image null,
            v varchar(max) null, plain int null);
        insert dbo.tp values
            (1, 'hello', N'wide', 0x0102, 'vv', 5),
            (2, null, null, null, null, 6)
        """;

    private static Simulation SimWithTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Table);
        return sim;
    }

    [TestMethod]
    public void TextPtr_NonNullTextCell_IsSixteenByteVarbinary()
        => AreEqual(16, SimWithTable().ExecuteScalar("select datalength(textptr(t)) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextPtr_NtextAndImage_AreSixteenBytes()
    {
        var sim = SimWithTable();
        AreEqual(16, sim.ExecuteScalar("select datalength(textptr(nt)) from dbo.tp where id = 1"));
        AreEqual(16, sim.ExecuteScalar("select datalength(textptr(im)) from dbo.tp where id = 1"));
    }

    [TestMethod]
    public void TextPtr_NullCell_ReturnsNull()
        => AreEqual(1, SimWithTable().ExecuteScalar(
            "select case when textptr(t) is null then 1 else 0 end from dbo.tp where id = 2"));

    [TestMethod]
    public void TextPtr_NonLobColumn_RaisesMsg8116()
        => SimWithTable().AssertSqlError(
            "select textptr(plain) from dbo.tp where id = 1", 8116,
            "Argument data type int is invalid for argument 1 of textptr function.");

    [TestMethod]
    public void TextPtr_VarcharMaxColumn_RaisesMsg8116()
        => SimWithTable().AssertSqlError("select textptr(v) from dbo.tp where id = 1", 8116);

    [TestMethod]
    public void TextPtr_NonColumnExpression_RaisesMsg280()
        => SimWithTable().AssertSqlError(
            "select textptr(cast('x' as text)) from dbo.tp where id = 1", 280,
            "Only base table columns are allowed in the TEXTPTR function.");

    [TestMethod]
    public void TextPtr_ComputedExpression_RaisesMsg280()
        => SimWithTable().AssertSqlError("select textptr(substring(t, 1, 1)) from dbo.tp where id = 1", 280);

    [TestMethod]
    public void TextValid_PointerFromSameColumn_ReturnsOne()
        => AreEqual(1, SimWithTable().ExecuteScalar("select textvalid('dbo.tp.t', textptr(t)) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_PointerOfNullCell_ReturnsZero()
        => AreEqual(0, SimWithTable().ExecuteScalar("select textvalid('dbo.tp.t', textptr(t)) from dbo.tp where id = 2"));

    [TestMethod]
    public void TextValid_PointerFromDifferentColumn_ReturnsZero()
        => AreEqual(0, SimWithTable().ExecuteScalar("select textvalid('dbo.tp.im', textptr(t)) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_NonLobColumnSegment_ReturnsZero()
        => AreEqual(0, SimWithTable().ExecuteScalar("select textvalid('dbo.tp.plain', textptr(t)) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_GarbagePointer_ReturnsZero()
        => AreEqual(0, SimWithTable().ExecuteScalar(
            "select textvalid('dbo.tp.t', 0x00000000000000000000000000000000) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_NullPointer_ReturnsZero()
        => AreEqual(0, SimWithTable().ExecuteScalar("select textvalid('dbo.tp.t', null) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_SinglePartName_ReturnsZero()
        => AreEqual(0, SimWithTable().ExecuteScalar("select textvalid('justacol', textptr(t)) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_ColumnSegmentIsCaseInsensitive()
        => AreEqual(1, SimWithTable().ExecuteScalar("select textvalid('dbo.tp.T', textptr(t)) from dbo.tp where id = 1"));

    [TestMethod]
    public void TextValid_FourPartName_Resolves()
        => AreEqual(1, SimWithTable().ExecuteScalar("select textvalid('simulated.dbo.tp.t', textptr(t)) from dbo.tp where id = 1"));
}
