using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>CROSS APPLY</c> and <c>OUTER APPLY</c>: the right
/// side is a correlated derived table re-executed per left-side row, with
/// the outer row's columns visible inside the inner SELECT's WHERE /
/// projection. CROSS APPLY drops outer rows whose lateral plan yields zero
/// rows; OUTER APPLY null-fills the right side. The shape EF Core 10 emits
/// for <c>SelectMany</c> over a filtered correlated child collection.
/// </summary>
[TestClass]
public sealed class ApplyTests
{
    private static DbConnection SeededBlogsPosts()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table blogs (id int, title varchar(20))").ExecuteNonQuery();
        _ = connection.CreateCommand("create table posts (id int, blog_id int, title varchar(20), score int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert blogs values (1, 'A'), (2, 'B'), (3, 'C')").ExecuteNonQuery();
        // Blog 1: 3 posts. Blog 2: 1 low-score post. Blog 3: no posts.
        _ = connection.CreateCommand(
            "insert posts values " +
            "(1, 1, 'P1', 10), (2, 1, 'P2', 20), (3, 1, 'P3', 30), " +
            "(4, 2, 'P4', 5)").ExecuteNonQuery();
        return connection;
    }

    // === CROSS APPLY ===

    [TestMethod]
    public void CrossApply_FilteredChild_EmitsMatchingPairs()
    {
        // The shape EF Core 10 emits for SelectMany of a filtered nav.
        using var connection = SeededBlogsPosts();
        using var reader = connection.CreateCommand(
            "select p0.bt, p0.pt from blogs as b " +
            "cross apply (select b.title as bt, p.title as pt from posts as p " +
            "             where b.id = p.blog_id and p.score > 10) as p0").ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEquivalent(new[] { ("A", "P2"), ("A", "P3") }, rows);
    }

    [TestMethod]
    public void CrossApply_ZeroMatches_DropsOuterRow()
    {
        // Blog 3 has no posts; CROSS APPLY drops it (INNER-style).
        using var connection = SeededBlogsPosts();
        var ids = new List<int>();
        using var reader = connection.CreateCommand(
            "select b.id from blogs as b " +
            "cross apply (select 1 as marker from posts as p where p.blog_id = b.id) as p0").ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 1, 1, 1, 2 }, ids);
    }

    [TestMethod]
    public void CrossApply_ExpressionOverOuterColumn()
    {
        // Probe #9 shape: lateral WHERE references outer column in arithmetic.
        using var connection = SeededBlogsPosts();
        var rows = new List<(string, int)>();
        using var reader = connection.CreateCommand(
            "select b.title, p0.score from blogs as b " +
            "cross apply (select p.score from posts as p " +
            "             where b.id = p.blog_id and p.score >= b.id * 5) as p0").ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        // Blog 1 (id=1, threshold=5): 10, 20, 30. Blog 2 (id=2, threshold=10): 5 fails.
        CollectionAssert.AreEquivalent(new[] { ("A", 10), ("A", 20), ("A", 30) }, rows);
    }

    [TestMethod]
    public void CrossApply_TopN_PerOuterRow()
    {
        // Top-2 posts by score per blog.
        using var connection = SeededBlogsPosts();
        var rows = new List<(int, string)>();
        using var reader = connection.CreateCommand(
            "select b.id, p0.title from blogs as b " +
            "cross apply (select top(2) p.title, p.score from posts as p " +
            "             where p.blog_id = b.id order by p.score desc) as p0").ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        CollectionAssert.AreEquivalent(new[] { (1, "P3"), (1, "P2"), (2, "P4") }, rows);
    }

    [TestMethod]
    public void CrossApply_NoOnPredicateAccepted()
    {
        // APPLY explicitly forbids ON; the inner WHERE provides correlation.
        // This test just confirms that APPLY without ON parses and runs.
        using var connection = SeededBlogsPosts();
        using var reader = connection.CreateCommand(
            "select b.id, p0.score from blogs as b " +
            "cross apply (select p.score from posts as p where p.blog_id = b.id) as p0 " +
            "order by b.id, p0.score").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (1, 20), (1, 30), (2, 5) }, rows);
    }

    // === OUTER APPLY ===

    [TestMethod]
    public void OuterApply_ZeroMatches_NullFillsRight()
    {
        // Blog 3 has no posts; OUTER APPLY emits one NULL row for it.
        using var connection = SeededBlogsPosts();
        var rows = new List<(int Id, int? Score)>();
        using var reader = connection.CreateCommand(
            "select b.id, p0.score from blogs as b " +
            "outer apply (select p.score from posts as p where p.blog_id = b.id) as p0 " +
            "order by b.id, p0.score").ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        var expected = new List<(int Id, int? Score)> { (1, 10), (1, 20), (1, 30), (2, 5), (3, null) };
        CollectionAssert.AreEqual(expected, rows);
    }

    [TestMethod]
    public void OuterApply_AllOuterRowsAppearAtLeastOnce()
    {
        using var connection = SeededBlogsPosts();
        var ids = new HashSet<int>();
        using var reader = connection.CreateCommand(
            "select b.id from blogs as b " +
            "outer apply (select p.id from posts as p where p.blog_id = b.id and p.score > 999) as p0").ExecuteReader();
        while (reader.Read()) _ = ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, ids.ToArray());
    }

    // === Chained APPLY ===

    [TestMethod]
    public void CrossApply_MultipleChained_CorrelateLeftToRight()
    {
        // Two APPLYs in a row: the second references both b and p0.
        // Blog 1 (scores 10, 20, 30): p0=10 → p1∈{10,20,30}; p0=20 → p1∈{20,30}; p0=30 → p1∈{30}.
        // Blog 2 (score 5): p0=5 → p1∈{5}. Blog 3 has no posts → no rows.
        using var connection = SeededBlogsPosts();
        var triples = new List<(int, int, int)>();
        using var reader = connection.CreateCommand(
            "select b.id, p0.score, p1.score from blogs as b " +
            "cross apply (select p.score from posts as p where p.blog_id = b.id) as p0 " +
            "cross apply (select q.score from posts as q where q.blog_id = b.id and q.score >= p0.score) as p1").ExecuteReader();
        while (reader.Read())
            triples.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        CollectionAssert.AreEquivalent(
            new[] { (1, 10, 10), (1, 10, 20), (1, 10, 30), (1, 20, 20), (1, 20, 30), (1, 30, 30), (2, 5, 5) },
            triples);
    }

    // === Parser rejections ===

    [TestMethod]
    public void Apply_RejectsOnClause()
    {
        // CROSS APPLY ... ON ... is not valid syntax (no ON for APPLY).
        using var connection = SeededBlogsPosts();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select 1 from blogs as b " +
                "cross apply (select 1 from posts as p where p.blog_id = b.id) as p0 " +
                "on b.id = p0.id").ExecuteScalar());
    }

    [TestMethod]
    public void Apply_RequiresParenthesizedSelect()
    {
        using var connection = SeededBlogsPosts();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select 1 from blogs as b cross apply posts as p").ExecuteScalar());
    }

    [TestMethod]
    public void OuterApply_AsLeadingKeyword_RequiresApply()
    {
        // OUTER alone (without LEFT/RIGHT/FULL preceding) only forms OUTER APPLY.
        using var connection = SeededBlogsPosts();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select 1 from blogs as b outer join posts as p on b.id = p.blog_id").ExecuteScalar());
    }
}
