using System.IO.Compression;
using System.Text;

namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// The committed work a database's next log backup carries: each transaction
/// that changed it since the last log backup, as the rows it deleted and
/// inserted, so a <c>RESTORE LOG … WITH STOPAT</c> can land between any two.
/// Kept only while a log backup chain runs (<see cref="Database.KeepsLog"/>);
/// a database that never takes a full backup outside <c>SIMPLE</c> recovery
/// records nothing.
/// </summary>
/// <remarks>
/// A transaction that changed a definition is recorded with
/// <see cref="LoggedCommit.ChangedDefinitions"/> and no rows: a point-in-time
/// restore replays rows forward from the state the restore sequence stands at
/// up to the first such commit, or backward from the log backup's own end
/// image down to the last, and a stop point with one on either side is
/// refused (see <c>docs/claude/backup-restore.md</c>). Past
/// <see cref="CapacityBytes"/> of row images the log stops recording rows
/// until the next log backup, and a stop point inside that span is refused
/// the same way rather than restored wrong.
/// </remarks>
internal sealed class DatabaseLog
{
    /// <summary>The row-image bytes kept between two log backups before recording stops.</summary>
    internal const long CapacityBytes = 32L * 1024 * 1024;

    private readonly Lock gate = new();
    private List<LoggedCommit> commits = [];
    private long bytes;

    /// <summary>The sequence number of the last commit recorded, which every backup set notes as the point its image stands at.</summary>
    public long Sequence;

    /// <summary>When recording stopped for capacity, the first commit unrecorded; null while every commit is.</summary>
    public DateTime? UnrecordedSinceUtc;

    /// <summary>Appends one committed transaction, numbering it.</summary>
    public void Append(LoggedCommit commit)
    {
        lock (this.gate)
        {
            commit.Sequence = ++this.Sequence;
            if (this.UnrecordedSinceUtc is not null)
                return;
            var size = commit.Bytes;
            if (this.bytes + size > CapacityBytes)
            {
                this.UnrecordedSinceUtc = commit.CommitUtc;
                return;
            }
            this.bytes += size;
            this.commits.Add(commit);
        }
    }

    /// <summary>
    /// What a log backup carries: every commit recorded since the last log
    /// backup that truncated, the sequence the backup's image stands at, and
    /// when recording stopped. Taken while the backup holds every table, so
    /// no commit falls between the image and these.
    /// </summary>
    public (List<LoggedCommit> Commits, long Sequence, DateTime? UnrecordedSinceUtc) TakeForBackup()
    {
        lock (this.gate)
            return ([.. this.commits], this.Sequence, this.UnrecordedSinceUtc);
    }

    /// <summary>
    /// Drops the commits through <paramref name="sequence"/>, which a log
    /// backup that truncates (neither <c>COPY_ONLY</c> nor <c>NO_TRUNCATE</c>)
    /// has carried, starting the next span.
    /// </summary>
    public void TruncateThrough(long sequence)
    {
        lock (this.gate)
        {
            _ = this.commits.RemoveAll(commit => commit.Sequence <= sequence);
            this.bytes = 0;
            foreach (var commit in this.commits)
                this.bytes += commit.Bytes;
            if (this.Sequence <= sequence)
                this.UnrecordedSinceUtc = null;
        }
    }

    /// <summary>The sequence a backup's image stands at.</summary>
    public long CurrentSequence()
    {
        lock (this.gate)
            return this.Sequence;
    }

    /// <summary>
    /// Forgets every commit, the sequence starting from <paramref name="sequence"/>
    /// and the definitions from <paramref name="fingerprint"/>: a chain
    /// starting or breaking, a restore.
    /// </summary>
    public void Reset(long sequence, long fingerprint)
    {
        lock (this.gate)
        {
            this.commits = [];
            this.bytes = 0;
            this.UnrecordedSinceUtc = null;
            this.Sequence = sequence;
            this.definitions = fingerprint;
        }
    }

    // The definitions the last commit stood on, as Database.DefinitionFingerprint hashes them.
    private long definitions;

    /// <summary>Takes <paramref name="fingerprint"/> as the database's definitions; true when they moved.</summary>
    public bool NoteDefinitions(long fingerprint)
    {
        lock (this.gate)
        {
            if (fingerprint == this.definitions)
                return false;
            this.definitions = fingerprint;
            return true;
        }
    }

    /// <summary>Serializes a log backup's commits, deflated.</summary>
    public static byte[] ToPayload(List<LoggedCommit> commits, DateTime? unrecordedSinceUtc)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(unrecordedSinceUtc?.Ticks ?? 0);
            writer.Write(commits.Count);
            foreach (var commit in commits)
                commit.Write(writer);
        }
        return buffer.ToArray();
    }

    /// <summary>Reads what <see cref="ToPayload"/> wrote.</summary>
    public static (List<LoggedCommit> Commits, DateTime? UnrecordedSinceUtc) FromPayload(byte[] payload)
    {
        if (payload.Length == 0)
            return ([], null);
        using var deflate = new DeflateStream(new MemoryStream(payload, writable: false), CompressionMode.Decompress);
        using var reader = new BinaryReader(deflate, Encoding.UTF8);
        var unrecorded = reader.ReadInt64();
        var count = reader.ReadInt32();
        var commits = new List<LoggedCommit>(count);
        for (var i = 0; i < count; i++)
            commits.Add(LoggedCommit.Read(reader));
        return (commits, unrecorded == 0 ? null : new DateTime(unrecorded, DateTimeKind.Utc));
    }
}

/// <summary>One committed transaction a <see cref="DatabaseLog"/> recorded.</summary>
internal sealed class LoggedCommit
{
    /// <summary>The commit's place in its database's log, drawn by <see cref="DatabaseLog.Append"/>.</summary>
    public long Sequence;

    public DateTime CommitUtc;

    /// <summary>The transaction changed a definition, which no row replay reproduces.</summary>
    public bool ChangedDefinitions;

    /// <summary>A <c>BEGIN TRAN name WITH MARK</c> transaction's name, or null.</summary>
    public string? MarkName;

    public string? MarkDescription;

    /// <summary>When the marked transaction began, which <c>logmarkhistory.mark_time</c> and a <c>STOPATMARK</c> restore's <c>stop_at</c> report.</summary>
    public DateTime MarkUtc;

    /// <summary>The rows the transaction deleted and inserted, in the order it wrote them.</summary>
    public readonly List<LoggedRowChange> Rows = [];

    /// <summary>Each written table's identity positions at commit: the column's ordinal and its reseed snapshot.</summary>
    public readonly List<(int ObjectId, int Ordinal, Int128? HighWaterMark, Int128? ReseededStart)> Identities = [];

    /// <summary>The row images' size, which <see cref="DatabaseLog.CapacityBytes"/> bounds.</summary>
    public long Bytes
    {
        get
        {
            long size = 64;
            foreach (var row in this.Rows)
                size += 16 + (row.Before?.Length ?? 0) + (row.After?.Length ?? 0);
            return size;
        }
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(this.Sequence);
        writer.Write(this.CommitUtc.Ticks);
        writer.Write(this.ChangedDefinitions);
        BackupBinary.WriteNullable(writer, this.MarkName);
        BackupBinary.WriteNullable(writer, this.MarkDescription);
        writer.Write(this.MarkUtc.Ticks);
        writer.Write(this.Rows.Count);
        foreach (var row in this.Rows)
        {
            writer.Write(row.ObjectId);
            WriteImage(writer, row.Before);
            WriteImage(writer, row.After);
        }
        writer.Write(this.Identities.Count);
        foreach (var (objectId, ordinal, highWaterMark, reseededStart) in this.Identities)
        {
            writer.Write(objectId);
            writer.Write(ordinal);
            BackupBinary.WriteNullableInt128(writer, highWaterMark);
            BackupBinary.WriteNullableInt128(writer, reseededStart);
        }

        static void WriteImage(BinaryWriter writer, byte[]? image)
        {
            writer.Write(image?.Length ?? -1);
            if (image is not null)
                writer.Write(image);
        }
    }

    public static LoggedCommit Read(BinaryReader reader)
    {
        var commit = new LoggedCommit
        {
            Sequence = reader.ReadInt64(),
            CommitUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
            ChangedDefinitions = reader.ReadBoolean(),
            MarkName = BackupBinary.ReadNullable(reader),
            MarkDescription = BackupBinary.ReadNullable(reader),
            MarkUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
        };
        var rows = reader.ReadInt32();
        for (var i = 0; i < rows; i++)
            commit.Rows.Add(new LoggedRowChange(reader.ReadInt32(), ReadImage(reader), ReadImage(reader)));
        var identities = reader.ReadInt32();
        for (var i = 0; i < identities; i++)
            commit.Identities.Add((reader.ReadInt32(), reader.ReadInt32(), BackupBinary.ReadNullableInt128(reader), BackupBinary.ReadNullableInt128(reader)));
        return commit;

        static byte[]? ReadImage(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            return length < 0 ? null : reader.ReadBytes(length);
        }
    }
}

/// <summary>
/// One row a logged transaction wrote: the image it removed and the one it
/// left, each as <see cref="LogReplay.Image"/> keeps it — null for the side an
/// insert or a delete lacks.
/// </summary>
internal readonly struct LoggedRowChange(int objectId, byte[]? before, byte[]? after)
{
    public readonly int ObjectId = objectId;
    public readonly byte[]? Before = before;
    public readonly byte[]? After = after;
}
