using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class SessionContextTests
{
    // SESSION_CONTEXT preserves the stored value's base type through the
    // sql_variant result: an int round-trips as int, an nvarchar as nvarchar.
    [TestMethod]
    public void SetAndRead_Named_PreservesIntType()
        => AreEqual(42, ExecuteScalar(
            "exec sp_set_session_context @key = N'TenantId', @value = 42; select session_context(N'TenantId')"));

    [TestMethod]
    public void SetAndRead_Positional()
        => AreEqual("hello", ExecuteScalar(
            "exec sp_set_session_context N'StrKey', N'hello'; select session_context(N'StrKey')"));

    [TestMethod]
    public void MissingKey_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select session_context(N'nope')"));

    // SESSION_CONTEXT projects sql_variant (like real); a stored int surfaces
    // its int inner and compares against an int column by unwrapping.
    [TestMethod]
    public void ReportsSqlVariantMetadata()
    {
        using var reader = new Simulation().ExecuteReader(
            "exec sp_set_session_context N'k', 7; select session_context(N'k')");
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        AreEqual(typeof(object), reader.GetFieldType(0));
        IsTrue(reader.Read());
        _ = IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(7, reader.GetValue(0));
    }

    [TestMethod]
    public void ConnectionProperty_ReportsSqlVariantMetadata()
    {
        using var reader = new Simulation().ExecuteReader("select connectionproperty('net_transport')");
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        IsTrue(reader.Read());
        AreEqual("TCP", reader.GetValue(0));
    }

    [TestMethod]
    public void Key_IsCaseSensitive()
        => IsInstanceOfType<DBNull>(ExecuteScalar(
            "exec sp_set_session_context N'TenantId', 42; select session_context(N'tenantid')"));

    [TestMethod]
    public void ReadOnlyKey_RejectsOverwrite_Msg15664()
    {
        var ex = new Simulation().AssertSqlError(
            "exec sp_set_session_context @key = N'Locked', @value = 1, @read_only = 1;"
            + " exec sp_set_session_context @key = N'Locked', @value = 2",
            15664);
        Assert.Contains("read_only", ex.Message);
    }

    [TestMethod]
    public void NullKey_RaisesMsg225()
        => new Simulation().AssertSqlError("exec sp_set_session_context @key = NULL, @value = 1", 225);

    [TestMethod]
    public void NullKeyArgument_RaisesMsg8116()
        => new Simulation().AssertSqlError("select session_context(NULL)", 8116);

    [TestMethod]
    public void UsableInWherePredicate()
        => AreEqual(1, ExecuteScalar("""
            create table t (id int not null primary key, tenant int not null);
            insert t values (1, 42), (2, 99);
            exec sp_set_session_context N'T', 42;
            select id from t where tenant = session_context(N'T')
            """));

    [TestMethod]
    public void ContextInfo_NullBeforeSet()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select context_info()"));

    [TestMethod]
    public void ContextInfo_PaddedTo128AfterSet()
        => AreEqual(128, ExecuteScalar("set context_info 0x4869; select datalength(context_info())"));

    [TestMethod]
    public void ConnectionProperty_KnownAndUnknown()
    {
        AreEqual("TCP", ExecuteScalar("select connectionproperty('net_transport')"));
        AreEqual("TSQL", ExecuteScalar("select connectionproperty('protocol_type')"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select connectionproperty('bogus')"));
    }

    [TestMethod]
    public void CurrentTransactionId_IsBigint()
        => IsInstanceOfType<long>(ExecuteScalar("select current_transaction_id()"));

    [TestMethod]
    public void CurrentRequestId_IsZero()
        => AreEqual(0, ExecuteScalar("select current_request_id()"));
}
