using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Runtime-side coverage for the value-width literal typing: the projected
/// column widths (asserted over the wire in <c>StringLiteralWidthWireTests</c>)
/// must never fall below the value a growable string scalar actually produces —
/// static / runtime parity. Also covers the binary length-variance comparison
/// that two exact-width binary literals now exercise (<c>0x01</c> is
/// <c>varbinary(1)</c>, <c>0x0100</c> is <c>varbinary(2)</c>).
/// </summary>
[TestClass]
public sealed class StringWidthAlgebraTests
{
    // REPLACE / STUFF / REPLICATE / SPACE grow past the operand width; the
    // runtime value must materialize in full despite the tighter projected type.
    [TestMethod]
    [DataRow("select len(replace('aaa', 'a', 'XY'))", 6)]
    [DataRow("select len(stuff('abcdef', 2, 1, 'XYZW'))", 9)]
    [DataRow("select len(replicate('ab', 5))", 10)]
    [DataRow("select datalength(space(7))", 7)]       // LEN strips trailing spaces; DATALENGTH counts them
    [DataRow("select len(left('abcdef', 20))", 6)]
    [DataRow("select len(substring('abcdef', 2, 3))", 3)]
    public void GrowableScalars_MaterializeFullValue(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    // Concatenation of two exact-width literals produces the full joined value.
    [TestMethod]
    public void Concatenation_ProducesFullValue()
        => AreEqual("abcde", new Simulation().ExecuteScalar("select 'ab' + 'cde'"));

    // CONCAT sums its arguments' declared widths, so an argument carrying no
    // declared width (the container form REPLACE / TRANSLATE project) leaves
    // nothing to sum and the whole result falls back to the container — the
    // value materializes in full either way, and the declared width the wire
    // then advertises is asserted in StringLiteralWidthWireTests.
    [TestMethod]
    [DataRow("select concat(replace('aaa', 'a', 'XY'), 'x')", "XYXYXYx")]
    [DataRow("select concat_ws('-', replace('aaa', 'a', 'XY'), 'x')", "XYXYXY-x")]
    public void ConcatOverContainerWidthArgument_MaterializesFullValue(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    // A CASE that could yield either arm materializes the wider arm intact.
    [TestMethod]
    public void CaseUnification_MaterializesWiderArm()
        => AreEqual("wxyz", new Simulation().ExecuteScalar("select case when 1=0 then 'ab' else 'wxyz' end"));

    // Binary literals of differing exact widths compare by byte span, not by
    // declared-length identity.
    [TestMethod]
    [DataRow("0x01 = 0x01", 1)]
    [DataRow("0x0001 = 0x0001", 1)]
    [DataRow("0x01 = 0x0001", 0)]                   // different bytes → not equal
    [DataRow("0x0001 = cast(0x0001 as varbinary(8))", 1)]
    [DataRow("0x01 < 0x0100", 1)]
    [DataRow("0x0100 > 0x01", 1)]
    public void BinaryLengthVariance_ComparesByBytes(string condition, int expectedRows)
        => AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());
}
