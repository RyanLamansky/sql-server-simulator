namespace SqlServerSimulator;

/// <summary>
/// An <c>INDEX</c> hint naming one filtered index is a plan real builds only
/// when the query's predicates confine the read to the filter — Msg 8622
/// otherwise. Every shape was probed 2026-10-06 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class FilteredIndexHintTests
{
    private const string Setup = """
        create table t (id int primary key, a int, b int, s varchar(5), d decimal(5, 1));
        create index ix on t (a) where b > 5;
        create index ixn on t (a) where b is not null;
        create index ixs on t (a) where s = 'x';
        create index ixi on t (a) where b in (1, 2);
        create index ixr on t (a) where b > 5 and b < 10;
        create index ixd on t (a) where d >= 1.5;
        create index ixe on t (a) where b <> 3;
        create index ixl on t (a) where b is null;
        insert t values (1, 1, 6, 'x', 2), (2, 2, 1, 'y', 1);
        """;

    [TestMethod]
    [DataRow("select a from t with (index(ix)) where b > 5")]
    [DataRow("select a from t with (index(ix)) where b >= 6")]
    [DataRow("select a from t with (index(ix)) where b = 6")]
    [DataRow("select a from t with (index(ix)) where 5 < b")]
    [DataRow("select a from t with (index(ix)) where 7 = b")]
    [DataRow("select a from t with (index(ix)) where b > 5 and a = 1")]
    [DataRow("select a from t with (index(ix)) where b > 6 and b < 100")]
    [DataRow("select a from t with (index(ix)) where b in (6, 7)")]
    [DataRow("select a from t with (index(ix)) where b between 6 and 8")]
    [DataRow("select a from t with (index(ix)) where not (b <= 5)")]
    [DataRow("select a from t with (index(2)) where b = 9")]
    [DataRow("select t.a from t with (index(ix)) join t u on u.id = t.id and t.b = 7")]
    [DataRow("select a from t with (index(ixn)) where b = 3")]
    [DataRow("select a from t with (index(ixn)) where b > 3")]
    [DataRow("select a from t with (index(ixs)) where s = 'x'")]
    [DataRow("select a from t with (index(ixi)) where b in (2, 1)")]
    [DataRow("select a from t with (index(ixr)) where b > 6 and b < 8")]
    [DataRow("select a from t with (index(ixd)) where d = 2")]
    [DataRow("select a from t with (index(ixe)) where b > 3")]
    [DataRow("select a from t with (index(ixl)) where b is null")]
    [DataRow("select a from t with (index(ix, ixd)) where b = 6")]
    public void APredicateConfiningTheRead_Plans(string query) =>
        _ = new Simulation().ExecuteNonQuery(Setup + query);

    [TestMethod]
    [DataRow("select a from t with (index(ix))")]
    [DataRow("select a from t with (index(ix)) where b > 4")]
    [DataRow("select a from t with (index(ix)) where a = 1 or b > 5")]
    [DataRow("select a from t with (index(ix)) where b in (5, 7)")]
    [DataRow("select a from t with (index(2)) where b = 1")]
    [DataRow("select t.a from t with (index(ix)) join t u on u.id = t.id where u.b = 7")]
    [DataRow("select a from t with (index(ixs)) where s = 'y'")]
    [DataRow("declare @v int = 7; select a from t with (index(ixr)) where b = @v")]
    [DataRow("select a from t with (index(ixd)) where d > 1.4")]
    [DataRow("select a from t with (index(ixe)) where b = 3")]
    [DataRow("select a from t with (index(ixl)) where b = 1")]
    public void APredicateLeavingRowsOutsideTheFilter_IsMsg8622(string query) =>
        _ = new Simulation().AssertSqlError(Setup + query, 8622);
}
