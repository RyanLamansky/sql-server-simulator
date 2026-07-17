using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the Query Store catalog views —
/// <c>sys.database_query_store_options</c> (per-database: one OFF row for a
/// user database, zero rows for a system database) and
/// <c>sys.query_store_runtime_stats</c> (always empty) — plus the SSMS Query
/// Store probe batch that reads them and the dispatch-loop non-progress guard
/// that batch's failure mode exposed. Probed against SQL Server 2025
/// (2026-07-15).
/// </summary>
[TestClass]
public sealed class QueryStoreCatalogViewTests
{
    /// <summary>
    /// The exact SSMS Query Store probe batch: OBJECT_ID gates the block,
    /// the first SELECT reads actual_state (0, Query Store off), the nested
    /// IF EXISTS over the always-empty runtime-stats view falls to its ELSE.
    /// Two result sets, 0 then 0 — probe-confirmed against a QS-off user
    /// database on real SQL Server.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void SsmsQueryStoreProbeBatch_ReturnsZeroThenZero()
    {
        using var reader = new Simulation().ExecuteReader(
            "IF OBJECT_ID (N'[sys].[database_query_store_options]') IS NOT NULL " +
            "BEGIN " +
            "SELECT ISNULL(actual_state, -2) FROM sys.database_query_store_options; " +
            "IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats) SELECT 1 ELSE SELECT 0; " +
            "END");

        IsTrue(reader.Read());
        AreEqual(0, Convert.ToInt32(reader.GetValue(0)));
        IsFalse(reader.Read());

        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, Convert.ToInt32(reader.GetValue(0)));
        IsFalse(reader.Read());

        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void DatabaseQueryStoreOptions_ObjectId_ResolvesToInt()
        => IsInstanceOfType<int>(new Simulation().ExecuteScalar(
            "SELECT OBJECT_ID(N'[sys].[database_query_store_options]')"));

    [TestMethod]
    public void DatabaseQueryStoreOptions_UserDatabase_ReturnsSingleOffRow()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT actual_state, desired_state_desc, query_capture_mode_desc FROM sys.database_query_store_options");

        IsTrue(reader.Read());
        AreEqual(0, Convert.ToInt32(reader.GetValue(0)));
        AreEqual("OFF", reader.GetString(1));
        // AUTO, not 2025's fresh-database CUSTOM default: DacFx's bacpac
        // model schema can't express CUSTOM (its import rejects capture
        // mode 4), so the OFF row reports the round-trippable pre-CUSTOM
        // shape the reference AW/WWI databases show.
        AreEqual("AUTO", reader.GetString(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void DatabaseQueryStoreOptions_SystemDatabase_ReturnsNoRows()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT actual_state FROM master.sys.database_query_store_options");

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void QueryStoreRuntimeStats_IsAlwaysEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar("SELECT COUNT(*) FROM sys.query_store_runtime_stats"));

    /// <summary>
    /// Non-progress-guard regression. A TRY-caught Msg 208 puts the batch in
    /// skip mode; the skipped IF's deferred-name recovery abandons the IF
    /// mid-parse, orphaning <c>SELECT 1 ELSE SELECT 2</c>; the bare ELSE then
    /// raises with the cursor already on a statement boundary. Pre-guard the
    /// recovery scan advanced zero tokens and the dispatch loop never
    /// terminated (the SSMS Query Store probe crash of 2026-07-15). The guard
    /// bounds it: the batch completes promptly and the CATCH returns the first
    /// error, Msg 208.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void SkipModeOrphanedElse_DoesNotHang_CatchReturnsFirstError()
        => AreEqual(208, new Simulation().ExecuteScalar(
            "BEGIN TRY " +
            "SELECT * FROM nosuchtable1 " +
            "IF EXISTS (SELECT 1 FROM nosuchtable2) SELECT 1 ELSE SELECT 2 " +
            "END TRY BEGIN CATCH SELECT ERROR_NUMBER() END CATCH"));
}
