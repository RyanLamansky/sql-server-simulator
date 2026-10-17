using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The flags and user type a result column's COLMETADATA carries, as SQL
/// Server 2025 sets them from what the column reads — fNullable 0x01, fCaseSen
/// 0x02, updatability-unknown 0x08, fIdentity 0x10, fComputed 0x20 and
/// fSparseColumnSet 0x0400 — captured 2026-10-17 with a raw TDS client.
/// </summary>
[TestClass]
public sealed class ColumnMetadataFlagsTests
{
    private static TdsSessionFixture Seeded()
    {
        var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create type dbo.al from varchar(5)");
        _ = fixture.RunBatch("""
            create table t (id int identity primary key, a int not null, b varchar(10) null, c as a + 1, rv rowversion,
                cs varchar(10) collate Latin1_General_CS_AS null, bn varchar(10) collate Latin1_General_BIN2 null, x xml null,
                nm sysname null, al dbo.al null);
            create table s (k int primary key, sp int sparse null, cset xml column_set for all_sparse_columns);
            insert t (a, b) values (1, 'x');
            """);
        _ = fixture.RunBatch("create view v as select id, a, c, a + 1 as e, rv from t");
        _ = fixture.RunBatch("create view vg as select a, count(*) as n, row_number() over (order by a) as rn from t group by a");
        _ = fixture.RunBatch("create view vu as select a from t union select a from t");
        return fixture;
    }

    private static string Flags(TdsSessionFixture fixture, string sql) =>
        string.Join(" ", TdsSessionFixture.ColumnFlags(fixture.RunBatch(sql)));

    [TestMethod]
    [DataRow("select id, a, b, c, rv from t", "id:0010 a:0008 b:0009 c:0021 rv:0000:ut=80")]
    [DataRow("select cs, bn, x, nm, al from t", "cs:000b bn:000b x:000b nm:0009:ut=256 al:0009:ut=257")]
    [DataRow("select * from s", "k:0008 cset:040b")]
    public void TableColumns_CarryTheirOwnCharacter(string sql, string flags)
    {
        using var fixture = Seeded();
        AreEqual(flags, Flags(fixture, sql));
    }

    [TestMethod]
    [DataRow("select 1 as k, 'x' as s, null as n, a + 1 as e, b + 'q' as q from t", "k:0020 s:0020 n:0021 e:0021 q:0021")]
    [DataRow("select 1 as k, (select 1) as sq", "k:0020 sq:0021")]
    [DataRow("select b collate Latin1_General_CS_AS as cl from t", "cl:0023")]
    [DataRow("select count(*) as n, max(a) as m from t", "n:0001 m:0001")]
    [DataRow("select row_number() over (order by a) as rn from t", "rn:0001")]
    [DataRow("select a from t union select a from t", "a:0000")]
    [DataRow("select v from (values (1)) q(v)", "v:0000")]
    [DataRow("select value from string_split('a,b', ',')", "value:0000")]
    public void Expressions_AreComputedAndDerivedValuesNeither(string sql, string flags)
    {
        using var fixture = Seeded();
        AreEqual(flags, Flags(fixture, sql));
    }

    /// <summary>
    /// Parentheses, a conversion a nullable column needs none of, a COLLATE
    /// naming its own collation and a CASE folded to one arm read as the column.
    /// </summary>
    [TestMethod]
    [DataRow("select (a) as p, cast(b as varchar(10)) as c1, convert(varchar(10), b) as c2, try_cast(b as varchar(10)) as c3 from t", "p:0008 c1:0009 c2:0009 c3:0009")]
    [DataRow("select b collate SQL_Latin1_General_CP1_CI_AS as cl, case when 1 = 1 then b end as k, iif(1 = 0, null, b) as i from t", "cl:0009 k:0009 i:0009")]
    [DataRow("select cast(a as int) as nn, cast(b as varchar(11)) as wider, convert(varchar(10), b, 0) as styled from t", "nn:0021 wider:0021 styled:0021")]
    public void NoOpOperations_ReadAsTheColumn(string sql, string flags)
    {
        using var fixture = Seeded();
        AreEqual(flags, Flags(fixture, sql));
    }

    /// <summary>
    /// A grouping expression is derived unless every column it reads is grouped
    /// on its own, which leaves it computed over the groups.
    /// </summary>
    [TestMethod]
    [DataRow("select a + 1 as g from t group by a + 1", "g:0001")]
    [DataRow("select a + 1 as g from t group by a + 1, a", "g:0021")]
    [DataRow("select b + 'x' as g, b from t group by b", "g:0021 b:0009")]
    public void GroupingExpressions(string sql, string flags)
    {
        using var fixture = Seeded();
        AreEqual(flags, Flags(fixture, sql));
    }

    /// <summary>
    /// A derived table or CTE passes its column reads through — a computed
    /// column's flag included — and its expressions as derived values.
    /// </summary>
    [TestMethod]
    [DataRow("select q.id, q.c, q.e from (select id, c, a + 1 as e from t) q", "id:0010 c:0021 e:0001")]
    [DataRow("with w as (select c, a + 1 as e from t) select * from w", "c:0021 e:0001")]
    [DataRow("select y.c from t x cross apply (select c from t where t.id = x.id) y", "c:0021")]
    [DataRow("select q.a from (select a from t union select a from t) q", "a:0000")]
    public void DerivedTables_PassColumnReadsThrough(string sql, string flags)
    {
        using var fixture = Seeded();
        AreEqual(flags, Flags(fixture, sql));
    }

    /// <summary>
    /// A view read directly expands — its expression computed, its derived
    /// values updatability-unknown — while a derived table over it reads what
    /// its catalog describes, an identity or a rowversion and otherwise an
    /// updatable column.
    /// </summary>
    [TestMethod]
    [DataRow("select * from v", "id:0010 a:0008 c:0021 e:0021 rv:0000:ut=80")]
    [DataRow("select * from vg", "a:0008 n:0009 rn:0009")]
    [DataRow("select * from vu", "a:0008")]
    [DataRow("select * from (select * from v) q", "id:0010 a:0008 c:0009 e:0009 rv:0000:ut=80")]
    [DataRow("with w as (select c, e from v) select * from w", "c:0009 e:0009")]
    public void Views_ExpandOrReadAsCatalogColumns(string sql, string flags)
    {
        using var fixture = Seeded();
        AreEqual(flags, Flags(fixture, sql));
    }

    /// <summary>A catalog view's computed columns are flagged as real's definitions compute them.</summary>
    [TestMethod]
    public void CatalogViewColumns_FollowRealsDefinitions()
    {
        using var fixture = Seeded();
        AreEqual("name:0008:ut=256 type:0021 is_ms_shipped:0020", Flags(fixture, "select name, type, is_ms_shipped from sys.objects where 1 = 0"));
        AreEqual("type:0009", Flags(fixture, "select q.type from (select type from sys.objects) q where 1 = 0"));
    }

    /// <summary>A case-sensitive database's strings are case-sensitive, a literal's included.</summary>
    [TestMethod]
    public void CaseSensitiveDatabase_FlagsItsStrings()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create database cs collate Latin1_General_CS_AS");
        _ = fixture.RunBatch("use cs");
        AreEqual("s:0022 n:0022 i:0020", Flags(fixture, "select 'x' as s, N'y' as n, 1 as i"));
    }
}
