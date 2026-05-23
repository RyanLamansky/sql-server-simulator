using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>CHECKSUM</c>, <c>BINARY_CHECKSUM</c>, and
/// <c>MIN_ACTIVE_ROWVERSION</c>. The simulator's CHECKSUM uses a
/// 32-bit FNV-1a fold over the value representation rather than
/// SQL Server's unpublished CRC; only the semantic guarantee
/// ("equal inputs → equal outputs") matches, not the exact bit
/// pattern — documented quirk.
/// </summary>
[TestClass]
public sealed class ChecksumAndRowVersionTests
{
    [TestMethod]
    public void Checksum_SameInput_SameOutput()
    {
        var a = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        var b = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        AreEqual(a, b);
    }

    [TestMethod]
    public void Checksum_DifferentInput_DifferentOutput()
    {
        var a = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        var b = (int)new Simulation().ExecuteScalar("select checksum('bar')")!;
        AreNotEqual(a, b);
    }

    [TestMethod]
    public void Checksum_MultipleArgs_Works()
    {
        var result = new Simulation().ExecuteScalar("select checksum(1, 'foo', cast('2024-01-15' as date))");
        IsTrue(result is int);
    }

    [TestMethod]
    public void Checksum_CaseInsensitive_SameAsLower()
    {
        var a = (int)new Simulation().ExecuteScalar("select checksum('FOO')")!;
        var b = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        AreEqual(a, b);
    }

    [TestMethod]
    public void BinaryChecksum_CaseSensitive_DiffersFromCaseChange()
    {
        var a = (int)new Simulation().ExecuteScalar("select binary_checksum('FOO')")!;
        var b = (int)new Simulation().ExecuteScalar("select binary_checksum('foo')")!;
        AreNotEqual(a, b);
    }

    [TestMethod]
    public void MinActiveRowVersion_Returns8Bytes()
    {
        var result = new Simulation().ExecuteScalar("select min_active_rowversion()");
        IsTrue(result is byte[] { Length: 8 });
    }
}
