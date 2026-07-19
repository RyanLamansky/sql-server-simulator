using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Result-set width metadata (COLMETADATA) as a real SqlClient reader observes
/// it via <c>GetColumnSchema().ColumnSize</c>. String / binary literals type at
/// their exact value width (probe-confirmed against SQL Server 2025 — a bare
/// <c>'abc'</c> advertises <c>varchar(3)</c>, not the <c>varchar(8000)</c>
/// container it once did), and the width algebra that combines them (concat
/// sum-capped, CASE / COALESCE / set-op max, ISNULL first-arg, per-function
/// derivations) flows through to the wire. SqlClient reports <c>ColumnSize</c>
/// in characters for the string families and <c>int.MaxValue</c> for a MAX
/// column.
/// </summary>
[TestClass]
public sealed class StringLiteralWidthWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private async Task<int> ColumnSizeAsync(string sql)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        return reader.GetColumnSchema()[0].ColumnSize ?? -1;
    }

    // Bare literals: exact value width, empty floors to 1, over-cap widens to MAX.
    [TestMethod]
    [DataRow("select 'abc' as x", 3)]
    [DataRow("select '' as x", 1)]
    [DataRow("select 'ab  ' as x", 4)]              // trailing spaces counted
    [DataRow("select N'abc' as x", 3)]              // chars, not bytes
    [DataRow("select N'' as x", 1)]
    [DataRow("select 0xAABB as x", 2)]
    [DataRow("select 0x as x", 1)]                  // empty binary floors to 1
    public async Task BareLiterals_TypeAtExactWidth(string sql, int expected)
        => AreEqual(expected, await ColumnSizeAsync(sql));

    [TestMethod]
    [DataRow("select cast('x' as varchar(max)) + 'abc' as x")]     // MAX operand propagates
    public async Task MaxOperand_WidensToMax(string sql)
        => AreEqual(int.MaxValue, await ColumnSizeAsync(sql));

    // A string literal longer than the family bound widens to the MAX form.
    [TestMethod]
    public async Task OverBoundLiteral_WidensToMax()
    {
        AreEqual(int.MaxValue, await ColumnSizeAsync($"select '{new string('a', 8001)}' as x"));
        AreEqual(int.MaxValue, await ColumnSizeAsync($"select N'{new string('a', 4001)}' as x"));
    }

    // Concatenation: sum of widths, capped at the family maximum (not MAX).
    [TestMethod]
    [DataRow("select 'ab' + 'cde' as x", 5)]
    [DataRow("select N'ab' + N'cde' as x", 5)]
    [DataRow("select 'ab' + N'cde' as x", 5)]                       // mixed family → nvarchar, char-count sum
    [DataRow("select cast('x' as varchar(10)) + 'abc' as x", 13)]
    [DataRow("select replicate(cast('a' as varchar(5000)), 1) + replicate(cast('b' as varchar(5000)), 1) as x", 8000)]
    public async Task Concatenation_SumsCappedAtFamilyMax(string sql, int expected)
        => AreEqual(expected, await ColumnSizeAsync(sql));

    // CASE / COALESCE / IIF / NULLIF / set ops: maximum of arm widths.
    [TestMethod]
    [DataRow("select case when 1=1 then 'ab' else 'wxyz' end as x", 4)]
    [DataRow("select case when 1=1 then 'ab' else null end as x", 2)]
    [DataRow("select case when 1=1 then 'ab' else N'wxyz' end as x", 4)]  // national family, char-count max
    [DataRow("select coalesce('ab', 'wxyz') as x", 4)]
    [DataRow("select iif(1=1, 'ab', 'wxyz') as x", 4)]
    [DataRow("select nullif('abcd', 'x') as x", 4)]
    [DataRow("select 'ab' as x union all select 'wxyz'", 4)]
    [DataRow("select 'ab' as x union select 'wxyz'", 4)]
    public async Task Unification_TakesMaxWidth(string sql, int expected)
        => AreEqual(expected, await ColumnSizeAsync(sql));

    // ISNULL fixes the result to the FIRST argument's width (unlike COALESCE).
    [TestMethod]
    [DataRow("select isnull('ab', 'wxyz') as x", 2)]
    [DataRow("select isnull('wxyz', 'ab') as x", 4)]
    public async Task IsNull_TakesFirstArgumentWidth(string sql, int expected)
        => AreEqual(expected, await ColumnSizeAsync(sql));

    // Width-deriving / preserving functions consume the literal width.
    [TestMethod]
    [DataRow("select upper('abc') as x", 3)]           // preserve input width
    [DataRow("select ltrim('  abc') as x", 5)]
    [DataRow("select left('abcdef', 2) as x", 2)]       // min(inputWidth, n)
    [DataRow("select left('abcdef', 20) as x", 6)]
    [DataRow("select right('abcdef', 2) as x", 2)]
    [DataRow("select substring('abcdef', 2, 3) as x", 3)]
    [DataRow("select substring('abcdef', 5, 20) as x", 6)]
    [DataRow("select replicate('ab', 3) as x", 6)]      // inputWidth × count
    [DataRow("select space(5) as x", 5)]
    [DataRow("select space(0) as x", 1)]                // floors to 1
    [DataRow("select stuff('abcdef', 2, 1, 'XY') as x", 7)]  // inputWidth - delete + replacement
    public async Task LengthDerivingFunctions_ComputeWidth(string sql, int expected)
        => AreEqual(expected, await ColumnSizeAsync(sql));

    // REPLACE can grow the input, so it stays the family container (8000).
    [TestMethod]
    public async Task Replace_StaysContainerWidth()
        => AreEqual(8000, await ColumnSizeAsync("select replace('aaa', 'a', 'XY') as x"));
}
