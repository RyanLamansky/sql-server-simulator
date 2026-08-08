namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>hierarchyid</c>: variable-length binary CLR UDT representing
/// a path through a tree (e.g. <c>/1/2/3/</c>). Internally a sequence of
/// "segments"; each segment is a tuple of signed-integer labels separated by
/// dots in the string form (e.g. the segment <c>1.5.3</c> is the int tuple
/// <c>[1, 5, 3]</c>). The string form joins segments with <c>/</c> with leading
/// and trailing slashes, so the root path is <c>/</c> (zero segments).
/// </summary>
/// <remarks>
/// <para>
/// The in-memory, on-disk, <c>CAST AS varbinary</c>, TDS-UDT-wire, and
/// <c>DATALENGTH</c> byte form are all one and the same: SQL Server's canonical
/// OrdPath encoding (see <see cref="HierarchyIdOrdPath"/>). The page codec here
/// is therefore a verbatim byte copy — the value already holds the OrdPath bytes
/// — so a stored value re-serializes with zero re-encoding and byte-matches a
/// real server. The segment-array form is a transient decode used only by
/// <c>ToString()</c> and the instance methods.
/// </para>
/// </remarks>
internal sealed class HierarchyIdSqlType() : SqlType(SqlTypeCategory.Other)
{
    public override Type ClrType => typeof(byte[]);

    public override bool IsFixedLength => false;

    /// <summary>Bytes a non-NULL value contributes to a row's variable-length area — its OrdPath byte length, which is also its <c>DATALENGTH</c>.</summary>
    public override int GetVariableByteCount(SqlValue value) => value.AsHierarchyIdBytes.Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsHierarchyIdBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromHierarchyIdBytes(source.ToArray());

    public override SqlValue ConvertParameter(object raw) => raw switch
    {
        // A byte[] parameter is raw OrdPath bytes — stored verbatim, matching
        // how SqlClient binds a SqlHierarchyId's serialized form.
        byte[] bytes => SqlValue.FromHierarchyIdBytes(bytes),
        string s => SqlValue.FromHierarchyId(ParsePath(s)),
        _ => throw new NotSupportedException($"No conversion from {raw.GetType()} to hierarchyid."),
    };

    public override string ToString() => "hierarchyid";

    /// <summary>
    /// Parses the canonical string form (<c>/1/2.5/3/</c>) into the
    /// segment-array internal representation. Empty input or input that
    /// doesn't match the leading/trailing-slash shape raises Msg 6522 with
    /// the same wording SQL Server produces for an invalid input to
    /// <c>hierarchyid::Parse</c>.
    /// </summary>
    public static long[][] ParsePath(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || input[0] != '/' || input[^1] != '/')
            throw SimulatedSqlException.InvalidHierarchyIdInput(input);
        if (input.Length == 1)
            return [];
        var inner = input.AsSpan(1, input.Length - 2);
        var segments = new List<long[]>();
        var start = 0;
        for (var i = 0; i <= inner.Length; i++)
        {
            if (i == inner.Length || inner[i] == '/')
            {
                if (i == start)
                    throw SimulatedSqlException.InvalidHierarchyIdInput(input);
                segments.Add(ParseSegment(inner[start..i], input));
                start = i + 1;
            }
        }
        return [.. segments];
    }

    private static long[] ParseSegment(ReadOnlySpan<char> segment, string fullInput)
    {
        var labels = new List<long>();
        var start = 0;
        for (var i = 0; i <= segment.Length; i++)
        {
            if (i == segment.Length || segment[i] == '.')
            {
                if (i == start)
                    throw SimulatedSqlException.InvalidHierarchyIdInput(fullInput);
                var slice = segment[start..i];
                // Labels span real's whole ordinal domain, which is wider
                // than int; one outside it is as malformed as a non-numeric
                // slice, and real reports the same Msg 6522 for both.
                if (!long.TryParse(slice, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    || value < HierarchyIdOrdPath.DomainMin
                    || value > HierarchyIdOrdPath.DomainMax)
                {
                    throw SimulatedSqlException.InvalidHierarchyIdInput(fullInput);
                }
                labels.Add(value);
                start = i + 1;
            }
        }

        // Every label of a dotted segment but the last encodes as ordinal + 1
        // (the order-preserving trick the terminator bit enables), so a
        // non-final label at the very top of the domain has nowhere to encode:
        // real refuses `/281479271683151.1/` while accepting both
        // `/281479271683150.1/` and `/1.281479271683151/` (probe-confirmed).
        for (var i = 0; i < labels.Count - 1; i++)
        {
            if (labels[i] == HierarchyIdOrdPath.DomainMax)
                throw SimulatedSqlException.InvalidHierarchyIdInput(fullInput);
        }

        return [.. labels];
    }

    /// <summary>
    /// Canonical string form for a parsed path. Empty path → <c>"/"</c>;
    /// otherwise <c>/seg1/seg2/.../</c> where each segment is dot-joined
    /// labels.
    /// </summary>
    public static string PathToString(long[][] path)
    {
        if (path.Length == 0)
            return "/";
        var sb = new System.Text.StringBuilder();
        _ = sb.Append('/');
        foreach (var segment in path)
        {
            for (var i = 0; i < segment.Length; i++)
            {
                if (i > 0)
                    _ = sb.Append('.');
                _ = sb.Append(segment[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            _ = sb.Append('/');
        }
        return sb.ToString();
    }
}
