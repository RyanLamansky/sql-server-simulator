using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal coverage for <see cref="SqlType.PromoteForArithmetic"/>'s
/// string-+-string branch — the per-length result-type rules can't be probed
/// through the public DbDataReader surface (which exposes only the bare
/// <c>SqlServerName</c>), so they live here.
/// </summary>
[TestClass]
public sealed class PromoteStringConcatTests
{
    [TestMethod]
    public void Varchar_Plus_Varchar_SumsLengths() =>
        AreEqual(30, ((VarcharSqlType)SqlType.PromoteForArithmetic(VarcharSqlType.Get(10, Collation.Default, Coercibility.CoercibleDefault), VarcharSqlType.Get(20, Collation.Default, Coercibility.CoercibleDefault), '+')).length);

    [TestMethod]
    public void Varchar_Plus_Varchar_CapsAt8000() =>
        AreEqual(8000, ((VarcharSqlType)SqlType.PromoteForArithmetic(VarcharSqlType.Get(8000, Collation.Default, Coercibility.CoercibleDefault), VarcharSqlType.Get(100, Collation.Default, Coercibility.CoercibleDefault), '+')).length);

    [TestMethod]
    public void NVarchar_Plus_NVarchar_SumsLengths_CapsAt4000() =>
        AreEqual(4000, ((NVarcharSqlType)SqlType.PromoteForArithmetic(NVarcharSqlType.Get(3000, Collation.Default, Coercibility.CoercibleDefault), NVarcharSqlType.Get(2000, Collation.Default, Coercibility.CoercibleDefault), '+')).length);

    [TestMethod]
    public void Char_Plus_Varchar_DropsToVarcharOfCombinedLength() =>
        AreEqual(15, ((VarcharSqlType)SqlType.PromoteForArithmetic(SqlType.GetChar(5), VarcharSqlType.Get(10, Collation.Default, Coercibility.CoercibleDefault), '+')).length);

    [TestMethod]
    public void NChar_Plus_Varchar_PromotesToNVarcharOfCombinedLength() =>
        AreEqual(15, ((NVarcharSqlType)SqlType.PromoteForArithmetic(SqlType.GetNChar(5), VarcharSqlType.Get(10, Collation.Default, Coercibility.CoercibleDefault), '+')).length);

    [TestMethod]
    public void Varchar_Plus_NVarchar_PromotesToNVarcharOfCombinedLength() =>
        AreEqual(30, ((NVarcharSqlType)SqlType.PromoteForArithmetic(VarcharSqlType.Get(10, Collation.Default, Coercibility.CoercibleDefault), NVarcharSqlType.Get(20, Collation.Default, Coercibility.CoercibleDefault), '+')).length);

    [TestMethod]
    public void Unspecified_Plus_BoundedVarchar_DropsToUnspecified()
    {
        // Length 0 means "we don't know"; the result can't reliably claim
        // a sum, so it falls back to the length-unspecified form.
        var result = SqlType.PromoteForArithmetic(VarcharSqlType.Get(0, Collation.Default, Coercibility.CoercibleDefault), VarcharSqlType.Get(10, Collation.Default, Coercibility.CoercibleDefault), '+');
        AreSame(VarcharSqlType.Get(0, Collation.Default, Coercibility.CoercibleDefault), result);
    }

    [TestMethod]
    public void Text_Plus_Varchar_DropsToVarcharUnspecified()
    {
        // LOB family operands have no per-cell width to add — the fall-back
        // is the unspecified-length form of the right family. Text carries
        // Implicit coercibility (it's a column-typed family), so the
        // Collation.Resolve hand-off yields Implicit rather than
        // CoercibleDefault.
        var result = SqlType.PromoteForArithmetic(SqlType.Text, VarcharSqlType.Get(10, Collation.Default, Coercibility.CoercibleDefault), '+');
        AreSame(VarcharSqlType.Get(0, Collation.Default, Coercibility.Implicit), result);
    }

    [TestMethod]
    public void NText_Plus_Char_DropsToNVarcharUnspecified()
    {
        // NText carries Implicit; the char(5) bridge is also Implicit (via
        // SqlType.GetChar's static helper), so the resolved rank is Implicit.
        var result = SqlType.PromoteForArithmetic(SqlType.NText, SqlType.GetChar(5), '+');
        AreSame(NVarcharSqlType.Get(0, Collation.Default, Coercibility.Implicit), result);
    }
}
