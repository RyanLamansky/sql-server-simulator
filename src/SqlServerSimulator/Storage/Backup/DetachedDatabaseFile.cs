using System.Text;

namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// What one file of a detached database holds on the simulated server's
/// disk: the database it belongs to and its own file id, and — in the primary
/// data file — the whole database as a <see cref="DatabaseImage"/>, which
/// <c>CREATE DATABASE … FOR ATTACH</c> reads back. The format is the
/// simulator's own, so neither real's <c>.mdf</c> files nor any other
/// simulator build's are read, and real reads none of these.
/// </summary>
internal sealed class DetachedDatabaseFile
{
    private static readonly byte[] Magic = "SQLSIMDF"u8.ToArray();

    /// <summary>Bumped whenever the layout below changes.</summary>
    private const int FormatVersion = 1;

    /// <summary>The detached database's <c>database_guid</c>, which every one of its files carries.</summary>
    public Guid DatabaseGuid;

    /// <summary>The file's <c>file_id</c> in the database.</summary>
    public int FileId;

    /// <summary>A log file, which the database can rebuild in its absence.</summary>
    public bool IsLog;

    /// <summary>
    /// The database was restoring when it was detached, which leaves it
    /// unattachable (Msg 1824); its primary file then carries no image.
    /// </summary>
    public bool WasRestoring;

    /// <summary>The primary data file's <see cref="DatabaseImage.ToPayload"/>, null in every other file.</summary>
    public byte[]? Payload;

    /// <summary>The primary data file: the one that carries the database.</summary>
    public bool IsPrimary => this.FileId == 1;

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(BackupMedia.SimulatorVersion);
            writer.Write(this.DatabaseGuid.ToByteArray());
            writer.Write(this.FileId);
            writer.Write(this.IsLog);
            writer.Write(this.WasRestoring);
            writer.Write(this.Payload?.Length ?? -1);
            if (this.Payload is { } payload)
                writer.Write(payload);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Reads <paramref name="bytes"/> as a detached database's file, or null
    /// for anything else — another simulator build's file included.
    /// </summary>
    public static DetachedDatabaseFile? TryRead(byte[] bytes)
    {
        if (bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
            _ = reader.ReadBytes(Magic.Length);
            if (reader.ReadInt32() != FormatVersion || reader.ReadString() != BackupMedia.SimulatorVersion)
                return null;
            var file = new DetachedDatabaseFile
            {
                DatabaseGuid = new Guid(reader.ReadBytes(16)),
                FileId = reader.ReadInt32(),
                IsLog = reader.ReadBoolean(),
                WasRestoring = reader.ReadBoolean(),
            };
            var length = reader.ReadInt32();
            if (length >= 0)
                file.Payload = reader.ReadBytes(length);
            return file;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
