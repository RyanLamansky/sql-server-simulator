using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The scope a <b>non-<c>APPLY</c></b> FROM source's own arguments bind in.
/// SQL Server holds none of the FROM's sources there — only <c>CROSS</c> /
/// <c>OUTER APPLY</c> makes a right side lateral — so a generator naming a
/// sibling is Msg 4104 while the same source under <c>APPLY</c>, or one reading
/// an <em>enclosing</em> query's column, answers.
/// <para>
/// Every case carries its probe citation (<c>N1.nn</c> / <c>N1b.nn</c>) from the
/// matrix run against SQL Server 2025 on 2026-08-05.
/// </para>
/// </summary>
[TestClass]
public sealed class GeneratorSourceScopeTests
{
    /// <summary>Two rows carrying a CSV column and a JSON column, plus an inline and a multi-statement TVF over the id.</summary>
    private static Simulation WithGenerators()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, csv varchar(100) not null, j nvarchar(200) not null);
            insert t values (1, 'a,b', N'[{"v":1},{"v":2}]'), (2, 'c', N'[{"v":3}]');
            """);
        sim.ExecuteBatches("create function dbo.tvf (@x int) returns table as return (select @x as y)");
        sim.ExecuteBatches(
            "create function dbo.mtvf (@x int) returns @r table (y int) as begin insert into @r values (@x); return; end");
        return sim;
    }

    private static void Msg4104(Simulation sim, string sql, string identifier)
        => Contains($"\"{identifier}\"", sim.AssertSqlError(sql, 4104).Message);

    private static void Msg207(Simulation sim, string sql, string leaf)
        => Contains($"'{leaf}'", sim.AssertSqlError(sql, 207).Message);

    // ---- the refusal, per generator kind and per join form ----------------

    /// <summary>N1.01 — <c>STRING_SPLIT</c> as a JOIN right side.</summary>
    [TestMethod]
    public void StringSplit_JoinRightSide_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, s.value from t join string_split(t.csv, ',') s on 1 = 1", "t.csv");

    /// <summary>N1.02 — the CROSS JOIN spelling.</summary>
    [TestMethod]
    public void StringSplit_CrossJoin_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, s.value from t cross join string_split(t.csv, ',') s", "t.csv");

    /// <summary>N1.03 — the comma spelling.</summary>
    [TestMethod]
    public void StringSplit_CommaJoin_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, s.value from t, string_split(t.csv, ',') s", "t.csv");

    /// <summary>N1.23 — a LEFT JOIN right side refuses identically.</summary>
    [TestMethod]
    public void StringSplit_LeftJoin_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, s.value from t left join string_split(t.csv, ',') s on 1 = 1", "t.csv");

    /// <summary>N1.08 — the generator is leftmost and names a sibling written after it.</summary>
    [TestMethod]
    public void StringSplit_Leftmost_ReadingLaterSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, s.value from string_split(t.csv, ',') s join t on 1 = 1", "t.csv");

    /// <summary>N1.09 — <c>OPENJSON</c>.</summary>
    [TestMethod]
    public void OpenJson_JoinRightSide_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, o.v from t join openjson(t.j) with (v int) o on 1 = 1", "t.j");

    /// <summary>N1.11 — an inline table-valued function.</summary>
    [TestMethod]
    public void InlineTvf_JoinRightSide_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, f.y from t join dbo.tvf(t.id) f on 1 = 1", "t.id");

    /// <summary>N1.13 — a multi-statement table-valued function.</summary>
    [TestMethod]
    public void MultiStatementTvf_JoinRightSide_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, f.y from t join dbo.mtvf(t.id) f on 1 = 1", "t.id");

    /// <summary>N1.14 — a table-value constructor's cell.</summary>
    [TestMethod]
    public void ValuesConstructor_JoinRightSide_ReadingSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, v.x from t join (values (t.id)) v(x) on 1 = 1", "t.id");

    /// <summary>N1.21 — the sibling reference is buried in an expression.</summary>
    [TestMethod]
    public void Tvf_ArgumentExpressionOverSibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select t.id, f.y from t join dbo.tvf(t.id + 1) f on 1 = 1", "t.id");

    /// <summary>N1.24 — one generator naming another generator's output column.</summary>
    [TestMethod]
    public void Generator_ReadingAnotherGenerator_IsMsg4104()
        => Msg4104(WithGenerators(), "select s2.value from string_split('a,b', ',') s1 join string_split(s1.value, ',') s2 on 1 = 1", "s1.value");

    /// <summary>N1b.09 — the argument names the generator's own alias.</summary>
    [TestMethod]
    public void Generator_ReadingItsOwnAlias_IsMsg4104()
        => Msg4104(WithGenerators(), "select s.value from t join string_split(s.value, ',') s on 1 = 1", "s.value");

    /// <summary>
    /// N1.26 — the qualifier names a sibling and the column doesn't exist:
    /// still Msg 4104, because the whole multi-part name is out of scope rather
    /// than the column being missing from a scope that holds it.
    /// </summary>
    [TestMethod]
    public void Generator_ReadingUnknownColumnOfASibling_IsMsg4104()
        => Msg4104(WithGenerators(), "select s.value from t join string_split(t.nosuch, ',') s on 1 = 1", "t.nosuch");

    /// <summary>N1.25 — the <em>unqualified</em> spelling is real's plain Msg 207 on the leaf.</summary>
    [TestMethod]
    public void Generator_ReadingUnqualifiedSiblingColumn_IsMsg207()
        => Msg207(WithGenerators(), "select t.id, s.value from t join string_split(csv, ',') s on 1 = 1", "csv");

    /// <summary>N1b.10 — a joined UPDATE's FROM takes the same refusal.</summary>
    [TestMethod]
    public void Generator_InAJoinedUpdateFrom_IsMsg4104()
        => Msg4104(WithGenerators(), "update u set u.id = 9 from t u join string_split(u.csv, ',') s on 1 = 1", "u.csv");

    /// <summary>N1b.04 — real binds the batch, so a never-taken IF branch refuses too.</summary>
    [TestMethod]
    public void Generator_InsideANeverTakenIfBranch_IsMsg4104()
        => Msg4104(WithGenerators(), "if 1 = 0 select t.id, s.value from t join string_split(t.csv, ',') s on 1 = 1", "t.csv");

    /// <summary>N1b.08 — a sibling of the <c>APPLY</c> body's own FROM, not of the enclosing one.</summary>
    [TestMethod]
    public void Generator_ReadingASiblingInsideAnApplyBody_IsMsg4104()
        => Msg4104(
            WithGenerators(),
            "select t.id, x.n from t cross apply (select count(*) as n from t u join string_split(u.csv, ',') s on 1 = 1) x",
            "u.csv");

    /// <summary>
    /// N1b.11 — the same alias exists in an enclosing scope, and the sibling
    /// still wins the refusal: the inner FROM's own <c>t</c> is what the name
    /// lands on.
    /// </summary>
    [TestMethod]
    public void Generator_ReadingASiblingShadowingAnEnclosingAlias_IsMsg4104()
        => Msg4104(
            WithGenerators(),
            "select o.id, (select count(*) from t join string_split(t.csv, ',') s on 1 = 1) as n from t o",
            "t.csv");

    // ---- what stays legal -------------------------------------------------

    /// <summary>N1.04 — <c>CROSS APPLY</c> is the form that grants laterality.</summary>
    [TestMethod]
    public void StringSplit_CrossApply_ReadsEachLeftRow()
        => AreEqual(3, WithGenerators().ExecuteScalar("select count(*) from t cross apply string_split(t.csv, ',') s"));

    /// <summary>N1.05 — and <c>OUTER APPLY</c>.</summary>
    [TestMethod]
    public void StringSplit_OuterApply_ReadsEachLeftRow()
        => AreEqual(3, WithGenerators().ExecuteScalar("select count(*) from t outer apply string_split(t.csv, ',') s"));

    /// <summary>N1.10 / N1.12 / N1.15 — the other generators under APPLY.</summary>
    [TestMethod]
    public void OtherGenerators_UnderApply_Answer()
    {
        var sim = WithGenerators();
        AreEqual(3, sim.ExecuteScalar("select count(*) from t cross apply openjson(t.j) with (v int) o"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from t cross apply dbo.tvf(t.id) f"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from t cross apply (values (t.id)) v(x)"));
    }

    /// <summary>N1.06 — a variable argument names no source.</summary>
    [TestMethod]
    public void StringSplit_VariableArgument_UnderJoin_Answers()
        => AreEqual(4, WithGenerators().ExecuteScalar("declare @csv varchar(100) = 'x,y'; select count(*) from t join string_split(@csv, ',') s on 1 = 1"));

    /// <summary>N1.07 — nor does a literal.</summary>
    [TestMethod]
    public void StringSplit_LiteralArgument_UnderJoin_Answers()
        => AreEqual(4, WithGenerators().ExecuteScalar("select count(*) from t join string_split('p,q', ',') s on 1 = 1"));

    /// <summary>N1.17 — the enclosing statement's row is a legal correlation.</summary>
    [TestMethod]
    public void Generator_ReadingTheEnclosingScope_FromASelectListSubquery_Answers()
        => AreEqual(3, WithGenerators().ExecuteScalar("select sum(n) from (select (select count(*) from string_split(o.csv, ',') s) as n from t o) x"));

    /// <summary>N1.18 — the same from an EXISTS body.</summary>
    [TestMethod]
    public void Generator_ReadingTheEnclosingScope_FromAnExistsBody_Answers()
        => AreEqual(1, WithGenerators().ExecuteScalar("select count(*) from t o where exists (select 1 from string_split(o.csv, ',') s where s.value = 'a')"));

    /// <summary>N1.19 — a derived table inside an APPLY body reading the APPLY's left side.</summary>
    [TestMethod]
    public void Generator_InsideAnApplyBodyDerivedTable_ReadingTheApplyLeft_Answers()
        => AreEqual(3, WithGenerators().ExecuteScalar("select count(*) from t o cross apply (select s.value from string_split(o.csv, ',') s) x"));

    /// <summary>N1b.07 — the generator sits in a JOIN inside the APPLY body and reads the APPLY's left side.</summary>
    [TestMethod]
    public void Generator_InAJoinInsideAnApplyBody_ReadingTheApplyLeft_Answers()
        => AreEqual(6, WithGenerators().ExecuteScalar("select sum(x.n) from t o cross apply (select count(*) as n from string_split(o.csv, ',') s join t u on 1 = 1) x"));

    /// <summary>N1b.12 — an enclosing alias the inner FROM doesn't shadow.</summary>
    [TestMethod]
    public void Generator_ReadingAnEnclosingAlias_Answers()
        => AreEqual(6, WithGenerators().ExecuteScalar("select sum(n) from (select (select count(*) from t u join string_split(o.csv, ',') s on 1 = 1) as n from t o) x"));
}
