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
/// The byte form on disk and via <c>CAST AS varbinary</c> is the simulator's
/// own representation rather than SQL Server's documented variable-bit ordinal
/// encoding. Round-trip via <c>CAST hierarchyid -&gt; varbinary -&gt; hierarchyid</c>
/// works inside the simulator; cross-engine byte transfer (BCP, SqlClient UDT
/// wire format) is deferred until the BACPAC loader bundle, at which point
/// the encoder/decoder will be replaced with the documented format.
/// </para>
/// <para>
/// Internal format: 2-byte little-endian segment count, then per segment a
/// 2-byte little-endian label count followed by each label as a 4-byte
/// little-endian int32. The root (<c>/</c>) is 2 bytes (segment count 0).
/// </para>
/// </remarks>
internal sealed class HierarchyIdSqlType() : SqlType(SqlTypeCategory.Other)
{
    public override Type ClrType => typeof(byte[]);

    public override bool IsFixedLength => false;

    /// <summary>
    /// Bytes a non-NULL value contributes to a row's variable-length area:
    /// 2 (segment count) + sum over segments of 2 + 4 * label-count.
    /// </summary>
    public override int GetVariableByteCount(SqlValue value)
    {
        var path = value.AsHierarchyId;
        var bytes = 2;
        foreach (var segment in path)
            bytes += 2 + (segment.Length * 4);
        return bytes;
    }

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var path = value.AsHierarchyId;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)path.Length);
        var offset = 2;
        foreach (var segment in path)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)segment.Length);
            offset += 2;
            foreach (var label in segment)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], label);
                offset += 4;
            }
        }
        return offset;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
            return SqlValue.FromHierarchyId([]);
        var segmentCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source);
        var path = new int[segmentCount][];
        var offset = 2;
        for (var i = 0; i < segmentCount; i++)
        {
            var labelCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
            offset += 2;
            var segment = new int[labelCount];
            for (var j = 0; j < labelCount; j++)
            {
                segment[j] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
                offset += 4;
            }
            path[i] = segment;
        }
        return SqlValue.FromHierarchyId(path);
    }

    public override SqlValue ConvertParameter(object raw) => raw switch
    {
        byte[] bytes => this.Decode(bytes),
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
    public static int[][] ParsePath(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || input[0] != '/' || input[^1] != '/')
            throw SimulatedSqlException.InvalidHierarchyIdInput(input);
        if (input.Length == 1)
            return [];
        var inner = input.AsSpan(1, input.Length - 2);
        var segments = new List<int[]>();
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

    private static int[] ParseSegment(ReadOnlySpan<char> segment, string fullInput)
    {
        var labels = new List<int>();
        var start = 0;
        for (var i = 0; i <= segment.Length; i++)
        {
            if (i == segment.Length || segment[i] == '.')
            {
                if (i == start)
                    throw SimulatedSqlException.InvalidHierarchyIdInput(fullInput);
                var slice = segment[start..i];
                if (!int.TryParse(slice, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw SimulatedSqlException.InvalidHierarchyIdInput(fullInput);
                labels.Add(value);
                start = i + 1;
            }
        }
        return [.. labels];
    }

    /// <summary>
    /// Canonical string form for a parsed path. Empty path → <c>"/"</c>;
    /// otherwise <c>/seg1/seg2/.../</c> where each segment is dot-joined
    /// labels.
    /// </summary>
    public static string PathToString(int[][] path)
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

    /// <summary>
    /// Lexicographic comparison on two paths: compares segment by segment,
    /// each segment lexicographically on its label tuple. A shorter
    /// prefix sorts before its extensions (e.g. <c>/1/</c> &lt; <c>/1/2/</c>;
    /// <c>/1/2/</c> &lt; <c>/1/2.1/</c>).
    /// </summary>
    public static int ComparePaths(int[][] left, int[][] right)
    {
        var common = Math.Min(left.Length, right.Length);
        for (var i = 0; i < common; i++)
        {
            var cmp = CompareSegment(left[i], right[i]);
            if (cmp != 0)
                return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareSegment(int[] left, int[] right)
    {
        var common = Math.Min(left.Length, right.Length);
        for (var i = 0; i < common; i++)
        {
            var cmp = left[i].CompareTo(right[i]);
            if (cmp != 0)
                return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }
}
