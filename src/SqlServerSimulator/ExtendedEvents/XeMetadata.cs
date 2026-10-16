using System.Globalization;
using System.IO.Compression;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// The Extended Events engine's catalog — its packages and the events,
/// actions, targets, types, maps, predicate sources and comparators they
/// hold, with each object's columns and each map's values — exactly as SQL
/// Server 2025's <c>sys.dm_xe_packages</c>, <c>sys.dm_xe_objects</c>,
/// <c>sys.dm_xe_object_columns</c> and <c>sys.dm_xe_map_values</c> list them
/// (dumped 2026-10-16 from 17.0.4065.4 on Linux). The rows live in
/// <c>ExtendedEvents/Metadata/*.tsv</c>, zipped into the assembly at build
/// time, and are read once per process on first use.
/// </summary>
/// <remarks>
/// Each file is one row per line, tab-separated, with <c>\t</c>, <c>\n</c>,
/// <c>\r</c> and <c>\\</c> escaped and <c>\N</c> for NULL. A package or
/// object reference is its zero-based line number in <c>Packages.tsv</c> or
/// <c>Objects.tsv</c> (empty for NULL), and <c>ObjectColumns.tsv</c> /
/// <c>MapValues.tsv</c> open each object's run of rows with an <c>@index</c>
/// line, the runs in the order real lists them.
/// </remarks>
internal sealed class XeMetadata
{
    public readonly XePackage[] Packages;

    /// <summary>Every object, in <c>sys.dm_xe_objects</c>' order.</summary>
    public readonly XeObject[] Objects;

    /// <summary>Every object column, in <c>sys.dm_xe_object_columns</c>' order.</summary>
    public readonly XeObjectColumn[] Columns;

    /// <summary>Every map value, in <c>sys.dm_xe_map_values</c>' order.</summary>
    public readonly XeMapValue[] MapValues;

    private XeMetadata()
    {
        using var stream = typeof(XeMetadata).Assembly.GetManifestResourceStream("SqlServerSimulator.XeMetadata.zip")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.XeMetadata.zip is missing.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var packages = new List<XePackage>();
        foreach (var fields in ReadRows(archive, "Packages.tsv"))
        {
            packages.Add(new XePackage(packages.Count, fields[0], Guid.Parse(fields[1]!), fields[2], OptionalInt(fields[3]), fields[4],
                fields[5], Convert.FromHexString(fields[6]!.AsSpan(2))));
        }
        this.Packages = [.. packages];

        var objects = new List<XeObject>();
        foreach (var fields in ReadRows(archive, "Objects.tsv"))
        {
            objects.Add(new XeObject(objects.Count, fields[0], fields[1], this.Packages[int.Parse(fields[2]!, CultureInfo.InvariantCulture)], fields[3]!,
                OptionalInt(fields[4]), fields[5], fields[6], OptionalPackage(fields[7]), OptionalInt(fields[8])));
        }
        this.Objects = [.. objects];

        var columns = new List<XeObjectColumn>();
        var owner = this.Objects[0];
        var run = new List<XeObjectColumn>();
        foreach (var fields in ReadRows(archive, "ObjectColumns.tsv", owner: true))
        {
            if (fields.Length == 1)
            {
                owner.Columns = [.. run];
                run.Clear();
                owner = this.Objects[int.Parse(fields[0]!, CultureInfo.InvariantCulture)];
                continue;
            }
            var column = new XeObjectColumn(owner, fields[0]!, int.Parse(fields[1]!, CultureInfo.InvariantCulture), fields[2]!, OptionalPackage(fields[3]), fields[4]!,
                fields[5], OptionalInt(fields[6]), fields[7], fields[8]);
            columns.Add(column);
            run.Add(column);
        }
        owner.Columns = [.. run];
        this.Columns = [.. columns];

        var mapValues = new List<XeMapValue>();
        owner = this.Objects[0];
        var values = new List<XeMapValue>();
        foreach (var fields in ReadRows(archive, "MapValues.tsv", owner: true))
        {
            if (fields.Length == 1)
            {
                owner.MapValues = [.. values];
                values.Clear();
                owner = this.Objects[int.Parse(fields[0]!, CultureInfo.InvariantCulture)];
                continue;
            }
            var value = new XeMapValue(owner, int.Parse(fields[0]!, CultureInfo.InvariantCulture), fields[1]!);
            mapValues.Add(value);
            values.Add(value);
        }
        owner.MapValues = [.. values];
        this.MapValues = [.. mapValues];

        int? OptionalInt(string? field) => field is null ? null : int.Parse(field, CultureInfo.InvariantCulture);

        XePackage? OptionalPackage(string? field) => string.IsNullOrEmpty(field) ? null : this.Packages[int.Parse(field, CultureInfo.InvariantCulture)];
    }

    /// <summary>The process-wide catalog, read on first use.</summary>
    public static XeMetadata Instance => Holder.Value;

    private static class Holder
    {
        public static readonly XeMetadata Value = new();
    }

    /// <summary>
    /// The object of <paramref name="objectType"/> named <paramref name="name"/>
    /// in the package named <paramref name="package"/>, or in the first package
    /// holding one when <paramref name="package"/> is null; names compare
    /// case-insensitively, as real resolves them (probed 2026-10-16 against
    /// SQL Server 2025). Private objects are found too; the caller decides
    /// whether one may be used.
    /// </summary>
    public XeObject? Find(string? package, string name, string objectType)
    {
        foreach (var candidate in this.Objects)
        {
            if (candidate.ObjectType == objectType
                && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (package is null || candidate.Package.Name.Equals(package, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>The object of <paramref name="objectType"/> named <paramref name="name"/> in <paramref name="package"/> exactly.</summary>
    public XeObject? Find(XePackage package, string name, string objectType)
    {
        foreach (var candidate in this.Objects)
        {
            if (ReferenceEquals(candidate.Package, package) && candidate.ObjectType == objectType && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    /// <summary>The rows of one file, each split into its unescaped fields; a run's <c>@index</c> line yields that index alone when <paramref name="owner"/> is set.</summary>
    private static IEnumerable<string?[]> ReadRows(ZipArchive archive, string entry, bool owner = false)
    {
        using var reader = new StreamReader((archive.GetEntry(entry) ?? throw new InvalidOperationException($"{entry} is missing from the Extended Events metadata.")).Open());
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            if (owner && line[0] == '@')
            {
                yield return [line[1..]];
                continue;
            }
            var fields = line.Split('\t');
            var result = new string?[fields.Length];
            for (var i = 0; i < fields.Length; i++)
                result[i] = Unescape(fields[i]);
            yield return result;
        }
    }

    private static string? Unescape(string field)
    {
        if (field == "\\N")
            return null;
        if (!field.Contains('\\', StringComparison.Ordinal))
            return field;
        var builder = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];
            if (c != '\\' || i + 1 == field.Length)
            {
                _ = builder.Append(c);
                continue;
            }
            _ = builder.Append(field[++i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                var other => other,
            });
        }
        return builder.ToString();
    }
}

/// <summary>One row of <c>sys.dm_xe_packages</c>; <see cref="Index"/> is its position there.</summary>
internal sealed class XePackage(int index, string? name, Guid guid, string? description, int? capabilities, string? capabilitiesDesc, string? moduleGuid, byte[] moduleAddress)
{
    public readonly int Index = index;
    public readonly string Name = name!;
    public readonly Guid Guid = guid;
    public readonly string Description = description!;
    public readonly int? Capabilities = capabilities;
    public readonly string? CapabilitiesDesc = capabilitiesDesc;
    public readonly string ModuleGuid = moduleGuid!;

    /// <summary>Real's load address for the module, kept as a stand-in.</summary>
    public readonly byte[] ModuleAddress = moduleAddress;
}

/// <summary>One row of <c>sys.dm_xe_objects</c>, with its columns and, for a map, its values.</summary>
internal sealed class XeObject(int index, string? name, string? objectType, XePackage package, string description, int? capabilities, string? capabilitiesDesc, string? typeName, XePackage? typePackage, int? typeSize)
{
    public readonly int Index = index;
    public readonly string Name = name!;

    /// <summary><c>event</c>, <c>action</c>, <c>target</c>, <c>type</c>, <c>map</c>, <c>pred_source</c>, <c>pred_compare</c> or <c>message</c>.</summary>
    public readonly string ObjectType = objectType!;
    public readonly XePackage Package = package;
    public readonly string Description = description;
    public readonly int? Capabilities = capabilities;
    public readonly string? CapabilitiesDesc = capabilitiesDesc;
    public readonly string? TypeName = typeName;
    public readonly XePackage? TypePackage = typePackage;
    public readonly int? TypeSize = typeSize;

    /// <summary>This object's rows of <c>sys.dm_xe_object_columns</c>, in real's order.</summary>
    public XeObjectColumn[] Columns = [];

    /// <summary>This map's rows of <c>sys.dm_xe_map_values</c>, in real's order.</summary>
    public XeMapValue[] MapValues = [];

    /// <summary>Whether real hides the object from sessions: its capabilities carry the <c>private</c> bit.</summary>
    public bool IsPrivate => this.Capabilities is { } bits && (bits & 1) != 0;

    /// <summary><c>package.name</c>, as real's messages spell an object.</summary>
    public string QualifiedName => this.Package.Name + "." + this.Name;
}

/// <summary>One row of <c>sys.dm_xe_object_columns</c>.</summary>
internal sealed class XeObjectColumn(XeObject owner, string name, int columnId, string typeName, XePackage? typePackage, string columnType, string? columnValue, int? capabilities, string? capabilitiesDesc, string? description)
{
    public readonly XeObject Owner = owner;
    public readonly string Name = name;
    public readonly int ColumnId = columnId;
    public readonly string TypeName = typeName;
    public readonly XePackage? TypePackage = typePackage;

    /// <summary><c>readonly</c>, <c>data</c> or <c>customizable</c>.</summary>
    public readonly string ColumnType = columnType;
    public readonly string? ColumnValue = columnValue;
    public readonly int? Capabilities = capabilities;
    public readonly string? CapabilitiesDesc = capabilitiesDesc;
    public readonly string? Description = description;
}

/// <summary>One row of <c>sys.dm_xe_map_values</c>.</summary>
internal sealed class XeMapValue(XeObject map, int key, string value)
{
    public readonly XeObject Map = map;
    public readonly int Key = key;
    public readonly string Value = value;
}
