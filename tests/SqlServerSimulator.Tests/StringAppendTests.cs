using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// A <c>varchar(max)</c> or <c>nvarchar(max)</c> variable built up a piece at
/// a time — <c>@v + …</c>, <c>@v += …</c>, <c>CONCAT(@v, …)</c>, the list idiom
/// <c>COALESCE(@v + ',', '') + …</c> — appends to its text in place, and every
/// answer is the ordinary assignment's, a NULL, a read of the variable mid-way
/// and a number among the parts included (probed 2026-10-25 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class StringAppendTests
{
    [TestMethod]
    [DataRow("declare @v varchar(max) = ''; select @v = @v + s from (values (1, 'a'), (2, null), (3, 'b')) t(id, s) order by id; select isnull(@v, '<null>')", "<null>")]
    [DataRow("declare @v varchar(max); select @v = coalesce(@v + ',', '') + s from (values (1, 'a'), (2, null), (3, 'b'), (4, 'c')) t(id, s) order by id; select @v", "b,c")]
    [DataRow("declare @v varchar(max); select @v = isnull(@v + ', ', '') + s from (values (1, 'a'), (2, 'b'), (3, null), (4, 'c')) t(id, s) order by id; select @v", "c")]
    [DataRow("declare @v varchar(max) = ''; select @v = @v + cast(len(@v) as varchar(10)) from (values (1), (2), (3), (4)) t(id) order by id; select @v", "0123")]
    [DataRow("declare @a varchar(max) = '', @b varchar(max); select @a = @a + s, @b = @a from (values (1, 'x'), (2, 'y'), (3, 'z')) t(id, s) order by id; select @a + '|' + @b", "xyz|xyz")]
    [DataRow("declare @v varchar(max) = '5'; set @v = @v + 1; select @v", "6")]
    [DataRow("declare @v nvarchar(max) = N''; select @v += s + N'-' from (values (1, 'a'), (2, 'b')) t(id, s) order by id; select @v", "a-b-")]
    [DataRow("declare @v varchar(max) = ''; while len(@v) < 25 set @v += 'abc'; select @v", "abcabcabcabcabcabcabcabcabc")]
    [DataRow("create table #u (id int, s varchar(10)); insert #u values (1, 'p'), (2, 'q'), (3, 'r'); declare @v varchar(max) = '>'; update #u set @v = @v + s; select @v", ">pqr")]
    [DataRow("declare @v varchar(max) = 'a'; set @v = @v + char(13) + char(10) + quotename('t') + space(1) + replicate('z', 2); select @v", "a\r\n[t] zz")]
    [DataRow("declare @v varchar(max) = 'ab   '; set @v += 'c   '; select concat(len(@v), '|', datalength(@v), '|', @v, '|')", "6|9|ab   c   |")]
    [DataRow("declare @v varchar(max) = ''; select @v = @v + c from (values (1, cast('ab' as char(4))), (2, cast('c' as char(2)))) t(id, c) order by id; select @v + '|'", "ab  c   |")]
    [DataRow("declare @v varchar(max) = 'a'; select @v = concat(@v, ',', s) from (values (1, 'b'), (2, null), (3, 'c')) t(id, s) order by id; select @v", "a,b,,c")]
    [DataRow("declare @v varchar(max) = 'x'; set @v = concat(@v, 1, 2.5, null, cast('2026-10-25' as date)); select @v", "x12.52026-10-25")]
    [DataRow("declare @v varchar(max); select @v = concat(@v, s) from (values (1, 'a'), (2, 'b'), (3, 'c')) t(id, s) order by id; select @v", "abc")]
    [DataRow("declare @v varchar(max) = 'o'; set @v = concat(@v, N'é', N'ő'); select @v", "oéo")]
    [DataRow("declare @v nvarchar(max) = N'z'; set @v = concat(@v, 'a', N'ő'); select @v", "zaő")]
    public void Append_AnswersAsTheAssignmentDoes(string sql, string expected) =>
        AreEqual(expected, ExecuteScalar(sql));

    /// <summary>
    /// An append takes time linear in what it adds, where copying the whole
    /// string at each step cost the square of the steps.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    [DataRow("declare @v varchar(max) = ''; select @v = @v + cast(value as varchar(10)) from generate_series(1, 100000) order by value; select len(@v)", 488895L)]
    [DataRow("declare @v varchar(max); select @v = coalesce(@v + ',', '') + cast(value as varchar(10)) from generate_series(1, 100000) order by value; select len(@v)", 588894L)]
    [DataRow("declare @v nvarchar(max) = N'', @i int = 0; while @i < 100000 begin set @v += N'abcdefghij'; set @i += 1; end; select len(@v)", 1000000L)]
    [DataRow("declare @v varchar(max) = ''; select @v = concat(@v, value, ',') from generate_series(1, 100000) order by value; select len(@v)", 588895L)]
    public void ManyAppends_RunInLinearTime(string sql, long expected) =>
        AreEqual(expected, ExecuteScalar(sql));
}
