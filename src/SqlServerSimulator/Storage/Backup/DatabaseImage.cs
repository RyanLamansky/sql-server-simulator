using System.IO.Compression;
using System.Text;

namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// Everything a backup set carries of one database: its settings, files,
/// principals, permissions and extended properties as values; its schema
/// objects as the T-SQL that recreates them (<see cref="Steps"/>), each with
/// the object ids it allocates; the state that T-SQL can't set, applied
/// directly once the objects exist; and every table's rows.
/// </summary>
/// <remarks>
/// Built from a live database by <see cref="Capture(Database, BrokerRuntime)"/> and replayed into a new
/// one by <see cref="Populate"/>. Recreating objects through the engine's own
/// DDL, rather than copying them, is what rebinds every parsed expression —
/// a default, a computed column, a module body — to the restored database's
/// objects.
/// </remarks>
internal sealed partial class DatabaseImage
{
    public string DatabaseName = "";
    public string CollationName = "";
    public string OwnerLoginName = "";
    public Guid DatabaseGuid;
    public Guid RecoveryForkGuid;
    public int CompatibilityLevel;
    public bool AllowSnapshotIsolation;
    public bool ReadCommittedSnapshot;
    public bool RecursiveTriggers;
    public bool Trustworthy;
    public bool CrossDatabaseChaining;
    public bool IsReadOnly;
    public bool BrokerEnabled;
    public bool FullTextDisabled;
    public byte RecoveryModel;
    public byte PageVerify;
    public byte DelayedDurability;
    public byte FileStreamNonTransactedAccess;
    public byte UserAccess;
    public int Switches;
    public int TargetRecoveryTimeSeconds;

    /// <summary>The ticks of the last good <c>DBCC CHECKDB</c>, 0 for none: real keeps it on the boot page a backup carries.</summary>
    public long LastGoodCheckDbTicks;
    public int DefaultFilegroupId;
    public int MemoryOptimizedFilegroupId;
    public string? FileStreamDirectoryName;
    public long[] Counters = [];
    public string[] ScopedPrimary = [];
    public string?[] ScopedSecondary = [];
    public QueryStoreOptions QueryStore = new();
    public readonly List<DatabaseFile> Files = [];
    public readonly List<(string Name, int Id)> Filegroups = [];
    public int[] ReadOnlyFilegroups = [];
    public int[] AutogrowAllFilesFilegroups = [];
    public readonly List<(int SchemaId, int OwnerId)> FixedRoleSchemaOwners = [];
    public readonly List<(string Name, int SchemaId, int PrincipalId)> Schemas = [];
    public readonly List<DatabasePrincipal> Principals = [];
    public readonly List<DatabasePermission> Permissions = [];
    public readonly List<(int RoleId, int MemberId)> RoleMembers = [];
    public readonly List<(ExtendedPropertyKey Key, SqlValue Value)> ExtendedProperties = [];
    public readonly List<ReplayStep> Steps = [];
    public readonly List<TableImage> Tables = [];
    public readonly List<ObjectImage> Objects = [];
    public readonly List<SequenceImage> Sequences = [];
    public readonly List<TypeImage> Types = [];

    /// <summary>The image deflated, as a backup set's payload.</summary>
    public byte[] ToPayload()
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true))
            this.Write(writer);
        return buffer.ToArray();
    }

    /// <summary>Inflates a backup set's payload; a damaged one raises <see cref="InvalidDataException"/>.</summary>
    public static DatabaseImage FromPayload(byte[] payload)
    {
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(payload, writable: false), CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate, Encoding.UTF8);
            return Read(reader);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The backup set's payload is damaged.", ex);
        }
    }

    private void Write(BinaryWriter w)
    {
        w.Write(this.DatabaseName);
        w.Write(this.CollationName);
        w.Write(this.OwnerLoginName);
        w.Write(this.DatabaseGuid.ToByteArray());
        w.Write(this.RecoveryForkGuid.ToByteArray());
        w.Write(this.CompatibilityLevel);
        w.Write(this.AllowSnapshotIsolation);
        w.Write(this.ReadCommittedSnapshot);
        w.Write(this.RecursiveTriggers);
        w.Write(this.Trustworthy);
        w.Write(this.CrossDatabaseChaining);
        w.Write(this.IsReadOnly);
        w.Write(this.BrokerEnabled);
        w.Write(this.FullTextDisabled);
        w.Write(this.RecoveryModel);
        w.Write(this.PageVerify);
        w.Write(this.DelayedDurability);
        w.Write(this.FileStreamNonTransactedAccess);
        w.Write(this.UserAccess);
        w.Write(this.Switches);
        w.Write(this.TargetRecoveryTimeSeconds);
        w.Write(this.LastGoodCheckDbTicks);
        w.Write(this.DefaultFilegroupId);
        w.Write(this.MemoryOptimizedFilegroupId);
        BackupBinary.WriteNullable(w, this.FileStreamDirectoryName);
        w.Write(this.Counters.Length);
        foreach (var counter in this.Counters)
            w.Write(counter);
        w.Write(this.ScopedPrimary.Length);
        for (var i = 0; i < this.ScopedPrimary.Length; i++)
        {
            w.Write(this.ScopedPrimary[i]);
            BackupBinary.WriteNullable(w, this.ScopedSecondary[i]);
        }
        WriteQueryStore(w, this.QueryStore);

        w.Write(this.Files.Count);
        foreach (var file in this.Files)
        {
            w.Write(file.FileId);
            w.Write(file.IsLog);
            w.Write(file.Name);
            w.Write(file.PhysicalName);
            w.Write(file.DataSpaceId);
            w.Write(file.SizePages);
            w.Write(file.MaxSizePages);
            w.Write(file.Growth);
            w.Write(file.IsPercentGrowth);
            w.Write(file.IsContainer);
        }
        w.Write(this.Filegroups.Count);
        foreach (var (name, id) in this.Filegroups)
        {
            w.Write(name);
            w.Write(id);
        }
        BackupBinary.WriteInts(w, this.ReadOnlyFilegroups);
        BackupBinary.WriteInts(w, this.AutogrowAllFilesFilegroups);
        w.Write(this.FixedRoleSchemaOwners.Count);
        foreach (var (schemaId, ownerId) in this.FixedRoleSchemaOwners)
        {
            w.Write(schemaId);
            w.Write(ownerId);
        }
        w.Write(this.Schemas.Count);
        foreach (var (name, schemaId, principalId) in this.Schemas)
        {
            w.Write(name);
            w.Write(schemaId);
            w.Write(principalId);
        }
        w.Write(this.Principals.Count);
        foreach (var p in this.Principals)
        {
            w.Write(p.PrincipalId);
            w.Write(p.Name);
            w.Write(p.TypeCode);
            w.Write(p.TypeDescription);
            w.Write(p.IsFixedRole);
            w.Write(p.CreateDate.Ticks);
            BackupBinary.WriteNullable(w, p.LoginName);
            BackupBinary.WriteNullable(w, p.SecurityIdentifierString);
            w.Write(p.LoginPrincipalId);
            BackupBinary.WriteNullable(w, p.DefaultSchemaName);
            w.Write(p.PasswordHash is not null);
            if (p.PasswordHash is { } hash)
            {
                w.Write(hash.Length);
                w.Write(hash);
            }
            w.Write(p.OwningPrincipalId);
            w.Write(p.LoginSid is not null);
            if (p.LoginSid is { } loginSid)
            {
                w.Write(loginSid.Length);
                w.Write(loginSid);
            }
        }
        w.Write(this.Permissions.Count);
        foreach (var p in this.Permissions)
        {
            w.Write(p.Class);
            w.Write(p.MajorId);
            w.Write(p.MinorId);
            w.Write(p.GranteePrincipalId);
            w.Write(p.GrantorPrincipalId);
            w.Write((byte)p.Permission);
            w.Write((byte)p.State);
            BackupBinary.WriteNullable(w, p.PermissionName);
        }
        w.Write(this.RoleMembers.Count);
        foreach (var (roleId, memberId) in this.RoleMembers)
        {
            w.Write(roleId);
            w.Write(memberId);
        }
        w.Write(this.ExtendedProperties.Count);
        foreach (var (key, value) in this.ExtendedProperties)
        {
            w.Write(key.Class);
            w.Write(key.MajorId);
            w.Write(key.MinorId);
            w.Write(key.Name);
            BackupBinary.WriteVariant(w, value);
        }
        w.Write(this.Steps.Count);
        foreach (var step in this.Steps)
            step.Write(w);
        w.Write(this.Tables.Count);
        foreach (var table in this.Tables)
            table.Write(w);
        w.Write(this.Objects.Count);
        foreach (var obj in this.Objects)
            obj.Write(w);
        w.Write(this.Sequences.Count);
        foreach (var sequence in this.Sequences)
            sequence.Write(w);
        w.Write(this.Types.Count);
        foreach (var type in this.Types)
            type.Write(w);
        this.WriteKeys(w);
        this.WritePlanGuides(w);
        this.WritePartitioning(w);
        this.WriteBroker(w);
        this.WriteAuditSpecifications(w);
    }

    private static DatabaseImage Read(BinaryReader r)
    {
        var image = new DatabaseImage
        {
            DatabaseName = r.ReadString(),
            CollationName = r.ReadString(),
            OwnerLoginName = r.ReadString(),
            DatabaseGuid = new Guid(r.ReadBytes(16)),
            RecoveryForkGuid = new Guid(r.ReadBytes(16)),
            CompatibilityLevel = r.ReadInt32(),
            AllowSnapshotIsolation = r.ReadBoolean(),
            ReadCommittedSnapshot = r.ReadBoolean(),
            RecursiveTriggers = r.ReadBoolean(),
            Trustworthy = r.ReadBoolean(),
            CrossDatabaseChaining = r.ReadBoolean(),
            IsReadOnly = r.ReadBoolean(),
            BrokerEnabled = r.ReadBoolean(),
            FullTextDisabled = r.ReadBoolean(),
            RecoveryModel = r.ReadByte(),
            PageVerify = r.ReadByte(),
            DelayedDurability = r.ReadByte(),
            FileStreamNonTransactedAccess = r.ReadByte(),
            UserAccess = r.ReadByte(),
            Switches = r.ReadInt32(),
            TargetRecoveryTimeSeconds = r.ReadInt32(),
            LastGoodCheckDbTicks = r.ReadInt64(),
            DefaultFilegroupId = r.ReadInt32(),
            MemoryOptimizedFilegroupId = r.ReadInt32(),
            FileStreamDirectoryName = BackupBinary.ReadNullable(r),
            Counters = ReadCounters(r),
        };
        var scoped = r.ReadInt32();
        image.ScopedPrimary = new string[scoped];
        image.ScopedSecondary = new string?[scoped];
        for (var i = 0; i < scoped; i++)
        {
            image.ScopedPrimary[i] = r.ReadString();
            image.ScopedSecondary[i] = BackupBinary.ReadNullable(r);
        }
        image.QueryStore = ReadQueryStore(r);

        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var fileId = r.ReadInt32();
            var isLog = r.ReadBoolean();
            var name = r.ReadString();
            var physical = r.ReadString();
            var dataSpace = r.ReadInt32();
            var size = r.ReadInt32();
            var maxSize = r.ReadInt32();
            var growth = r.ReadInt32();
            var percent = r.ReadBoolean();
            var container = r.ReadBoolean();
            image.Files.Add(new DatabaseFile(fileId, isLog, name, physical, dataSpace, size, maxSize, growth, percent, container));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Filegroups.Add((r.ReadString(), r.ReadInt32()));
        image.ReadOnlyFilegroups = BackupBinary.ReadInts(r);
        image.AutogrowAllFilesFilegroups = BackupBinary.ReadInts(r);
        for (var i = r.ReadInt32(); i > 0; i--)
            image.FixedRoleSchemaOwners.Add((r.ReadInt32(), r.ReadInt32()));
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Schemas.Add((r.ReadString(), r.ReadInt32(), r.ReadInt32()));
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var principalId = r.ReadInt32();
            var name = r.ReadString();
            var typeCode = r.ReadString();
            var typeDescription = r.ReadString();
            var isFixedRole = r.ReadBoolean();
            var createDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var loginName = BackupBinary.ReadNullable(r);
            var sid = BackupBinary.ReadNullable(r);
            image.Principals.Add(new DatabasePrincipal(principalId, name, typeCode, typeDescription, isFixedRole, createDate, loginName, sid)
            {
                LoginPrincipalId = r.ReadInt32(),
                DefaultSchemaName = BackupBinary.ReadNullable(r),
                PasswordHash = r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null,
                OwningPrincipalId = r.ReadInt32(),
                LoginSid = r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null,
            });
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var @class = r.ReadByte();
            var majorId = r.ReadInt32();
            var minorId = r.ReadInt32();
            var grantee = r.ReadInt32();
            var grantor = r.ReadInt32();
            var permission = (Permission)r.ReadByte();
            var state = (PermissionState)r.ReadByte();
            image.Permissions.Add(new DatabasePermission(@class, majorId, minorId, grantee, grantor, permission, state, BackupBinary.ReadNullable(r)));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
            image.RoleMembers.Add((r.ReadInt32(), r.ReadInt32()));
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var key = new ExtendedPropertyKey(r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadString());
            image.ExtendedProperties.Add((key, BackupBinary.ReadVariant(r)));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Steps.Add(ReplayStep.Read(r));
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Tables.Add(TableImage.Read(r));
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Objects.Add(ObjectImage.Read(r));
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Sequences.Add(SequenceImage.Read(r));
        for (var i = r.ReadInt32(); i > 0; i--)
            image.Types.Add(TypeImage.Read(r));
        ReadKeys(r, image);
        ReadPlanGuides(r, image);
        ReadPartitioning(r, image);
        ReadBroker(r, image);
        ReadAuditSpecifications(r, image);
        return image;
    }

    private static long[] ReadCounters(BinaryReader r)
    {
        var counters = new long[r.ReadInt32()];
        for (var i = 0; i < counters.Length; i++)
            counters[i] = r.ReadInt64();
        return counters;
    }

    private static void WriteQueryStore(BinaryWriter w, QueryStoreOptions q)
    {
        w.Write((int)q.DesiredState);
        w.Write(q.FlushIntervalSeconds);
        w.Write(q.IntervalLengthMinutes);
        w.Write(q.MaxStorageSizeMb);
        w.Write(q.StaleQueryThresholdDays);
        w.Write(q.MaxPlansPerQuery);
        w.Write((int)q.CaptureMode);
        w.Write(q.SizeBasedCleanupAuto);
        w.Write(q.WaitStatsCaptureOn);
        w.Write(q.CapturePolicyExecutionCount);
        w.Write(q.CapturePolicyTotalCompileCpuTimeMs);
        w.Write(q.CapturePolicyTotalExecutionCpuTimeMs);
        w.Write(q.CapturePolicyStaleThresholdHours);
    }

    private static QueryStoreOptions ReadQueryStore(BinaryReader r) => new()
    {
        DesiredState = (QueryStoreState)r.ReadInt32(),
        FlushIntervalSeconds = r.ReadInt64(),
        IntervalLengthMinutes = r.ReadInt64(),
        MaxStorageSizeMb = r.ReadInt64(),
        StaleQueryThresholdDays = r.ReadInt64(),
        MaxPlansPerQuery = r.ReadInt64(),
        CaptureMode = (QueryStoreCaptureMode)r.ReadInt32(),
        SizeBasedCleanupAuto = r.ReadBoolean(),
        WaitStatsCaptureOn = r.ReadBoolean(),
        CapturePolicyExecutionCount = r.ReadInt32(),
        CapturePolicyTotalCompileCpuTimeMs = r.ReadInt64(),
        CapturePolicyTotalExecutionCpuTimeMs = r.ReadInt64(),
        CapturePolicyStaleThresholdHours = r.ReadInt32(),
    };
}

/// <summary>Which pass of the replay a <see cref="ReplayStep"/> runs in.</summary>
internal enum ReplayPhase : byte
{
    /// <summary>Types, sequences, tables, indexes, modules: in object-id order, a step failing for a missing dependency retried after the rest.</summary>
    Definitions,

    /// <summary>Foreign keys, once every key they reference exists.</summary>
    ForeignKeys,

    /// <summary>
    /// Standalone statistics, which queries create as they compile and so a
    /// database restored in place may hold more or fewer of than its backup.
    /// </summary>
    Statistics,
}

/// <summary>
/// One statement the replay runs in the restored database, under the
/// <c>QUOTED_IDENTIFIER</c> / <c>ANSI_NULLS</c> its object was created with,
/// at the creation instant it carries, handing out the ids it lists.
/// </summary>
internal sealed class ReplayStep
{
    public string Sql = "";
    public ReplayPhase Phase;

    /// <summary>The order the step runs in within its phase: the id of the object it creates.</summary>
    public int SortKey;

    public int[] ObjectIds = [];
    public int[] UserTypeIds = [];
    public int[] XmlCollectionIds = [];
    public bool QuotedIdentifier = true;
    public bool AnsiNulls = true;
    public DateTime CreateDate;

    /// <summary>The schema an unqualified name in <see cref="Sql"/> creates in: a module's own, which its verbatim definition may not name.</summary>
    public string? DefaultSchema;

    /// <summary>
    /// Whether the definition may draw ids beyond <see cref="ObjectIds"/> ahead of
    /// them — a module whose body binds a table-valued parameter, for one — so
    /// the replay runs it in a transaction it can roll back and run again with
    /// those draws accounted for.
    /// </summary>
    public bool MayDrawMoreIds;

    public void Write(BinaryWriter w)
    {
        w.Write(this.Sql);
        BackupBinary.WriteNullable(w, this.DefaultSchema);
        w.Write(this.MayDrawMoreIds);
        w.Write((byte)this.Phase);
        w.Write(this.SortKey);
        BackupBinary.WriteInts(w, this.ObjectIds);
        BackupBinary.WriteInts(w, this.UserTypeIds);
        BackupBinary.WriteInts(w, this.XmlCollectionIds);
        w.Write(this.QuotedIdentifier);
        w.Write(this.AnsiNulls);
        w.Write(this.CreateDate.Ticks);
    }

    public static ReplayStep Read(BinaryReader r) => new()
    {
        Sql = r.ReadString(),
        DefaultSchema = BackupBinary.ReadNullable(r),
        MayDrawMoreIds = r.ReadBoolean(),
        Phase = (ReplayPhase)r.ReadByte(),
        SortKey = r.ReadInt32(),
        ObjectIds = BackupBinary.ReadInts(r),
        UserTypeIds = BackupBinary.ReadInts(r),
        XmlCollectionIds = BackupBinary.ReadInts(r),
        QuotedIdentifier = r.ReadBoolean(),
        AnsiNulls = r.ReadBoolean(),
        CreateDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
    };
}

/// <summary>
/// The state of a key constraint or index its DDL doesn't set, applied once
/// it exists, and whether its statistic had been built.
/// </summary>
internal struct IndexState
{
    public string Name;
    public int IndexId;
    public byte FillFactor;
    public bool IsPadded;
    public bool AllowRowLocks;
    public bool AllowPageLocks;
    public bool OptimizeForSequentialKey;
    public byte DataCompression;
    public bool XmlCompression;
    public bool StatisticsNoRecompute;
    public bool IsDisabled;
    public int FilegroupId;
    public bool KeyMayExceedLimit;
    public bool IgnoreDupKey;
    public bool AutoDrop;
    public bool HasPersistedSample;
    public bool HasStatistics;
    public DateTime ModifyDate;
    public PlacementImage Placement;

    public readonly void Write(BinaryWriter w)
    {
        w.Write(this.Name);
        w.Write(this.IndexId);
        w.Write(this.FillFactor);
        w.Write(this.IsPadded);
        w.Write(this.AllowRowLocks);
        w.Write(this.AllowPageLocks);
        w.Write(this.OptimizeForSequentialKey);
        w.Write(this.DataCompression);
        w.Write(this.XmlCompression);
        w.Write(this.StatisticsNoRecompute);
        w.Write(this.IsDisabled);
        w.Write(this.FilegroupId);
        w.Write(this.KeyMayExceedLimit);
        w.Write(this.IgnoreDupKey);
        w.Write(this.AutoDrop);
        w.Write(this.HasPersistedSample);
        w.Write(this.HasStatistics);
        w.Write(this.ModifyDate.Ticks);
        this.Placement.Write(w);
    }

    public static IndexState Read(BinaryReader r) => new()
    {
        Name = r.ReadString(),
        IndexId = r.ReadInt32(),
        FillFactor = r.ReadByte(),
        IsPadded = r.ReadBoolean(),
        AllowRowLocks = r.ReadBoolean(),
        AllowPageLocks = r.ReadBoolean(),
        OptimizeForSequentialKey = r.ReadBoolean(),
        DataCompression = r.ReadByte(),
        XmlCompression = r.ReadBoolean(),
        StatisticsNoRecompute = r.ReadBoolean(),
        IsDisabled = r.ReadBoolean(),
        FilegroupId = r.ReadInt32(),
        KeyMayExceedLimit = r.ReadBoolean(),
        IgnoreDupKey = r.ReadBoolean(),
        AutoDrop = r.ReadBoolean(),
        HasPersistedSample = r.ReadBoolean(),
        HasStatistics = r.ReadBoolean(),
        ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
        Placement = PlacementImage.Read(r),
    };
}

/// <summary>A CHECK, DEFAULT or FOREIGN KEY constraint's state its DDL doesn't set.</summary>
internal struct ConstraintState
{
    /// <summary>The constraint's name; for a DEFAULT, its column's.</summary>
    public string Name;
    public bool IsSystemNamed;
    public bool IsNotTrusted;
    public bool NotForReplication;
    public bool IsDisabled;
    public string? Definition;
    public DateTime ModifyDate;

    public readonly void Write(BinaryWriter w)
    {
        w.Write(this.Name);
        w.Write(this.IsSystemNamed);
        w.Write(this.IsNotTrusted);
        w.Write(this.NotForReplication);
        w.Write(this.IsDisabled);
        BackupBinary.WriteNullable(w, this.Definition);
        w.Write(this.ModifyDate.Ticks);
    }

    public static ConstraintState Read(BinaryReader r) => new()
    {
        Name = r.ReadString(),
        IsSystemNamed = r.ReadBoolean(),
        IsNotTrusted = r.ReadBoolean(),
        NotForReplication = r.ReadBoolean(),
        IsDisabled = r.ReadBoolean(),
        Definition = BackupBinary.ReadNullable(r),
        ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
    };
}

/// <summary>A standalone statistic: created by its step, then rebuilt under its own id and flags.</summary>
internal struct StatisticState
{
    public string Name;
    public int StatsId;
    public bool AutoCreated;
    public bool NoRecompute;
    public bool AutoDrop;
    public bool HasPersistedSample;
    public bool HasStatistics;
    public DateTime CreateDate;
    public int[] Columns;
    public string? FilterDefinition;

    public readonly void Write(BinaryWriter w)
    {
        w.Write(this.Name);
        BackupBinary.WriteInts(w, this.Columns);
        BackupBinary.WriteNullable(w, this.FilterDefinition);
        w.Write(this.StatsId);
        w.Write(this.AutoCreated);
        w.Write(this.NoRecompute);
        w.Write(this.AutoDrop);
        w.Write(this.HasPersistedSample);
        w.Write(this.HasStatistics);
        w.Write(this.CreateDate.Ticks);
    }

    public static StatisticState Read(BinaryReader r) => new()
    {
        Name = r.ReadString(),
        Columns = BackupBinary.ReadInts(r),
        FilterDefinition = BackupBinary.ReadNullable(r),
        StatsId = r.ReadInt32(),
        AutoCreated = r.ReadBoolean(),
        NoRecompute = r.ReadBoolean(),
        AutoDrop = r.ReadBoolean(),
        HasPersistedSample = r.ReadBoolean(),
        HasStatistics = r.ReadBoolean(),
        CreateDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
    };
}

/// <summary>A user table: the state its DDL doesn't set, and its rows.</summary>
internal sealed class TableImage
{
    public string SchemaName = "";
    public string Name = "";
    public int ObjectId;
    public DateTime ModifyDate;
    public int? OwnerPrincipalId;
    public bool UsesAnsiNulls;
    public byte LockEscalation;
    public int FilegroupId;
    public int LobFilegroupId;
    public byte HeapDataCompression;
    public bool HeapXmlCompression;
    public bool PageCompressed;
    public int TextInRowLimit;
    public bool LargeValueTypesOutOfRow;
    public bool LockOnBulkLoad;
    public bool HeapAllowRowLocks;
    public bool HeapAllowPageLocks;
    public bool KeysMayExceedLimit;
    public int MaxColumnIdUsed;

    /// <summary>The base rows' placement on a partition scheme, and a heap's per-partition compression.</summary>
    public PlacementImage Placement;

    /// <summary>Per column, in order: its id, ANSI_PADDING capture and untyped-NULL mark.</summary>
    public readonly List<(int ColumnId, bool IsAnsiPaddingOff, bool IsUntypedNull)> Columns = [];

    public Int128? IdentityHighWaterMark;
    public Int128? IdentityReseededStart;
    public readonly List<IndexState> Keys = [];
    public readonly List<IndexState> Indexes = [];
    public readonly List<ConstraintState> Checks = [];
    public readonly List<ConstraintState> Defaults = [];
    public readonly List<ConstraintState> ForeignKeys = [];
    public readonly List<StatisticState> Statistics = [];

    /// <summary>The rows, each stored column value as <see cref="BackupRowCodec"/> writes it.</summary>
    public byte[] Rows = [];
    public int RowCount;

    public void Write(BinaryWriter w)
    {
        w.Write(this.SchemaName);
        w.Write(this.Name);
        w.Write(this.ObjectId);
        w.Write(this.ModifyDate.Ticks);
        BackupBinary.WriteNullableInt(w, this.OwnerPrincipalId);
        w.Write(this.UsesAnsiNulls);
        w.Write(this.LockEscalation);
        w.Write(this.FilegroupId);
        w.Write(this.LobFilegroupId);
        w.Write(this.HeapDataCompression);
        w.Write(this.HeapXmlCompression);
        w.Write(this.PageCompressed);
        w.Write(this.TextInRowLimit);
        w.Write(this.LargeValueTypesOutOfRow);
        w.Write(this.LockOnBulkLoad);
        w.Write(this.HeapAllowRowLocks);
        w.Write(this.HeapAllowPageLocks);
        w.Write(this.KeysMayExceedLimit);
        w.Write(this.MaxColumnIdUsed);
        this.Placement.Write(w);
        w.Write(this.Columns.Count);
        foreach (var (columnId, paddingOff, untypedNull) in this.Columns)
        {
            w.Write(columnId);
            w.Write(paddingOff);
            w.Write(untypedNull);
        }
        BackupBinary.WriteNullableInt128(w, this.IdentityHighWaterMark);
        BackupBinary.WriteNullableInt128(w, this.IdentityReseededStart);
        WriteList(w, this.Keys, static (writer, state) => state.Write(writer));
        WriteList(w, this.Indexes, static (writer, state) => state.Write(writer));
        WriteList(w, this.Checks, static (writer, state) => state.Write(writer));
        WriteList(w, this.Defaults, static (writer, state) => state.Write(writer));
        WriteList(w, this.ForeignKeys, static (writer, state) => state.Write(writer));
        WriteList(w, this.Statistics, static (writer, state) => state.Write(writer));
        w.Write(this.RowCount);
        w.Write(this.Rows.Length);
        w.Write(this.Rows);
    }

    private static void WriteList<T>(BinaryWriter w, List<T> list, Action<BinaryWriter, T> write)
    {
        w.Write(list.Count);
        foreach (var item in list)
            write(w, item);
    }

    private static void ReadList<T>(BinaryReader r, List<T> list, Func<BinaryReader, T> read)
    {
        for (var i = r.ReadInt32(); i > 0; i--)
            list.Add(read(r));
    }

    public static TableImage Read(BinaryReader r)
    {
        var table = new TableImage
        {
            SchemaName = r.ReadString(),
            Name = r.ReadString(),
            ObjectId = r.ReadInt32(),
            ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
            OwnerPrincipalId = BackupBinary.ReadNullableInt(r),
            UsesAnsiNulls = r.ReadBoolean(),
            LockEscalation = r.ReadByte(),
            FilegroupId = r.ReadInt32(),
            LobFilegroupId = r.ReadInt32(),
            HeapDataCompression = r.ReadByte(),
            HeapXmlCompression = r.ReadBoolean(),
            PageCompressed = r.ReadBoolean(),
            TextInRowLimit = r.ReadInt32(),
            LargeValueTypesOutOfRow = r.ReadBoolean(),
            LockOnBulkLoad = r.ReadBoolean(),
            HeapAllowRowLocks = r.ReadBoolean(),
            HeapAllowPageLocks = r.ReadBoolean(),
            KeysMayExceedLimit = r.ReadBoolean(),
            MaxColumnIdUsed = r.ReadInt32(),
            Placement = PlacementImage.Read(r),
        };
        for (var i = r.ReadInt32(); i > 0; i--)
            table.Columns.Add((r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean()));
        table.IdentityHighWaterMark = BackupBinary.ReadNullableInt128(r);
        table.IdentityReseededStart = BackupBinary.ReadNullableInt128(r);
        ReadList(r, table.Keys, IndexState.Read);
        ReadList(r, table.Indexes, IndexState.Read);
        ReadList(r, table.Checks, ConstraintState.Read);
        ReadList(r, table.Defaults, ConstraintState.Read);
        ReadList(r, table.ForeignKeys, ConstraintState.Read);
        ReadList(r, table.Statistics, StatisticState.Read);
        table.RowCount = r.ReadInt32();
        table.Rows = r.ReadBytes(r.ReadInt32());
        return table;
    }
}

/// <summary>
/// A module, synonym, table type or other schema object's state its DDL
/// doesn't set: owner, modification instant, the principal its
/// <c>EXECUTE AS</c> resolved to, a trigger's switches and a procedure's
/// startup flag.
/// </summary>
internal sealed class ObjectImage
{
    /// <summary>The object's schema, or null for a database DDL trigger.</summary>
    public string? SchemaName;
    public string Name = "";
    public int ObjectId;
    public DateTime ModifyDate;
    public int? OwnerPrincipalId;
    public int? ExecuteAsPrincipalId;
    public bool IsDisabled;
    public bool NotForReplication;
    public int FirstForActions;
    public int LastForActions;
    public bool IsStartup;

    public void Write(BinaryWriter w)
    {
        BackupBinary.WriteNullable(w, this.SchemaName);
        w.Write(this.Name);
        w.Write(this.ObjectId);
        w.Write(this.ModifyDate.Ticks);
        BackupBinary.WriteNullableInt(w, this.OwnerPrincipalId);
        BackupBinary.WriteNullableInt(w, this.ExecuteAsPrincipalId);
        w.Write(this.IsDisabled);
        w.Write(this.NotForReplication);
        w.Write(this.FirstForActions);
        w.Write(this.LastForActions);
        w.Write(this.IsStartup);
    }

    public static ObjectImage Read(BinaryReader r) => new()
    {
        SchemaName = BackupBinary.ReadNullable(r),
        Name = r.ReadString(),
        ObjectId = r.ReadInt32(),
        ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
        OwnerPrincipalId = BackupBinary.ReadNullableInt(r),
        ExecuteAsPrincipalId = BackupBinary.ReadNullableInt(r),
        IsDisabled = r.ReadBoolean(),
        NotForReplication = r.ReadBoolean(),
        FirstForActions = r.ReadInt32(),
        LastForActions = r.ReadInt32(),
        IsStartup = r.ReadBoolean(),
    };
}

/// <summary>A sequence's settings and position, which its <c>CREATE SEQUENCE</c> shell doesn't carry.</summary>
internal sealed class SequenceImage
{
    public string SchemaName = "";
    public string Name = "";
    public int ObjectId;
    public Int128 StartValue;
    public Int128 Increment;
    public Int128 MinValue;
    public Int128 MaxValue;
    public bool Cycle;
    public long? CacheSize;
    public Int128 CurrentValue;
    public bool IsExhausted;
    public Int128? LastUsedValue;

    public void Write(BinaryWriter w)
    {
        w.Write(this.SchemaName);
        w.Write(this.Name);
        w.Write(this.ObjectId);
        BackupBinary.WriteInt128(w, this.StartValue);
        BackupBinary.WriteInt128(w, this.Increment);
        BackupBinary.WriteInt128(w, this.MinValue);
        BackupBinary.WriteInt128(w, this.MaxValue);
        w.Write(this.Cycle);
        w.Write(this.CacheSize.HasValue);
        if (this.CacheSize is { } cache)
            w.Write(cache);
        BackupBinary.WriteInt128(w, this.CurrentValue);
        w.Write(this.IsExhausted);
        BackupBinary.WriteNullableInt128(w, this.LastUsedValue);
    }

    public static SequenceImage Read(BinaryReader r) => new()
    {
        SchemaName = r.ReadString(),
        Name = r.ReadString(),
        ObjectId = r.ReadInt32(),
        StartValue = BackupBinary.ReadInt128(r),
        Increment = BackupBinary.ReadInt128(r),
        MinValue = BackupBinary.ReadInt128(r),
        MaxValue = BackupBinary.ReadInt128(r),
        Cycle = r.ReadBoolean(),
        CacheSize = r.ReadBoolean() ? r.ReadInt64() : null,
        CurrentValue = BackupBinary.ReadInt128(r),
        IsExhausted = r.ReadBoolean(),
        LastUsedValue = BackupBinary.ReadNullableInt128(r),
    };
}

/// <summary>An alias type's or XML schema collection's owner and dates, keyed by its type id.</summary>
internal sealed class TypeImage
{
    /// <summary>The alias type's <c>user_type_id</c>, or the collection's <c>xml_collection_id</c>.</summary>
    public int Id;
    public bool IsXmlSchemaCollection;
    public int? OwnerPrincipalId;
    public DateTime ModifyDate;

    /// <summary>
    /// A collection's segments as document count and first component id
    /// pairs, which the replay's single <c>CREATE</c> can't recreate.
    /// </summary>
    public int[] XmlComponentSegments = [];

    public void Write(BinaryWriter w)
    {
        w.Write(this.Id);
        w.Write(this.IsXmlSchemaCollection);
        BackupBinary.WriteNullableInt(w, this.OwnerPrincipalId);
        w.Write(this.ModifyDate.Ticks);
        BackupBinary.WriteInts(w, this.XmlComponentSegments);
    }

    public static TypeImage Read(BinaryReader r) => new()
    {
        Id = r.ReadInt32(),
        IsXmlSchemaCollection = r.ReadBoolean(),
        OwnerPrincipalId = BackupBinary.ReadNullableInt(r),
        ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
        XmlComponentSegments = BackupBinary.ReadInts(r),
    };
}
