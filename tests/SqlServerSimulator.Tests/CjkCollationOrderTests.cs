using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The East Asian collations' primary order — script interleaving and ideograph
/// order — probed 2026-09-29 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CjkCollationOrderTests
{
    [TestMethod]
    [DataRow("Chinese_PRC_CI_AS", "az", "a中", -1)]
    [DataRow("Chinese_PRC_CI_AS", "Zebra", "安徽", -1)]
    [DataRow("Chinese_PRC_CI_AS", "安徽", "北京", -1)]
    [DataRow("Chinese_PRC_CI_AS", "北京", "广州", -1)]
    [DataRow("Chinese_PRC_CI_AS", "广州", "上海", -1)]
    [DataRow("Chinese_PRC_CI_AS", "上海", "中国", -1)]
    [DataRow("Chinese_PRC_CI_AS", "重庆", "中国", 1)]
    [DataRow("Chinese_PRC_CI_AS", "长沙", "成都", -1)]
    [DataRow("Chinese_PRC_CI_AS", "ア", "安", -1)]
    [DataRow("Chinese_PRC_CI_AS", "가", "安", -1)]
    [DataRow("Chinese_PRC_CI_AS", "Ａ", "b", -1)]
    [DataRow("Chinese_PRC_Stroke_CI_AS", "十", "一", 1)]
    [DataRow("Chinese_PRC_Stroke_CI_AS", "人", "山", -1)]
    [DataRow("Chinese_Taiwan_Stroke_CI_AS", "十", "一", 1)]
    [DataRow("Chinese_Taiwan_Stroke_CI_AS", "國", "中", 1)]
    [DataRow("Chinese_Taiwan_Stroke_CI_AS", "z", "中", -1)]
    [DataRow("Japanese_CI_AS", "あ", "亜", -1)]
    [DataRow("Japanese_CI_AS", "亜", "唖", -1)]
    [DataRow("Japanese_CI_AS", "漢字", "かんじ", 1)]
    [DataRow("Japanese_CI_AS", "z", "ア", -1)]
    [DataRow("Korean_Wansung_CI_AS", "가", "家", -1)]
    [DataRow("Korean_Wansung_CI_AS", "z", "가", 1)]
    [DataRow("Korean_Wansung_CI_AS", "家", "나", -1)]
    [DataRow("Chinese_PRC_90_CI_AS", "az", "a中", -1)]
    [DataRow("Japanese_XJIS_140_CI_AS", "z", "ア", -1)]
    [DataRow("Chinese_Simplified_Pinyin_100_CI_AS", "安徽", "北京", -1)]
    public void Comparison_FollowsRealsPrimaryOrder(string collation, string left, string right, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when N'{left}' collate {collation} < N'{right}' then -1 when N'{left}' collate {collation} = N'{right}' then 0 else 1 end"));

    [TestMethod]
    public void OrderBy_ReadsPinyinAndPlacesLatinFirst()
        => AreEqual("Zebra|安徽|北京|长沙|广州|上海|中国|重庆", new Simulation().ExecuteScalar("""
            select string_agg(v, '|') within group (order by v collate Chinese_PRC_CI_AS)
            from (values (N'中国'), (N'重庆'), (N'北京'), (N'上海'), (N'安徽'), (N'长沙'), (N'Zebra'), (N'广州')) t(v)
            """));
}
