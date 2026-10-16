using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage.Backup;

// A backup's partition functions and schemes, and where each table, key and
// index is placed on them. They come back as values rather than DDL — real
// writes no DDL for a boundary list — under their original ids, and the
// placements are stamped on the recreated tables, whose rows a partition
// never stores apart.
partial class DatabaseImage
{
    public readonly List<PartitionFunctionImage> PartitionFunctions = [];
    public readonly List<PartitionSchemeImage> PartitionSchemes = [];

    private void CapturePartitioning(Database database)
    {
        foreach (var (_, function) in database.PartitionFunctions)
        {
            this.PartitionFunctions.Add(new PartitionFunctionImage
            {
                Name = function.Name,
                FunctionId = function.FunctionId,
                ParameterTypeSql = TypeSql(function.ParameterType, function.SpelledNumeric),
                BoundaryOnRight = function.BoundaryOnRight,
                Boundaries = [.. function.Boundaries],
                CreateDate = function.CreateDate,
                ModifyDate = function.ModifyDate,
            });
        }
        this.PartitionFunctions.Sort(static (a, b) => a.FunctionId.CompareTo(b.FunctionId));
        foreach (var (_, scheme) in database.PartitionSchemes)
        {
            this.PartitionSchemes.Add(new PartitionSchemeImage
            {
                Name = scheme.Name,
                DataSpaceId = scheme.DataSpaceId,
                FunctionName = scheme.Function.Name,
                Destinations = [.. scheme.Destinations],
                NextUsed = scheme.NextUsed,
            });
        }
        this.PartitionSchemes.Sort(static (a, b) => a.DataSpaceId.CompareTo(b.DataSpaceId));
    }

    private static PlacementImage CapturePlacement(PartitionPlacement? placement, List<byte>? dataCompression, List<byte>? xmlCompression) => new()
    {
        SchemeName = placement?.Scheme.Name,
        ColumnName = placement?.Column.Name,
        DataCompression = dataCompression is null ? null : [.. dataCompression],
        XmlCompression = xmlCompression is null ? null : [.. xmlCompression],
    };

    /// <summary>
    /// Recreates the partition functions and schemes in a restored database
    /// before its tables: a function through its DDL with one NULL boundary,
    /// which settles its parameter type as real's parser would, then in place
    /// under its own id and boundaries.
    /// </summary>
    private void RestorePartitioning(Database database, Action<string> execute)
    {
        foreach (var image in this.PartitionFunctions)
        {
            execute($"CREATE PARTITION FUNCTION {Quote(image.Name)} ({image.ParameterTypeSql}) AS RANGE {(image.BoundaryOnRight ? "RIGHT" : "LEFT")} FOR VALUES (NULL)");
            var parsed = database.PartitionFunctions[image.Name];
            var boundaries = Array.ConvertAll(image.Boundaries, boundary => boundary.IsNull ? SqlValue.Null(parsed.ParameterType) : boundary.CoerceTo(parsed.ParameterType));
            database.PartitionFunctions[image.Name] = new PartitionFunction(image.Name, image.FunctionId, parsed.ParameterType, parsed.DeclaredMaxLength,
                image.BoundaryOnRight, boundaries, image.CreateDate, parsed.SpelledNumeric)
            {
                ModifyDate = image.ModifyDate,
            };
        }
        foreach (var image in this.PartitionSchemes)
        {
            database.PartitionSchemes[image.Name] = new PartitionScheme(image.Name, image.DataSpaceId, database.PartitionFunctions[image.FunctionName],
                [.. image.Destinations], image.NextUsed);
        }
    }

    /// <summary>The placement <paramref name="image"/> names on <paramref name="table"/>, null for none.</summary>
    private static PartitionPlacement? Placement(Database database, HeapTable table, PlacementImage image) =>
        image.SchemeName is { } scheme && image.ColumnName is { } column
            ? new PartitionPlacement(database.PartitionSchemes[scheme], Array.Find(table.Columns, candidate => database.Collation.Equals(candidate.Name, column))
                ?? throw new InvalidOperationException($"RESTORE didn't recreate the partitioning column '{column}'."))
            : null;

    /// <summary>Whether two images hold the same partition functions and schemes, boundaries and all.</summary>
    private static bool SamePartitioning(DatabaseImage live, DatabaseImage image)
    {
        if (live.PartitionFunctions.Count != image.PartitionFunctions.Count || live.PartitionSchemes.Count != image.PartitionSchemes.Count)
            return false;
        for (var i = 0; i < live.PartitionFunctions.Count; i++)
        {
            var (a, b) = (live.PartitionFunctions[i], image.PartitionFunctions[i]);
            if (a.Name != b.Name || a.FunctionId != b.FunctionId || a.ParameterTypeSql != b.ParameterTypeSql || a.BoundaryOnRight != b.BoundaryOnRight
                || a.Boundaries.Length != b.Boundaries.Length)
            {
                return false;
            }
            for (var j = 0; j < a.Boundaries.Length; j++)
            {
                if (PartitionFunction.Compare(a.Boundaries[j], b.Boundaries[j]) != 0)
                    return false;
            }
        }
        for (var i = 0; i < live.PartitionSchemes.Count; i++)
        {
            var (a, b) = (live.PartitionSchemes[i], image.PartitionSchemes[i]);
            if (a.Name != b.Name || a.DataSpaceId != b.DataSpaceId || a.FunctionName != b.FunctionName || a.NextUsed != b.NextUsed
                || !a.Destinations.AsSpan().SequenceEqual(b.Destinations))
            {
                return false;
            }
        }
        return true;
    }

    private void WritePartitioning(BinaryWriter w)
    {
        w.Write(this.PartitionFunctions.Count);
        foreach (var function in this.PartitionFunctions)
            function.Write(w);
        w.Write(this.PartitionSchemes.Count);
        foreach (var scheme in this.PartitionSchemes)
            scheme.Write(w);
    }

    private static void ReadPartitioning(BinaryReader r, DatabaseImage image)
    {
        for (var i = r.ReadInt32(); i > 0; i--)
            image.PartitionFunctions.Add(PartitionFunctionImage.Read(r));
        for (var i = r.ReadInt32(); i > 0; i--)
            image.PartitionSchemes.Add(PartitionSchemeImage.Read(r));
    }
}

/// <summary>A partition function as a backup carries it.</summary>
internal sealed class PartitionFunctionImage
{
    public string Name = "";
    public int FunctionId;
    public string ParameterTypeSql = "";
    public bool BoundaryOnRight;
    public SqlValue[] Boundaries = [];
    public DateTime CreateDate;
    public DateTime ModifyDate;

    public void Write(BinaryWriter w)
    {
        w.Write(this.Name);
        w.Write(this.FunctionId);
        w.Write(this.ParameterTypeSql);
        w.Write(this.BoundaryOnRight);
        w.Write(this.Boundaries.Length);
        foreach (var boundary in this.Boundaries)
            BackupBinary.WriteVariant(w, boundary);
        w.Write(this.CreateDate.Ticks);
        w.Write(this.ModifyDate.Ticks);
    }

    public static PartitionFunctionImage Read(BinaryReader r)
    {
        var name = r.ReadString();
        var functionId = r.ReadInt32();
        var parameterTypeSql = r.ReadString();
        var boundaryOnRight = r.ReadBoolean();
        var boundaries = new SqlValue[r.ReadInt32()];
        for (var i = 0; i < boundaries.Length; i++)
            boundaries[i] = BackupBinary.ReadVariant(r);
        return new PartitionFunctionImage
        {
            Name = name,
            FunctionId = functionId,
            ParameterTypeSql = parameterTypeSql,
            BoundaryOnRight = boundaryOnRight,
            Boundaries = boundaries,
            CreateDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
            ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
        };
    }
}

/// <summary>A partition scheme as a backup carries it.</summary>
internal sealed class PartitionSchemeImage
{
    public string Name = "";
    public int DataSpaceId;
    public string FunctionName = "";
    public int[] Destinations = [];
    public int? NextUsed;

    public void Write(BinaryWriter w)
    {
        w.Write(this.Name);
        w.Write(this.DataSpaceId);
        w.Write(this.FunctionName);
        BackupBinary.WriteInts(w, this.Destinations);
        BackupBinary.WriteNullableInt(w, this.NextUsed);
    }

    public static PartitionSchemeImage Read(BinaryReader r) => new()
    {
        Name = r.ReadString(),
        DataSpaceId = r.ReadInt32(),
        FunctionName = r.ReadString(),
        Destinations = BackupBinary.ReadInts(r),
        NextUsed = BackupBinary.ReadNullableInt(r),
    };
}

/// <summary>
/// Where a table's base rows, a key or an index is placed on a partition
/// scheme, if anywhere, and its per-partition compression.
/// </summary>
internal struct PlacementImage
{
    public string? SchemeName;
    public string? ColumnName;
    public byte[]? DataCompression;
    public byte[]? XmlCompression;

    public readonly void Write(BinaryWriter w)
    {
        BackupBinary.WriteNullable(w, this.SchemeName);
        BackupBinary.WriteNullable(w, this.ColumnName);
        WriteBytes(w, this.DataCompression);
        WriteBytes(w, this.XmlCompression);
    }

    public static PlacementImage Read(BinaryReader r) => new()
    {
        SchemeName = BackupBinary.ReadNullable(r),
        ColumnName = BackupBinary.ReadNullable(r),
        DataCompression = ReadBytes(r),
        XmlCompression = ReadBytes(r),
    };

    private static void WriteBytes(BinaryWriter w, byte[]? bytes)
    {
        w.Write(bytes is not null);
        if (bytes is not null)
        {
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    private static byte[]? ReadBytes(BinaryReader r) => r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null;

    /// <summary>The placement's per-partition compression as the table, key or index holds it.</summary>
    public static List<byte>? List(byte[]? bytes) => bytes is null ? null : [.. bytes];
}
