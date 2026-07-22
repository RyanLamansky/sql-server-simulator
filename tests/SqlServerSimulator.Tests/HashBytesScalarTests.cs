using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>HASHBYTES(algorithm, input)</c>. Byte outputs and the
/// accepted-algorithm set are probe-confirmed against SQL Server 2025
/// (2026-07-21): MD5 / MD4 / SHA / SHA1 / SHA2_256 / SHA2_512 hash; the
/// removed MD2 and any unknown name yield a NULL result; a non-character /
/// non-binary input raises Msg 8116; a typed-NULL input yields NULL.
/// </summary>
[TestClass]
public sealed class HashBytesScalarTests
{
    private static string Hex(string sql)
        => Convert.ToHexString((byte[])new Simulation().ExecuteScalar(sql)!);

    [TestMethod]
    public void Sha2_256_MatchesReal()
        => AreEqual("2D711642B726B04401627CA9FBAC32F5C8530FB1903CC4DB02258717921A4881",
            Hex("select hashbytes('SHA2_256', 'x')"));

    [TestMethod]
    public void Md5_MatchesReal()
        => AreEqual("9DD4E461268C8034F5C8564E155C67A6", Hex("select hashbytes('MD5', 'x')"));

    [TestMethod]
    public void Md4_MatchesReal()
        => AreEqual("51B834B7C1EF0B59EA50888FCB39ACE2", Hex("select hashbytes('MD4', 'x')"));

    [TestMethod]
    public void Sha1_MatchesReal()
        => AreEqual("11F6AD8EC52A2984ABAAFD7C3B516503785C2072", Hex("select hashbytes('SHA1', 'x')"));

    [TestMethod]
    public void Sha_AliasesSha1()
        => AreEqual("11F6AD8EC52A2984ABAAFD7C3B516503785C2072", Hex("select hashbytes('SHA', 'x')"));

    [TestMethod]
    public void Sha2_512_MatchesReal()
        => AreEqual(
            "A4ABD4448C49562D828115D13A1FCCEA927F52B4D5459297F8B43E42DA89238BC13626E43DCB38DDB082488927EC904FB42057443983E88585179D50551AFE62",
            Hex("select hashbytes('SHA2_512', 'x')"));

    [TestMethod]
    public void AlgorithmName_IsCaseInsensitive()
        => AreEqual("2D711642B726B04401627CA9FBAC32F5C8530FB1903CC4DB02258717921A4881",
            Hex("select hashbytes('sha2_256', 'x')"));

    /// <summary>
    /// 'x' (0x78) and 0x78 hash identically — varchar goes through CP1252.
    /// </summary>
    [TestMethod]
    public void VarcharInput_HashesAsSingleCp1252Byte()
        => AreEqual(Hex("select hashbytes('SHA2_256', 0x78)"), Hex("select hashbytes('SHA2_256', 'x')"));

    [TestMethod]
    public void EmptyInput_HashesEmptyByteSequence()
        => AreEqual("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            Hex("select hashbytes('SHA2_256', '')"));

    [TestMethod]
    public void RemovedMd2Algorithm_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select hashbytes('MD2', 'x')"));

    [TestMethod]
    public void UnknownAlgorithm_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select hashbytes('BOGUS', 'x')"));

    [TestMethod]
    public void TypedNullInput_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select hashbytes('SHA2_256', cast(null as varchar(10)))"));

    [TestMethod]
    public void IntegerInput_RaisesMsg8116()
        => new Simulation().AssertSqlError("select hashbytes('SHA2_256', cast(5 as int))", 8116);
}
