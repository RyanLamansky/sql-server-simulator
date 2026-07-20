using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the placeholder security / FILESTREAM scalars —
/// <c>CERTENCODED</c>, <c>CERTPRIVATEKEY</c>, and
/// <c>GET_FILESTREAM_TRANSACTION_CONTEXT</c>. The simulator models no
/// certificate store or FILESTREAM storage, so each returns NULL (the answer
/// real SQL Server gives for a nonexistent certificate id / a session with no
/// active FILESTREAM transaction) while still enforcing the argument-count
/// diagnostics. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CertificateAndFilestreamScalarTests
{
    private static object? Scalar(string sql) => new Simulation().ExecuteScalar(sql);

    [TestMethod]
    public void CertEncoded_UnknownCertId_ReturnsNull()
        => AreEqual(1, Scalar("select case when certencoded(9999) is null then 1 else 0 end"));

    [TestMethod]
    public void CertPrivateKey_UnknownCertId_ReturnsNull()
        => AreEqual(1, Scalar("select case when certprivatekey(9999, 'pw') is null then 1 else 0 end"));

    [TestMethod]
    public void CertPrivateKey_WithDecryptionPassword_ReturnsNull()
        => AreEqual(1, Scalar("select case when certprivatekey(9999, 'pw', 'newpw') is null then 1 else 0 end"));

    [TestMethod]
    public void CertEncoded_WrongArgumentCount_RaisesMsg174()
        => new Simulation().AssertSqlError(
            "select certencoded(1, 2)", 174, "The CertEncoded function requires 1 argument(s).");

    [TestMethod]
    public void CertEncoded_ZeroArguments_RaisesMsg174()
        => new Simulation().AssertSqlError(
            "select certencoded()", 174, "The CertEncoded function requires 1 argument(s).");

    [TestMethod]
    public void CertPrivateKey_OneArgument_RaisesMsg189()
        => new Simulation().AssertSqlError(
            "select certprivatekey(1)", 189, "The CertPrivateKey function requires 2 to 3 arguments.");

    [TestMethod]
    public void CertPrivateKey_FourArguments_RaisesMsg189()
        => new Simulation().AssertSqlError(
            "select certprivatekey(1, 'a', 'b', 'c')", 189, "The CertPrivateKey function requires 2 to 3 arguments.");

    [TestMethod]
    public void GetFilestreamTransactionContext_ReturnsNull()
        => AreEqual(1, Scalar("select case when get_filestream_transaction_context() is null then 1 else 0 end"));

    [TestMethod]
    public void GetFilestreamTransactionContext_WithArgument_RaisesMsg174()
        => new Simulation().AssertSqlError(
            "select get_filestream_transaction_context(1)", 174,
            "The get_filestream_transaction_context function requires 0 argument(s).");
}
