using SqlServerSimulator.ExtendedEvents;

namespace SqlServerSimulator.Audit;

/// <summary>Where a server audit writes: <c>TO FILE</c>, <c>TO APPLICATION_LOG</c> or <c>TO SECURITY_LOG</c>, the three a Linux server takes.</summary>
internal enum AuditDestination : byte
{
    File,
    ApplicationLog,
    SecurityLog,
}

/// <summary>
/// One server audit: its <c>sys.server_audits</c> and
/// <c>sys.server_file_audits</c> row, and while it runs the file it writes.
/// Changed only under <see cref="AuditServer.Gate"/>; a statement inside a
/// transaction logs a <see cref="Clone"/> to roll back to.
/// </summary>
internal sealed class ServerAudit(int id, string name, Guid guid, DateTime createDate, int principalId)
{
    public readonly int Id = id;

    public string Name = name;

    public readonly Guid Guid = guid;

    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// Moved only by <c>MODIFY NAME</c>: real leaves it at the create date
    /// through every other <c>ALTER</c>, a change of target, options or
    /// predicate and a start included (probed 2026-10-17 against SQL Server
    /// 2025).
    /// </summary>
    public DateTime ModifyDate = createDate;

    /// <summary>The creating login's server principal id.</summary>
    public readonly int PrincipalId = principalId;

    public AuditDestination Destination;

    /// <summary>0 <c>CONTINUE</c>, 1 <c>SHUTDOWN</c>, 2 <c>FAIL_OPERATION</c>.</summary>
    public byte OnFailure;

    /// <summary>Milliseconds; 0 writes each record as it fires.</summary>
    public int QueueDelay = 1000;

    /// <summary>The <c>WHERE</c> predicate as <c>sys.server_audits.predicate</c> stores it, null for none.</summary>
    public string? PredicateText;

    public XePredicate? Predicate;

    /// <summary>The directory as written, with a <c>/</c> appended when it ends in neither separator.</summary>
    public string FilePath = "";

    /// <summary>Megabytes; 0 for <c>UNLIMITED</c>.</summary>
    public long MaxSize;

    /// <summary><c>MAX_ROLLOVER_FILES</c>, 2,147,483,647 for <c>UNLIMITED</c> (its default).</summary>
    public int MaxRolloverFiles = int.MaxValue;

    public int MaxFiles;

    public bool ReserveDiskSpace;

    /// <summary>The run in progress, null while the audit is stopped.</summary>
    public RunningAudit? Running;

    /// <summary><c>name_GUID.sqlaudit</c>, which <c>sys.server_file_audits.log_file_name</c> reports and each file's name extends.</summary>
    public string LogFileName => this.Name + "_" + this.Guid.ToString("D").ToUpperInvariant() + ".sqlaudit";

    public string TypeCode => this.Destination switch
    {
        AuditDestination.ApplicationLog => "AL",
        AuditDestination.SecurityLog => "SL",
        _ => "FL",
    };

    public string TypeDesc => this.Destination switch
    {
        AuditDestination.ApplicationLog => "APPLICATION LOG",
        AuditDestination.SecurityLog => "SECURITY LOG",
        _ => "FILE",
    };

    public string OnFailureDesc => this.OnFailure switch
    {
        1 => "SHUTDOWN SERVER INSTANCE",
        2 => "FAIL OPERATION",
        _ => "CONTINUE",
    };

    /// <summary>A copy of the definition, a running audit's run left out, for a rollback to restore.</summary>
    public ServerAudit Clone()
    {
        var copy = (ServerAudit)this.MemberwiseClone();
        copy.Running = null;
        return copy;
    }

    /// <summary>Takes <paramref name="saved"/>'s definition back, keeping the run in progress.</summary>
    public void Restore(ServerAudit saved)
    {
        this.Name = saved.Name;
        this.ModifyDate = saved.ModifyDate;
        this.Destination = saved.Destination;
        this.OnFailure = saved.OnFailure;
        this.QueueDelay = saved.QueueDelay;
        this.PredicateText = saved.PredicateText;
        this.Predicate = saved.Predicate;
        this.FilePath = saved.FilePath;
        this.MaxSize = saved.MaxSize;
        this.MaxRolloverFiles = saved.MaxRolloverFiles;
        this.MaxFiles = saved.MaxFiles;
        this.ReserveDiskSpace = saved.ReserveDiskSpace;
    }
}

/// <summary>A server audit while it runs: when it started, its stand-in session address and the file it writes.</summary>
internal sealed class RunningAudit(ServerAudit audit, DateTime startTime, byte[] address, AuditFile? file)
{
    public readonly ServerAudit Audit = audit;

    public readonly DateTime StartTime = startTime;

    /// <summary>The address <c>sys.dm_server_audit_status.event_session_address</c> reports, a stand-in like the SQLOS views'.</summary>
    public readonly byte[] Address = address;

    /// <summary>The file a <c>TO FILE</c> audit writes; null for the two event logs, which keep nothing a statement reads.</summary>
    public readonly AuditFile? File = file;
}

/// <summary>
/// One <c>.sqlaudit</c> file: its full path and the records written to it,
/// kept in the simulation's memory rather than on the host's disk, where it
/// outlives its audit as a file on disk does.
/// </summary>
internal sealed class AuditFile(string path)
{
    public readonly string Path = path;

    public readonly Lock Gate = new();

    public readonly List<AuditRecord> Records = [];

    /// <summary>Bytes past the 16 KB header the records take, at the simulator's estimate of real's encoding.</summary>
    public long RecordBytes;

    /// <summary>
    /// The size <c>sys.dm_server_audit_status.audit_file_size</c> reports:
    /// the header and the first 4 KB buffer at the start (20,480 bytes), then
    /// whole 4 KB buffers.
    /// </summary>
    public long Size
    {
        get
        {
            lock (this.Gate)
                return 16384 + (Math.Max(1, (this.RecordBytes + 4095) / 4096) * 4096);
        }
    }
}

/// <summary>
/// One record an audit wrote: its <c>audit_event</c> field values, as the
/// <c>SecAudit</c> package's private event carries them, and the moment it
/// fired.
/// </summary>
internal sealed class AuditRecord(XeValue[] data, DateTime eventTime)
{
    /// <summary>The values of <c>audit_event</c>'s data columns, position for position.</summary>
    public readonly XeValue[] Data = data;

    public readonly DateTime EventTime = eventTime;
}

/// <summary>
/// A server or database audit specification: its catalog row, the audit it
/// writes to (by GUID, which outlives the audit's drop or a restore onto
/// another server) and the actions and groups it names.
/// </summary>
internal sealed class AuditSpecification(int id, string name, DateTime createDate, Guid auditGuid)
{
    public readonly int Id = id;

    public readonly string Name = name;

    public readonly DateTime CreateDate = createDate;

    /// <summary>Never moved: real leaves a specification's at its create date through every <c>ALTER</c> (probed 2026-10-17 against SQL Server 2025).</summary>
    public DateTime ModifyDate = createDate;

    public Guid AuditGuid = auditGuid;

    public bool Enabled;

    /// <summary>The groups and actions, in the order a statement added them.</summary>
    public List<AuditSpecificationDetail> Details = [];

    public AuditSpecification Clone()
    {
        var copy = (AuditSpecification)this.MemberwiseClone();
        copy.Details = [.. this.Details];
        return copy;
    }

    public void Restore(AuditSpecification saved)
    {
        this.ModifyDate = saved.ModifyDate;
        this.AuditGuid = saved.AuditGuid;
        this.Enabled = saved.Enabled;
        this.Details = [.. saved.Details];
    }
}

/// <summary>
/// One group or action a specification names: the action, and for an action
/// of a database specification the securable it watches — the database
/// (class 0), a schema (3) or an object (1) — and the principal it watches
/// for (0 for <c>public</c>, everyone).
/// </summary>
internal readonly struct AuditSpecificationDetail(AuditAction action, byte securableClass, int majorId, int principalId) : IEquatable<AuditSpecificationDetail>
{
    public readonly AuditAction Action = action;

    /// <summary>100 for a server specification's group; 0, 1 or 3 in a database specification.</summary>
    public readonly byte SecurableClass = securableClass;

    public readonly int MajorId = majorId;

    public readonly int PrincipalId = principalId;

    public string ClassDesc => this.SecurableClass switch
    {
        1 => "OBJECT_OR_COLUMN",
        3 => "SCHEMA",
        100 => "SERVER",
        _ => "DATABASE",
    };

    public bool Equals(AuditSpecificationDetail other) =>
        this.Action.Id == other.Action.Id && this.Action.IsGroup == other.Action.IsGroup && this.SecurableClass == other.SecurableClass
        && this.MajorId == other.MajorId && this.PrincipalId == other.PrincipalId;

    public override bool Equals(object? obj) => obj is AuditSpecificationDetail other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Action.Id, this.SecurableClass, this.MajorId, this.PrincipalId);
}
