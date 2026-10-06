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
        AreEqual("Shared memory", reader.GetValue(0));
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

    // A variable's value converts to binary; a NULL or string one is Msg 2743
    // (probed 2026-09-28 against SQL Server 2025).
    [TestMethod]
    [DataRow("declare @b varbinary(128) = 0x0102", "0x0102")]
    [DataRow("declare @b int = 258", "0x00000102")]
    public void ContextInfo_FromVariable(string declaration, string expectedPrefix)
        => AreEqual(expectedPrefix, ExecuteScalar($"{declaration}; set context_info @b; select convert(varchar(12), substring(context_info(), 1, {(expectedPrefix.Length - 2) / 2}), 1)"));

    [TestMethod]
    [DataRow("declare @b varbinary(10)")]
    [DataRow("declare @b varchar(10) = 'ab'")]
    public void ContextInfo_FromNullOrStringVariable_RaisesMsg2743(string declaration)
        => new Simulation().AssertSqlError($"{declaration}; set context_info @b", 2743, "SET CONTEXT_INFO option requires varbinary (128) NOT NULL parameter.");

    [TestMethod]
    public void ConnectionProperty_KnownAndUnknown()
    {
        // An in-process connection answers as real's shared-memory transport.
        AreEqual("Shared memory", ExecuteScalar("select connectionproperty('net_transport')"));
        AreEqual("TSQL", ExecuteScalar("select connectionproperty('protocol_type')"));
        AreEqual("<local machine>|varchar", ExecuteScalar("select concat(cast(connectionproperty('client_net_address') as varchar(50)), '|', cast(sql_variant_property(connectionproperty('client_net_address'), 'BaseType') as varchar(20)))"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select connectionproperty('local_tcp_port')"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select connectionproperty('bogus')"));
    }

    [TestMethod]
    public void CurrentTransactionId_IsBigint()
        => IsInstanceOfType<long>(ExecuteScalar("select current_transaction_id()"));

    /// <summary>
    /// The in-process connection is a MARS one, whose first request real
    /// numbers 2 (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CurrentRequestId_IsTheMarsRequestsOwn()
        => AreEqual(2, ExecuteScalar("select current_request_id()"));

    /// <summary>A string property carries its sys.dm_exec_connections column's declared type (probed 2026-09-26 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("net_transport", 80)]
    [DataRow("auth_scheme", 40)]
    [DataRow("client_net_address", 48)]
    public void ConnectionProperty_StringsCarryTheirDeclaredLength(string property, int maxLength)
        => AreEqual(maxLength, ExecuteScalar($"select cast(sql_variant_property(connectionproperty('{property}'), 'MaxLength') as int)"));

    /// <summary>
    /// Keys compare without case in their first character alone and without
    /// trailing spaces (probed 2026-10-04 against SQL Server 2025): a key set
    /// as <c>Key</c> reads back as <c>key</c> but not <c>KEY</c>.
    /// </summary>
    [TestMethod]
    public void KeyComparison_FirstCharacterIgnoresCase()
    {
        AreEqual("1|1|", ExecuteScalar("exec sp_set_session_context 'Key', 1; select concat(cast(session_context(N'Key') as int), '|', cast(session_context(N'key ') as int), '|', cast(session_context(N'KEY') as int))"));
        AreEqual("1|2", ExecuteScalar("exec sp_set_session_context 'key', 1; exec sp_set_session_context 'KEY', 2; select concat(cast(session_context(N'Key') as int), '|', cast(session_context(N'KEY') as int))"));
    }

    /// <summary>The procedure's own refusals (probed 2026-10-04 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("exec sp_set_session_context 'k'", 16903)]
    [DataRow("exec sp_set_session_context 'k', 1, 0, 5", 16914)]
    [DataRow("exec sp_set_session_context 5, 1", 225)]
    [DataRow("exec sp_set_session_context '', 1", 15666)]
    [DataRow("declare @k nvarchar(200) = replicate('k', 129); exec sp_set_session_context @k, 1", 15666)]
    [DataRow("declare @v nvarchar(max) = N'abc'; exec sp_set_session_context 'k', @v", 15600)]
    [DataRow("declare @v xml = '<a/>'; exec sp_set_session_context 'k', @v", 15600)]
    [DataRow("exec sp_set_session_context 'k', 1, null", 15600)]
    [DataRow("exec sp_set_session_context 'k', 1, 'x'", 15600)]
    public void Refusals(string sql, int number)
    {
        var error = new Simulation().AssertSqlError(sql, number);
        AreEqual((1, "sp_set_session_context"), (error.Errors[0].LineNumber, error.Errors[0].Procedure));
    }

    /// <summary>A name that is no parameter is ignored (probed 2026-10-04 against SQL Server 2025).</summary>
    [TestMethod]
    public void UnknownName_Ignored()
        => AreEqual(DBNull.Value, ExecuteScalar("exec sp_set_session_context @key = 'k', @val = 1; select session_context(N'k')"));
}
