using System.Runtime.CompilerServices;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Precomputed per-schema row-navigation geometry: per-ordinal column kind,
/// fixed-section byte offsets, bit-run positions, var-directory indexes, and
/// the null-bitmap / var-count positions. Everything here is a pure function
/// of the <see cref="HeapColumn"/> schema — only the per-row wide-var-offsets
/// tag bit and the var-offset directory contents vary by row — so caching it
/// per schema array turns <c>RowDecoder.DecodeColumn</c>'s two O(columns)
/// walks (header validation + navigate-to-ordinal) into O(1) reads. Profiled
/// as the dominant remaining per-row cost of scan-bound join / aggregate
/// queries after column-name-resolution memoization.
/// </summary>
/// <remarks>
/// Keyed by schema <b>array identity</b> through a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>: the arrays are the
/// long-lived per-table <c>HeapTable.StoredColumns</c> instances (and the
/// per-plan schemas derived from them), so the table costs nothing until a
/// schema is first decoded through the fast path and is collected with it.
/// The layout is immutable after construction, so cross-thread sharing needs
/// no synchronization beyond the weak table's own.
/// </remarks>
internal sealed class RowLayout
{
    private static readonly ConditionalWeakTable<HeapColumn[], RowLayout> cache = [];

    /// <summary>The layout for <paramref name="schema"/>, computed on first use.</summary>
    public static RowLayout For(HeapColumn[] schema) => cache.GetValue(schema, static s => new RowLayout(s));

    /// <summary>How a column's value is located within the row image.</summary>
    public enum ColumnKind : byte
    {
        /// <summary>Fixed-width value at <see cref="Offsets"/>.</summary>
        Fixed,

        /// <summary>Bit within the run byte at <see cref="Offsets"/>, position <see cref="BitIndexes"/>.</summary>
        Bit,

        /// <summary>Variable-length value; <see cref="Offsets"/> is the var-directory index.</summary>
        Variable,
    }

    public readonly ColumnKind[] Kinds;

    /// <summary>
    /// Per-ordinal location: the absolute byte offset of a fixed value or a
    /// bit-run byte, or the var-offset-directory index of a variable column.
    /// </summary>
    public readonly int[] Offsets;

    /// <summary>Bit position within the run byte, for <see cref="ColumnKind.Bit"/> ordinals only.</summary>
    public readonly byte[] BitIndexes;

    /// <summary>Absolute offset where the fixed-length section ends — byte 2's expected header word, the fast path's schema-mismatch guard.</summary>
    public readonly int ExpectedFixedEnd;

    /// <summary>Absolute offset of the null bitmap.</summary>
    public readonly int BitmapStart;

    /// <summary>Absolute offset of the var-column-count word (the null bitmap's end); the var-offset directory follows it.</summary>
    public readonly int VarCountPosition;

    public readonly int VarColumnCount;

    private RowLayout(HeapColumn[] schema)
    {
        if (schema.Length == 0)
            throw new ArgumentException("Schema must have at least one column.", nameof(schema));

        var n = schema.Length;
        this.Kinds = new ColumnKind[n];
        this.Offsets = new int[n];
        this.BitIndexes = new byte[n];

        var fixedPos = 4;
        var varIndex = 0;
        var bitsInRun = 0;
        var bitByteOffset = -1;
        for (var i = 0; i < n; i++)
        {
            if (schema[i].Type == SqlType.Bit)
            {
                if (bitsInRun % 8 == 0)
                {
                    bitByteOffset = fixedPos;
                    fixedPos++;
                }

                this.Kinds[i] = ColumnKind.Bit;
                this.Offsets[i] = bitByteOffset;
                this.BitIndexes[i] = (byte)(bitsInRun % 8);
                bitsInRun++;
            }
            else if (schema[i].Type.IsFixedLength)
            {
                this.Kinds[i] = ColumnKind.Fixed;
                this.Offsets[i] = fixedPos;
                fixedPos += schema[i].Type.FixedLength;
                bitsInRun = 0;
            }
            else
            {
                this.Kinds[i] = ColumnKind.Variable;
                this.Offsets[i] = varIndex;
                varIndex++;
            }
        }

        this.ExpectedFixedEnd = fixedPos;
        this.BitmapStart = fixedPos + 2;
        this.VarCountPosition = this.BitmapStart + ((n + 7) / 8);
        this.VarColumnCount = varIndex;
    }
}
