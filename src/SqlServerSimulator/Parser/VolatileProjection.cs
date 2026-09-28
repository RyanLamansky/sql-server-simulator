using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// How a query body's per-call-varying projection — a column computed from
/// <c>NEWID()</c> or <c>CRYPT_GEN_RANDOM</c> — reaches a query that reads the
/// body as a derived table, CTE or <c>VALUES</c> source. Real merges such a
/// body into the reader and draws the value once per row the <em>reader</em>
/// produces, wherever the source sits, unless the body needs the value itself
/// (<see cref="FixesValues"/>); probed 2026-09-28 against SQL Server 2025.
/// </summary>
/// <remarks>
/// A joined reader re-draws each <see cref="Ordinals"/> column per output row
/// (<see cref="Refresh"/>), so a one-row body crossed with ten rows reads ten
/// values, and two references to one column within a row agree. A volatile
/// column whose expression reads a body column or a subquery can't be
/// re-evaluated outside the body; it keeps the value the body drew
/// (<see cref="Complete"/> is false), which the materialization pass answers
/// by re-running a non-leftmost body per left row as before.
/// </remarks>
internal sealed class VolatileProjection(bool fixesValues, int[] ordinals, Expression[] expressions, bool complete)
{
    /// <summary>
    /// True when the body reads the value itself — a <c>DISTINCT</c> over a
    /// FROM, an <c>ORDER BY</c> key under <c>TOP</c> / <c>OFFSET</c>, a
    /// multi-row <c>VALUES</c> — so real draws it once per body row and every
    /// reader sees that draw.
    /// </summary>
    public readonly bool FixesValues = fixesValues;

    /// <summary>The volatility of a body that draws once per body row and re-draws nothing.</summary>
    public static readonly VolatileProjection Fixed = new(fixesValues: true, [], [], complete: true);

    /// <summary>The output ordinals a reader re-draws per row.</summary>
    public readonly int[] Ordinals = ordinals;

    /// <summary>Each re-drawn column's projection expression, aligned with <see cref="Ordinals"/>.</summary>
    public readonly Expression[] Expressions = expressions;

    /// <summary>Whether every volatile column is re-drawable, so the body's own rows may be kept for the enumeration.</summary>
    public readonly bool Complete = complete;

    /// <summary>
    /// The volatility of a body projecting <paramref name="projection"/>, or
    /// null when no column draws per call.
    /// </summary>
    public static VolatileProjection? Of(List<Expression> projection, bool fixesValues)
    {
        List<int>? ordinals = null;
        List<Expression>? expressions = null;
        var complete = true;
        for (var i = 0; i < projection.Count; i++)
        {
            var (draws, readsRow) = Classify(projection[i]);
            if (!draws)
                continue;
            if (readsRow)
            {
                complete = false;
                continue;
            }
            (ordinals ??= []).Add(i);
            (expressions ??= []).Add(projection[i]);
        }
        return ordinals is null && complete
            ? null
            : fixesValues
                ? Fixed
                : new VolatileProjection(fixesValues: false, [.. ordinals ?? []], [.. expressions ?? []], complete);
    }

    /// <summary>Whether <paramref name="node"/> calls a per-call-varying built-in anywhere below it.</summary>
    public static bool DrawsPerCall(ExpressionNode node) => Classify(node).Draws;

    private static (bool Draws, bool ReadsRow) Classify(ExpressionNode node)
    {
        var draws = false;
        var readsRow = false;
        node.Walk((visited, shape) =>
        {
            if (visited is NewId or CryptGenRandom)
                draws = true;
            if (shape.Column is not null || shape.Locals.Exists(local => local is Selection))
                readsRow = true;
            return true;
        });
        return (draws, readsRow);
    }

    /// <summary>
    /// <paramref name="row"/> with each <see cref="Ordinals"/> column drawn
    /// afresh — a new array, since the body's row may be shared by every
    /// output row it joined to.
    /// </summary>
    public byte[] Refresh(byte[] row, HeapColumn[] columns, BatchContext batch)
    {
        var values = new SqlValue[columns.Length];
        for (var c = 0; c < columns.Length; c++)
            values[c] = RowDecoder.DecodeColumn(columns, row, c);
        var runtime = new RuntimeContext(static name => throw SimulatedSqlException.InvalidColumnName(name), batch);
        for (var i = 0; i < this.Ordinals.Length; i++)
        {
            var ordinal = this.Ordinals[i];
            var drawn = this.Expressions[i].Run(runtime);
            var type = columns[ordinal].Type;
            values[ordinal] = drawn.IsNull || drawn.Type == type ? drawn : drawn.CoerceTo(type);
        }
        return RowEncoder.EncodeRow(columns, values);
    }
}
