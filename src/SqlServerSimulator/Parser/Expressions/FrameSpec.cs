namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Kind of a single frame boundary, used for the <c>start</c> and <c>end</c>
/// of an explicit window frame (<c>ROWS BETWEEN &lt;start&gt; AND &lt;end&gt;</c>).
/// </summary>
internal enum FrameBoundKind
{
    UnboundedPreceding,
    NPreceding,
    CurrentRow,
    NFollowing,
    UnboundedFollowing,
}

/// <summary>
/// A single frame boundary. <see cref="Offset"/> is meaningful only for
/// <see cref="FrameBoundKind.NPreceding"/> / <see cref="FrameBoundKind.NFollowing"/>;
/// it's the constant integer literal that was supplied (probed: real SQL
/// Server requires a non-negative integer literal here — non-literal
/// expressions raise Msg 102 at parse, negative ones Msg 1014).
/// </summary>
internal readonly struct FrameBound(FrameBoundKind kind, long offset)
{
    public readonly FrameBoundKind Kind = kind;
    public readonly long Offset = offset;

    public static readonly FrameBound UnboundedPreceding = new(FrameBoundKind.UnboundedPreceding, 0);
    public static readonly FrameBound CurrentRow = new(FrameBoundKind.CurrentRow, 0);
    public static readonly FrameBound UnboundedFollowing = new(FrameBoundKind.UnboundedFollowing, 0);
    public static FrameBound NPreceding(long offset) => new(FrameBoundKind.NPreceding, offset);
    public static FrameBound NFollowing(long offset) => new(FrameBoundKind.NFollowing, offset);
}

/// <summary>
/// Explicit window frame (<c>ROWS BETWEEN x AND y</c> or <c>RANGE BETWEEN x AND y</c>).
/// Captured at parse, applied per-row at execute. <see cref="IsRange"/>
/// distinguishes RANGE (peer-tie groups share frame extents) from ROWS
/// (each row gets its own extent by offset arithmetic).
/// </summary>
internal sealed class FrameSpec(bool isRange, FrameBound start, FrameBound end)
{
    public readonly bool IsRange = isRange;

    public readonly FrameBound Start = start;

    public readonly FrameBound End = end;
}
