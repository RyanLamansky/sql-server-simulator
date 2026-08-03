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
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        return reader.GetColumnSchema()[0].ColumnSize ?? -1;
    }

    /// <summary>
    /// The declared width and the first row's value for a statement carrying
    /// bound string parameters, which SqlClient sends as an sp_executesql RPC.
    /// Reading the value as well as the metadata is the point: a COLMETADATA
    /// width narrower than the ROW value's own length prefix is what corrupts
    /// the token stream, and only draining the row observes it.
    /// </summary>
    private async Task<(int ColumnSize, object? Value)> WidthAndValueAsync(string sql, params string[] parameterValues)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < parameterValues.Length; i++)
            _ = command.Parameters.AddWithValue($"@p{i}", parameterValues[i]);

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var size = reader.GetColumnSchema()[0].ColumnSize ?? -1;
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        return (size, reader.GetValue(0));
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

    // A CONCAT argument of no declared width can't contribute to the sum, so
    // the whole result falls back to the family container — probe-confirmed
    // against SQL Server 2025, which answers 8000 / 4000 for these.
    [TestMethod]
    [DataRow("select concat(replace('aaa', 'a', 'XY'), 'x') as x", 8000)]
    [DataRow("select concat_ws('-', replace('aaa', 'a', 'XY'), 'x') as x", 8000)]
    [DataRow("select concat(replace(N'aaa', N'a', N'XY'), N'x') as x", 4000)]
    public async Task Concat_ContainerWidthArgument_StaysContainerWidth(string sql, int expected)
        => AreEqual(expected, await ColumnSizeAsync(sql));

    // A MAX argument still decides the result ahead of any width, container included.
    [TestMethod]
    public async Task Concat_MaxArgumentBesideContainerWidthArgument_StaysMax()
        => AreEqual(int.MaxValue, await ColumnSizeAsync("select concat(cast('a' as varchar(max)), replace('aaa', 'a', 'XY')) as x"));

    /// <summary>
    /// A bound string parameter reaches the expression tree with no declared
    /// width, so a CONCAT over one has no per-argument width to sum. Summing it
    /// as zero projected <c>nvarchar(1)</c> while the concatenation itself
    /// produced the full value, and the resulting ROW length prefix overran the
    /// declared maximum — "Protocol error in TDS stream" on the ODBC driver,
    /// and a silently truncating read elsewhere.
    /// </summary>
    [TestMethod]
    [DataRow("select concat(@p0, @p1) as x", "ab")]
    [DataRow("select concat_ws('-', @p0, @p1) as x", "a-b")]
    public async Task Concat_BoundParameterArguments_DeclareContainerWidthAndReturnWholeValue(string sql, string expected)
    {
        var (columnSize, value) = await WidthAndValueAsync(sql, "a", "b");
        AreEqual(4000, columnSize);
        AreEqual(expected, value);
    }

    /// <summary>One bound parameter beside a literal is enough — the literal's
    /// own width alone would have declared <c>nvarchar(1)</c>.</summary>
    [TestMethod]
    public async Task Concat_BoundParameterBesideLiteral_DeclaresContainerWidthAndReturnsWholeValue()
    {
        var (columnSize, value) = await WidthAndValueAsync("select concat(@p0, 'b') as x", "a");
        AreEqual(4000, columnSize);
        AreEqual("ab", value);
    }
}
