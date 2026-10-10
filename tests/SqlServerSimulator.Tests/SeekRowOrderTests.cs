using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A seek with no ORDER BY reads its rows in the order of the index it seeks:
/// by the index key, then by the row locator — the clustered key on a clustered
/// table, the row's address on a heap — however the rows were written, moved by
/// an UPDATE or put back by a rollback, and an IN list's values in ascending
/// order. Each setup seeks once before the write it tests, so the seek cache
/// is maintained across that write rather than built after it. Every
/// expectation probed 2026-10-10 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SeekRowOrderTests
{
    private static string Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join(":", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetValue(i)}")));
        return string.Join(",", rows);
    }

    private const string ManyToMany = """
        create table p (id int identity primary key, name nvarchar(30) not null);
        create table pf (id bigint identity primary key, from_person_id int not null references p(id), to_person_id int not null references p(id),
            constraint uq unique (from_person_id, to_person_id));
        create index ix_from on pf(from_person_id); create index ix_to on pf(to_person_id);
        insert p (name) values ('Anne'), ('Bill'), ('Chuck'), ('David');
        insert pf (from_person_id, to_person_id) values (1, 2), (1, 3), (2, 1), (3, 1);
        insert pf (from_person_id, to_person_id) values (4, 1), (4, 3), (1, 4), (3, 4);
        select id from pf where from_person_id = 1;
        begin tran; save tran s1;
        delete pf where (from_person_id = 2 and to_person_id = 1) or (from_person_id = 1 and to_person_id = 2);
        rollback
        """;

    private const string Drifted = """
        create table t (id int primary key, a int not null, v int not null default 0);
        create index ix_a on t(a);
        insert t (id, a) values (5, 1), (3, 1), (9, 1), (1, 2), (7, 1), (8, 2), (2, 2), (6, 3);
        select id from t where a = 1
        """;

    [TestMethod]
    [DataRow(ManyToMany, "select p.id, p.name from p inner join pf on (p.id = pf.to_person_id) where pf.from_person_id = 1", "2:Bill,3:Chuck,4:David")]
    [DataRow(ManyToMany, "select id from pf where from_person_id = 1", "1,2,7")]
    [DataRow("create table h (id int not null, a int not null, pad char(10) not null default 'x'); create index ix_a on h(a); insert h (id, a) values (1, 1), (2, 1), (3, 2), (4, 1); select id from h where a = 1; begin tran; delete h where id = 1; rollback", "select id, pad from h where a = 1", "1:x         ,2:x         ,4:x         ")]
    [DataRow("create table t (id int primary key, a int not null, pad char(10) not null default 'x'); create index ix_a on t(a); insert t (id, a) values (1, 2), (2, 1), (3, 1), (4, 1); select id from t where a = 1; update t set a = 1 where id = 1", "select id from t where a = 1", "1,2,3,4")]
    [DataRow("create table h (id int not null, a int not null, pad char(10) not null default 'x'); create index ix_a on h(a); insert h (id, a) values (1, 2), (2, 1), (3, 1), (4, 1); select id from h where a = 1; update h set a = 1 where id = 1", "select id, a from h where a = 1", "1:1,2:1,3:1,4:1")]
    [DataRow(Drifted, "select id from t where a = 1", "3,5,7,9")]
    [DataRow(Drifted, "select id from t where a between 1 and 2", "3,5,7,9,1,2,8")]
    [DataRow(Drifted, "select id from t where a > 1", "1,2,8,6")]
    [DataRow(Drifted, "select id from t where a in (2, 1)", "3,5,7,9,1,2,8")]
    [DataRow(Drifted, "select id from t where a = 1 or a = 2", "3,5,7,9,1,2,8")]
    [DataRow(Drifted, "update t set v = 1 output inserted.id where a = 1", "3,5,7,9")]
    [DataRow(Drifted, "delete t output deleted.id where a = 2", "1,2,8")]
    [DataRow("create table c (a int not null, b int not null, v int null, primary key (a, b)); insert c (a, b, v) values (1, 3, 0), (1, 1, 0), (2, 5, 0), (1, 2, 0)", "select b, v from c where a = 1", "1:0,2:0,3:0")]
    [DataRow("create table c (a int not null, b int not null, c int not null, primary key (a, b)); create index ix_c on c(c); insert c values (2, 1, 1), (1, 5, 1), (1, 2, 1), (3, 0, 2), (1, 1, 2)", "select a, b from c where c = 1", "1:2,1:5,2:1")]
    [DataRow("create table n (k int not null, a int not null, id int not null); create clustered index cx on n(k); create index ix_a on n(a); insert n values (5, 1, 1), (3, 1, 2), (5, 1, 3), (1, 1, 4), (3, 1, 5)", "select k, id from n where a = 1", "1:4,3:2,3:5,5:1,5:3")]
    [DataRow("create table s (id int primary key, a int not null, b int not null); create index ix_ab on s(a, b); insert s values (1, 1, 9), (2, 1, 3), (3, 2, 1), (4, 1, 5), (5, 1, 3); select id from s where a = 1", "select id, b from s where a = 1", "2:3,5:3,4:5,1:9")]
    [DataRow("create table s (id int primary key, a int not null, b int not null); create index ix_ab on s(a, b); insert s values (1, 1, 9), (2, 1, 3), (3, 2, 1), (4, 1, 5), (5, 1, 3)", "select id, b from s where a = 1 and b > 2", "2:3,5:3,4:5,1:9")]
    public void Seek_ReadsTheIndexOrder(string setup, string query, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(setup);
        AreEqual(expected, Rows(simulation, query));
    }
}
