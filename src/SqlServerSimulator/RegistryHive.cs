using System.Collections.Frozen;

namespace SqlServerSimulator;

/// <summary>
/// The <c>HKEY_LOCAL_MACHINE</c> registry SQL Server 2025 on Linux presents to
/// its registry procedures, as <c>Registry.tsv</c> holds it (crawled 2026-10-17
/// through <c>xp_regenumkeys</c> / <c>xp_regenumvalues</c>): the SQL Server
/// keys under <c>SOFTWARE\Microsoft\Microsoft SQL Server</c> and
/// <c>SOFTWARE\Microsoft\MSSQLServer</c> and the services under
/// <c>SYSTEM\CurrentControlSet\Services</c> whole, and the keys above them one
/// level deep — their values and the names of their subkeys, not those
/// subkeys' contents. Values keep the order the server enumerates them in.
/// </summary>
internal static class RegistryHive
{
    /// <summary>The instance's key, which the <c>xp_instance_reg*</c> procedures read for <see cref="InstanceAlias"/>.</summary>
    private const string InstanceKey = @"SOFTWARE\Microsoft\Microsoft SQL Server\MSSQL";

    /// <summary>The instance-independent path an <c>xp_instance_reg*</c> call names the instance's key by.</summary>
    private const string InstanceAlias = @"SOFTWARE\Microsoft\MSSQLServer";

    private static readonly Lazy<FrozenDictionary<string, RegistryKey>> Keys = new(Load);

    /// <summary>
    /// Opens <paramref name="subkey"/>, the <c>xp_instance_reg*</c> form mapping
    /// <c>SOFTWARE\Microsoft\MSSQLServer</c> to the instance's key when
    /// <paramref name="instanceMapped"/>. Trailing backslashes are ignored and
    /// names compare without case; a leading one is an invalid path. A key
    /// real's registry holds below a key carried only one level deep is
    /// <see cref="RegistryOpen.Unmodeled"/>.
    /// </summary>
    internal static RegistryOpen TryOpen(string subkey, bool instanceMapped, out RegistryKey? key)
    {
        key = null;
        if (subkey.StartsWith('\\'))
            return RegistryOpen.InvalidPath;
        var path = subkey.TrimEnd('\\');
        if (instanceMapped && path.StartsWith(InstanceAlias, StringComparison.OrdinalIgnoreCase)
            && (path.Length == InstanceAlias.Length || path[InstanceAlias.Length] == '\\'))
        {
            path = InstanceKey + path[InstanceAlias.Length..];
        }
        var keys = Keys.Value;
        if (keys.TryGetValue(path, out key))
            return RegistryOpen.Found;
        // The deepest key the hive carries above the path decides whether the
        // next name down is one real holds.
        var segments = path.Split('\\');
        for (var depth = segments.Length - 1; depth >= 0; depth--)
        {
            if (keys.TryGetValue(string.Join('\\', segments, 0, depth), out var ancestor))
            {
                return ancestor.SubKeys.Exists(name => name.Equals(segments[depth], StringComparison.OrdinalIgnoreCase))
                    ? RegistryOpen.Unmodeled
                    : RegistryOpen.Missing;
            }
        }
        return RegistryOpen.Missing;
    }

    private static FrozenDictionary<string, RegistryKey> Load()
    {
        using var stream = typeof(RegistryHive).Assembly.GetManifestResourceStream("SqlServerSimulator.Registry.tsv")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.Registry.tsv is missing.");
        using var reader = new StreamReader(stream);
        var keys = new Dictionary<string, RegistryKey>(StringComparer.OrdinalIgnoreCase);
        RegistryKey? current = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            var fields = line.Split('\t');
            switch (fields[0])
            {
                case "K":
                    current = new RegistryKey(fields[1]);
                    keys[fields[1]] = current;
                    break;
                case "S":
                    current!.SubKeys.Add(fields[1]);
                    break;
                case "V":
                    var cells = new string?[fields.Length - 2];
                    for (var i = 0; i < cells.Length; i++)
                        cells[i] = fields[i + 2] == @"\N" ? null : fields[i + 2];
                    current!.Values.Add(new RegistryValue(fields[1], cells));
                    break;
                default:
                    throw new InvalidDataException($"Unexpected registry line '{line}'.");
            }
        }
        return keys.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>What <see cref="RegistryHive.TryOpen"/> found.</summary>
internal enum RegistryOpen
{
    /// <summary>The key, carried whole.</summary>
    Found,

    /// <summary>No such key: <c>RegOpenKeyEx()</c>'s error 2.</summary>
    Missing,

    /// <summary>A path with a leading backslash: <c>RegOpenKeyEx()</c>'s error 161.</summary>
    InvalidPath,

    /// <summary>A key real's registry holds that the hive doesn't carry.</summary>
    Unmodeled,
}

/// <summary>One registry key: its values in enumeration order and the names of its subkeys.</summary>
internal sealed class RegistryKey(string path)
{
    public readonly string Path = path;

    public readonly List<RegistryValue> Values = [];

    public readonly List<string> SubKeys = [];

    /// <summary>The value named <paramref name="name"/> (without case), if the key has one.</summary>
    public RegistryValue? Find(string name)
    {
        foreach (var value in this.Values)
        {
            if (value.Cells[0] is { } valueName && valueName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }
}

/// <summary>
/// One value as <c>xp_regenumvalues</c> sends it: the row of its own result
/// set, its name first and its data after — one cell for a string or DWORD,
/// more for a multi-string — each typed <c>D</c> (an <c>int</c>) or <c>S</c>
/// (an <c>nvarchar(255)</c>) in <see cref="Types"/>.
/// </summary>
internal readonly struct RegistryValue(string types, string?[] cells)
{
    public readonly string Types = types;

    public readonly string?[] Cells = cells;
}
