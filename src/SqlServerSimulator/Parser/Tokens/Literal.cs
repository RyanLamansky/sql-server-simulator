using SqlServerSimulator.Storage;
using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// A typed literal value embedded in the SQL text: <c>'foo'</c> (varchar),
/// <c>N'foo'</c> (nvarchar), and <c>0xHEX</c> (varbinary). The surrounding
/// parser treats these uniformly as values via <see cref="Value"/>; numeric
/// literals retain their own <see cref="Numeric"/> token because they
/// participate in the integer-promotion path differently.
/// </summary>
internal sealed class Literal(SqlValue value, string command, int index, int length) : Token(command, index, length)
{
    public readonly SqlValue Value = value;

    /// <summary>
    /// Longest character-literal body real keeps in a message's
    /// <c>near '…'</c> slot, counted in UTF-16 code units after the doubled
    /// delimiters collapse. Probe-confirmed against SQL Server 2025
    /// (2026-07-31): a 130-character literal reports 129 characters, 200
    /// doubled quotes report 129 apostrophes, and 200 astral-plane characters
    /// report 129 code units — 64 whole ones plus a split surrogate pair, so
    /// the clip counts code units rather than text elements.
    /// </summary>
    private const int MaxBodyLength = 129;

    /// <summary>
    /// Longest binary-literal value real keeps in the same slot, in bytes —
    /// twice <see cref="MaxBodyLength"/>, consistent with one shared 258-byte
    /// buffer that a character body fills two bytes per code unit.
    /// Probe-confirmed: 258 bytes render whole, 259 and beyond clip to 258.
    /// </summary>
    private const int MaxBinaryLength = MaxBodyLength * 2;

    /// <summary>
    /// Renders this literal the way real names it in a message's
    /// <c>near '…'</c> slot, which follows the value rather than the spelling:
    /// a character literal loses its delimiters, its <c>N</c> prefix and the
    /// doubling that escaped an embedded delimiter (<c>'it''s'</c> is reported
    /// as <c>it's</c>), while a binary literal is re-rendered from its parsed
    /// bytes as lowercase hex, restoring the leading zero an odd-digit literal
    /// omitted (<c>0xABC</c> is reported as <c>0x0abc</c>). A currency literal
    /// keeps its source text — <c>$00005</c> stays <c>$00005</c> rather than
    /// becoming the money value it denotes.
    /// <para>
    /// A character body renders as it was written, <em>not</em> as the value
    /// the collation stores: under a CP1252 database <c>'日本'</c> stores as
    /// <c>??</c> yet real still reports <c>near '日本'</c>, so this reads the
    /// source rather than <see cref="Value"/>.
    /// </para>
    /// </summary>
    public override string ErrorText
    {
        get
        {
            var source = this.Source;
            return source[0] switch
            {
                '\'' or '"' => UnescapeBody(source[1..^1], source[0]),
                'N' or 'n' => UnescapeBody(source[2..^1], '\''),
                '0' when source.Length > 1 && source[1] is 'x' or 'X' => this.RenderBytes(),
                _ => Clip(source),
            };
        }
    }

    /// <summary>
    /// Collapses each doubled <paramref name="delimiter"/> in
    /// <paramref name="body"/> to the one character it stands for, stopping at
    /// <see cref="MaxBodyLength"/> code units.
    /// </summary>
    private static string UnescapeBody(ReadOnlySpan<char> body, char delimiter)
    {
        var builder = new StringBuilder(Math.Min(body.Length, MaxBodyLength));
        for (var i = 0; i < body.Length && builder.Length < MaxBodyLength; i++)
        {
            _ = builder.Append(body[i]);
            if (body[i] == delimiter)
                i++;
        }
        return builder.ToString();
    }

    /// <summary>Renders the parsed bytes as a <c>0x</c>-prefixed lowercase hex run.</summary>
    private string RenderBytes()
    {
        var bytes = this.Value.AsBytes.AsSpan();
        return string.Concat("0x", Convert.ToHexStringLower(bytes[..Math.Min(bytes.Length, MaxBinaryLength)]));
    }
}
