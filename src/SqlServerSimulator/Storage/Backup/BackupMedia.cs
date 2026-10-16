using System.Reflection;
using System.Text;

namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// The contents of one backup device: the backup sets <c>BACKUP DATABASE</c>
/// wrote to it, in position order (<c>FILE = 1</c> first). The format is the
/// simulator's own — Microsoft's Tape Format isn't reproduced, so a real
/// <c>.bak</c> is refused as an incorrectly formed media family, as real refuses
/// a file that isn't one.
/// </summary>
/// <remarks>
/// A set's payload is its <see cref="DatabaseImage"/>, deflated. The image holds
/// row values in the simulator's own storage encodings, so the header records
/// the simulator build that wrote it and a restore by any other build is
/// refused (Msg 3169, as real refuses a backup from a newer version).
/// </remarks>
internal sealed class BackupMedia
{
    private static readonly byte[] Magic = "SQLSIMBK"u8.ToArray();

    /// <summary>Bumped whenever the layout below or <see cref="DatabaseImage"/>'s changes.</summary>
    private const int FormatVersion = 2;

    /// <summary>
    /// What a media family shorter than this holds is reported as an empty
    /// volume (Msg 3254) rather than an incorrectly formed one (Msg 3241), as
    /// real reports a file under its 4 KB media header (probed 2026-10-11
    /// against SQL Server 2022).
    /// </summary>
    internal const int MinimumMediaLength = 4096;

    /// <summary>The build that writes and reads backups: the assembly's informational version, which carries its commit.</summary>
    internal static readonly string SimulatorVersion =
        typeof(BackupMedia).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The <c>MEDIANAME</c> the device was formatted with, or null.</summary>
    public string? MediaName;

    /// <summary>The <c>MEDIADESCRIPTION</c> the device was formatted with, or null.</summary>
    public string? MediaDescription;

    /// <summary>The backup sets, <c>FILE = n</c> being <c>Sets[n - 1]</c>.</summary>
    public readonly List<BackupSet> Sets = [];

    /// <summary>Serializes the media, padded to <see cref="MinimumMediaLength"/>.</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(SimulatorVersion);
            BackupBinary.WriteNullable(writer, this.MediaName);
            BackupBinary.WriteNullable(writer, this.MediaDescription);
            writer.Write(this.Sets.Count);
            foreach (var set in this.Sets)
                set.Write(writer);
        }
        if (stream.Length < MinimumMediaLength)
            stream.SetLength(MinimumMediaLength);
        return stream.ToArray();
    }

    /// <summary>The outcome of reading a device's bytes.</summary>
    internal enum ReadOutcome
    {
        Read,

        /// <summary>Shorter than a media header: Msg 3254.</summary>
        Empty,

        /// <summary>Not a simulator backup, or a damaged one: Msg 3241.</summary>
        Malformed,

        /// <summary>Written by another simulator build: Msg 3169.</summary>
        OtherVersion,
    }

    /// <summary>
    /// Reads <paramref name="bytes"/> as media; <paramref name="writtenBy"/> is
    /// the writing build's version when <see cref="ReadOutcome.OtherVersion"/>.
    /// </summary>
    public static ReadOutcome TryRead(byte[] bytes, out BackupMedia? media, out string? writtenBy)
    {
        media = null;
        writtenBy = null;
        if (bytes.Length < MinimumMediaLength)
            return ReadOutcome.Empty;
        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return ReadOutcome.Malformed;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
            _ = reader.ReadBytes(Magic.Length);
            var format = reader.ReadInt32();
            writtenBy = reader.ReadString();
            if (format != FormatVersion || writtenBy != SimulatorVersion)
                return ReadOutcome.OtherVersion;
            var read = new BackupMedia
            {
                MediaName = BackupBinary.ReadNullable(reader),
                MediaDescription = BackupBinary.ReadNullable(reader),
            };
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
                read.Sets.Add(BackupSet.Read(reader));
            media = read;
            return ReadOutcome.Read;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return ReadOutcome.Malformed;
        }
    }
}

/// <summary>
/// One backup set on a <see cref="BackupMedia"/>: the header fields
/// <c>RESTORE</c> checks and reports, and the compressed
/// <see cref="DatabaseImage"/>.
/// </summary>
internal sealed class BackupSet
{
    public string? Name;
    public string? Description;
    public string DatabaseName = "";
    public Guid BackupSetGuid;
    public DateTime StartUtc;
    public DateTime FinishUtc;
    public bool IsCopyOnly;

    /// <summary>The block of the set's first LSN, the server's last transaction id as the backup began; its last LSN is 24 blocks on, as a full backup's is on real.</summary>
    public long FirstLsnBlock;

    /// <summary>Written <c>WITH CHECKSUM</c>, which a <c>RESTORE WITH CHECKSUM</c> requires.</summary>
    public bool HasChecksums;

    /// <summary>The pages each file reports in the <c>Processed N pages</c> messages, by logical name, in file order.</summary>
    public readonly List<(string LogicalName, long Pages)> FilePages = [];

    /// <summary>The deflated <see cref="DatabaseImage"/>.</summary>
    public byte[] Payload = [];

    public void Write(BinaryWriter writer)
    {
        BackupBinary.WriteNullable(writer, this.Name);
        BackupBinary.WriteNullable(writer, this.Description);
        writer.Write(this.DatabaseName);
        writer.Write(this.BackupSetGuid.ToByteArray());
        writer.Write(this.StartUtc.Ticks);
        writer.Write(this.FinishUtc.Ticks);
        writer.Write(this.IsCopyOnly);
        writer.Write(this.FirstLsnBlock);
        writer.Write(this.HasChecksums);
        writer.Write(this.FilePages.Count);
        foreach (var (name, pages) in this.FilePages)
        {
            writer.Write(name);
            writer.Write(pages);
        }
        writer.Write(this.Payload.Length);
        writer.Write(this.Payload);
    }

    public static BackupSet Read(BinaryReader reader)
    {
        var set = new BackupSet
        {
            Name = BackupBinary.ReadNullable(reader),
            Description = BackupBinary.ReadNullable(reader),
            DatabaseName = reader.ReadString(),
            BackupSetGuid = new Guid(reader.ReadBytes(16)),
            StartUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
            FinishUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
            IsCopyOnly = reader.ReadBoolean(),
            FirstLsnBlock = reader.ReadInt64(),
            HasChecksums = reader.ReadBoolean(),
        };
        var files = reader.ReadInt32();
        for (var i = 0; i < files; i++)
            set.FilePages.Add((reader.ReadString(), reader.ReadInt64()));
        var length = reader.ReadInt32();
        set.Payload = reader.ReadBytes(length);
        if (set.Payload.Length != length)
            throw new EndOfStreamException();
        return set;
    }
}

/// <summary>The few <see cref="BinaryWriter"/> shapes the backup format adds.</summary>
internal static class BackupBinary
{
    public static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    public static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    public static void WriteInts(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
            writer.Write(value);
    }

    public static int[] ReadInts(BinaryReader reader)
    {
        var values = new int[reader.ReadInt32()];
        for (var i = 0; i < values.Length; i++)
            values[i] = reader.ReadInt32();
        return values;
    }

    public static void WriteInt128(BinaryWriter writer, Int128 value)
    {
        writer.Write((ulong)(UInt128)value);
        writer.Write((ulong)((UInt128)value >> 64));
    }

    public static Int128 ReadInt128(BinaryReader reader)
    {
        var low = reader.ReadUInt64();
        var high = reader.ReadUInt64();
        return (Int128)(((UInt128)high << 64) | low);
    }

    public static void WriteNullableInt128(BinaryWriter writer, Int128? value)
    {
        writer.Write(value.HasValue);
        if (value is { } v)
            WriteInt128(writer, v);
    }

    public static Int128? ReadNullableInt128(BinaryReader reader) => reader.ReadBoolean() ? ReadInt128(reader) : null;

    public static void WriteNullableInt(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value is { } v)
            writer.Write(v);
    }

    public static int? ReadNullableInt(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;

    /// <summary>
    /// Writes one non-row value — an extended property's — as a
    /// <c>sql_variant</c>, the type every such value reads back as.
    /// </summary>
    public static void WriteVariant(BinaryWriter writer, SqlValue value)
    {
        if (value.IsNull)
        {
            writer.Write((byte)0);
            return;
        }
        var wrapped = value.Type is SqlVariantSqlType;
        var variant = wrapped ? value : SqlValue.FromVariant(value);
        writer.Write(wrapped ? (byte)1 : (byte)2);
        var bytes = new byte[SqlType.SqlVariant.GetVariableByteCount(variant)];
        _ = SqlType.SqlVariant.Encode(variant, bytes);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    public static SqlValue ReadVariant(BinaryReader reader)
    {
        var tag = reader.ReadByte();
        if (tag == 0)
            return SqlValue.Null(SqlType.SqlVariant);
        var variant = SqlType.SqlVariant.Decode(reader.ReadBytes(reader.ReadInt32()));
        return tag == 1 ? variant : variant.AsVariantInner;
    }
}
