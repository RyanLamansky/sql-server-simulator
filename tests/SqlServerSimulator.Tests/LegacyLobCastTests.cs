using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Explicit-conversion rules for the legacy LOB types. Real SQL Server refuses
/// <c>CAST</c> / <c>CONVERT</c> from <c>text</c> / <c>ntext</c> to anything
/// outside the string family with Msg 529, even when the payload would parse
/// cleanly — the value's parseability never enters into it. Probe-confirmed
/// against SQL Server 2025 (17.0.1125.2) on 2026-07-24.
/// </summary>
[TestClass]
public sealed class LegacyLobCastTests
{
    private static Simulation WithLobRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (tx text, nt ntext, im image);
            insert t values ('5', '5', 0x05)
            """);
        return simulation;
    }

    [TestMethod]
    [DataRow("cast(tx as int)", "text", "int")]
    [DataRow("cast(tx as bigint)", "text", "bigint")]
    [DataRow("cast(tx as decimal(10,2))", "text", "decimal")]
    [DataRow("cast(tx as float)", "text", "float")]
    [DataRow("cast(tx as money)", "text", "money")]
    [DataRow("cast(tx as bit)", "text", "bit")]
    [DataRow("cast(tx as date)", "text", "date")]
    [DataRow("cast(tx as datetime)", "text", "datetime")]
    [DataRow("cast(tx as uniqueidentifier)", "text", "uniqueidentifier")]
    [DataRow("cast(tx as varbinary(10))", "text", "varbinary")]
    [DataRow("cast(nt as int)", "ntext", "int")]
    [DataRow("cast(nt as date)", "ntext", "date")]
    [DataRow("cast(tx as sql_variant)", "text", "sql_variant")]
    // image has the mirror-image allow-list: binary family only, so xml is
    // reachable from text but not from image.
    [DataRow("cast(im as int)", "image", "int")]
    [DataRow("cast(im as xml)", "image", "xml")]
    [DataRow("cast(im as uniqueidentifier)", "image", "uniqueidentifier")]
    [DataRow("cast(im as varchar(10))", "image", "varchar")]
    public void ExplicitCastOutsideTheSourceFamily_RaisesMsg529(string expression, string source, string target) =>
        WithLobRow().AssertSqlError(
            $"select {expression} from t",
            529,
            $"Explicit conversion from data type {source} to {target} is not allowed.");

    [TestMethod]
    [DataRow("convert(int, tx)")]
    [DataRow("try_cast(tx as int)")]
    [DataRow("try_convert(int, tx)")]
    public void ConvertAndTryForms_AlsoRaiseMsg529(string expression) =>
        // TRY_CAST / TRY_CONVERT do *not* swallow this one: 529 is an illegal
        // conversion, not a conversion failure, so it propagates on real too.
        WithLobRow().AssertSqlError(
            $"select {expression} from t",
            529,
            "Explicit conversion from data type text to int is not allowed.");

    [TestMethod]
    [DataRow("cast(tx as varchar(10))", "5")]
    [DataRow("cast(tx as nvarchar(10))", "5")]
    [DataRow("cast(tx as char(3))", "5  ")]
    [DataRow("cast(tx as xml)", "5")]
    [DataRow("cast(nt as varchar(10))", "5")]
    [DataRow("cast(nt as nvarchar(10))", "5")]
    public void ExplicitCastWithinStringFamily_Converts(string expression, string expected) =>
        // xml is string-category here, which is why the string-category test is
        // the whole allow-list rather than an enumeration of the string types.
        AreEqual(expected, WithLobRow().ExecuteScalar($"select {expression} from t"));

    [TestMethod]
    [DataRow("cast(im as varbinary(10))")]
    [DataRow("cast(im as varbinary(max))")]
    [DataRow("cast(im as image)")]
    public void ImageCastWithinBinaryFamily_Converts(string expression) =>
        CollectionAssert.AreEqual(new byte[] { 0x05 }, (byte[])WithLobRow().ExecuteScalar($"select {expression} from t")!);

    [TestMethod]
    public void ImplicitComparison_KeepsItsOwnError() =>
        // The gate sits on the explicit-CAST seam on purpose — real answers the
        // implicit path with a different error entirely (Msg 402 for a string
        // partner, Msg 206 for a numeric one), so routing it through 529 would
        // trade one divergence for another.
        _ = WithLobRow().AssertSqlError("select 1 from t where tx = 'x'", 402);
}
