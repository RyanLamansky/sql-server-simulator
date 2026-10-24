using System.Buffers.Binary;
using System.Collections.Concurrent;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

/// <summary>
/// The <c>objtype</c> of a <c>sys.dm_exec_cached_plans</c> row: an ad hoc
/// batch, a prepared one (a parameterized command, <c>sp_executesql</c> with
/// declarations, or a statement simple parameterization rewrote), or a
/// module's body.
/// </summary>
internal enum CachedPlanKind : byte
{
    Adhoc,
    Prepared,
    Proc,
    Trigger,
}

/// <summary>Which cached plan a batch's statements are recorded under (<see cref="Parser.BatchContext.CachedPlanOrigin"/>).</summary>
internal enum CachedPlanOrigin : byte
{
    /// <summary>A batch real compiles into no plan of its own: a compile walk, an internal batch, an inlined body.</summary>
    None,

    /// <summary>A client's batch: ad hoc, or prepared when it carries parameters.</summary>
    ClientBatch,

    /// <summary>An <c>EXEC('…')</c> string, an ad hoc batch of its own.</summary>
    DynamicBatch,

    /// <summary>An <c>sp_executesql</c> statement, prepared when it declares parameters.</summary>
    ExecuteSql,

    /// <summary>A module body, whose call set its plan.</summary>
    Module,
}

/// <summary>
/// What <c>sys.dm_exec_requests</c> reports of a running statement — see
/// <c>Simulation.ReportedStatement</c>.
/// </summary>
internal readonly struct RunningStatement(string text, int startOffset, int endOffset, byte[] sqlHandle, byte[] planHandle)
{
    /// <summary>The text <see cref="SqlHandle"/> names, which the offsets index.</summary>
    public readonly string Text = text;

    public readonly int StartOffset = startOffset;
    public readonly int EndOffset = endOffset;
    public readonly byte[] SqlHandle = sqlHandle;
    public readonly byte[] PlanHandle = planHandle;
}

/// <summary>What a batch's top-level statements have been, for the plan cache's settings-only rule.</summary>
internal enum CachedPlanStatements : byte
{
    None,

    /// <summary>Only <c>SET</c> options and transaction statements.</summary>
    SettingsOnly,

    /// <summary>Something else too.</summary>
    Other,
}

/// <summary>
/// What makes two compiles the same cached plan: the plan's kind, the database
/// it compiled in, the batch text and parameter declaration (or the module's
/// object id), and the plan-affecting session settings — the cache-key
/// attributes <c>sys.dm_exec_plan_attributes</c> lists, as far as the
/// simulator varies them. A prepared batch's declaration is kept apart from
/// its text, so a lookup needn't join the two.
/// </summary>
internal readonly struct CachedPlanKey(CachedPlanKind kind, short databaseId, int objectId, string? text, string? declaration, int setOptions, short languageId, short dateFormat, byte dateFirst)
    : IEquatable<CachedPlanKey>
{
    public readonly CachedPlanKind Kind = kind;
    public readonly short DatabaseId = databaseId;
    public readonly int ObjectId = objectId;
    public readonly string? Text = text;
    public readonly string? Declaration = declaration;
    public readonly int SetOptions = setOptions;
    public readonly short LanguageId = languageId;
    public readonly short DateFormat = dateFormat;
    public readonly byte DateFirst = dateFirst;

    public bool Equals(CachedPlanKey other) =>
        this.Kind == other.Kind && this.DatabaseId == other.DatabaseId && this.ObjectId == other.ObjectId
        && this.SetOptions == other.SetOptions && this.LanguageId == other.LanguageId && this.DateFormat == other.DateFormat
        && this.DateFirst == other.DateFirst && string.Equals(this.Text, other.Text, StringComparison.Ordinal)
        && string.Equals(this.Declaration, other.Declaration, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is CachedPlanKey other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Kind, this.DatabaseId, this.ObjectId, this.Text, this.Declaration, this.SetOptions, this.DateFormat, this.DateFirst);
}

/// <summary>
/// A total, last, minimum and maximum over a plan's or statement's executions,
/// the four columns each <c>sys.dm_exec_*_stats</c> measure reports.
/// </summary>
internal struct PlanCacheMetric
{
    public long Total;
    public long Last;
    public long Min;
    public long Max;

    public void Add(long value, bool first)
    {
        this.Total += value;
        this.Last = value;
        this.Min = first ? value : Math.Min(this.Min, value);
        this.Max = first ? value : Math.Max(this.Max, value);
    }
}

/// <summary>
/// One <c>sys.dm_exec_query_stats</c> row: a statement of a cached plan, placed
/// by its byte offsets in the plan's text, with what its executions measured.
/// Mutated under its plan's lock.
/// </summary>
internal sealed class CachedStatementStats(int startOffset, int endOffset, long planGeneration, DateTime creationTime, string statementText, QueryStoreShape shape, byte parameterizationType, long? contextSettingsId)
{
    public readonly int StartOffset = startOffset;
    public readonly int EndOffset = endOffset;
    public readonly long PlanGeneration = planGeneration;
    public readonly DateTime CreationTime = creationTime;

    /// <summary>The statement's text as Query Store stores it, which its <c>statement_sql_handle</c> hashes.</summary>
    public readonly string StatementText = statementText;

    public readonly QueryStoreShape Shape = shape;
    public readonly byte ParameterizationType = parameterizationType;
    public readonly long? ContextSettingsId = contextSettingsId;

    /// <summary>The <c>statement_sql_handle</c>, hashed the first time something reports it.</summary>
    public byte[] StatementSqlHandle => field ??= Simulation.StatementSqlHandleOf(this.StatementText);

    public DateTime LastExecutionTime = creationTime;
    public long ExecutionCount;
    public PlanCacheMetric WorkerTime;
    public PlanCacheMetric ElapsedTime;
    public PlanCacheMetric LogicalReads;
    public PlanCacheMetric Rows;
}

/// <summary>
/// One <c>sys.dm_exec_cached_plans</c> row — a compiled batch or module — with
/// the statements it holds and, for a procedure, trigger or scalar function,
/// what its calls measured (<c>sys.dm_exec_procedure_stats</c> and siblings).
/// Mutable state is read and written under the instance's own lock.
/// </summary>
internal sealed class CachedPlan
{
    public readonly CachedPlanKey Key;
    public readonly Database Database;

    /// <summary>The batch's text, or the module's definition as the plan compiled it (null for an encrypted module).</summary>
    public readonly string? Text;

    /// <summary>The module the plan compiles, or null for a batch.</summary>
    public readonly SchemaObject? Module;

    /// <summary>
    /// The <c>objectid</c> plan attribute: the module's id, or the ad hoc
    /// object id a batch's text hashes to. A batch's, and the two handles
    /// below, are hashed the first time something reports them, which most
    /// plans of a test run's one-off batches never are.
    /// </summary>
    /// <remarks>
    /// Never 0 once settled (an ad hoc id is at least 1), so 0 means "not
    /// yet"; racing first reads compute the same value.
    /// </remarks>
    public int ObjectId
    {
        get
        {
            if (field == 0)
                field = this.Module?.ObjectId ?? BuiltInResources.AdHocObjectIdOf(this.Text!);
            return field;
        }
    }

    /// <summary>The <c>user_id</c> plan attribute: the default schema one-part names resolved through, -2 when none did.</summary>
    public readonly int UserId;

    public readonly short CompatibilityLevel;

    public byte[] PlanHandle => field ??= PlanHandleOf(this.Key, this.ObjectId);

    public byte[] SqlHandle => field ??= this.Module is null ? BuiltInResources.SqlHandleOf(this.Text!) : ModuleSqlHandle(this.Key.DatabaseId, this.Module);

    public readonly DateTime CachedTime;

    /// <summary>How many times the plan was looked up and used.</summary>
    public int UseCount;

    /// <summary>Set once the plan has left the cache, so a reference held to it is known stale.</summary>
    public volatile bool Removed;

    /// <summary>
    /// The scalar-function call that last counted a use, as the calling
    /// statement's frame and dispatch serial: a function's plan counts one use
    /// per calling statement, however many rows call it.
    /// </summary>
    public long LastUseStatement;

    /// <summary>
    /// The module's compiled body the statements were measured under; a
    /// different one at a call means the body recompiled, which starts the
    /// statements' measures over under a new plan generation.
    /// </summary>
    public object? CompiledBody;

    public long PlanGeneration = 1;
    public readonly List<CachedStatementStats> Statements = [];

    public DateTime LastExecutionTime;
    public long ExecutionCount;
    public PlanCacheMetric WorkerTime;
    public PlanCacheMetric ElapsedTime;
    public PlanCacheMetric LogicalReads;

    public CachedPlan(in CachedPlanKey key, Database database, string? text, SchemaObject? module, int userId, DateTime cachedTime)
    {
        this.Key = key;
        this.Database = database;
        this.Text = text;
        this.Module = module;
        this.UserId = userId;
        this.CompatibilityLevel = (short)database.CompatibilityLevel;
        this.CachedTime = this.LastExecutionTime = cachedTime;
    }

    public bool IsBatch => this.Key.Kind is CachedPlanKind.Adhoc or CachedPlanKind.Prepared;

    /// <summary>The <c>number</c> column <c>sys.dm_exec_sql_text</c> reports: 1 for a procedure, 0 for another module.</summary>
    public short Number => this.Module is Procedure ? (short)1 : (short)0;

    /// <summary>
    /// A plan handle in real's 44-byte shape: the type byte (6 for a batch, 5
    /// for a module), the database id, the object id, and four bytes and a
    /// tail the simulator derives from the cache key, so a plan compiled
    /// again under the same key gets the handle it had — as real's did in
    /// probes (2026-10-15 against SQL Server 2025), whose middle bytes aren't
    /// otherwise reproduced.
    /// </summary>
    internal static byte[] PlanHandleOf(in CachedPlanKey key, int objectId)
    {
        var handle = new byte[BuiltInResources.SqlHandleLength];
        handle[0] = key.Kind is CachedPlanKind.Adhoc or CachedPlanKind.Prepared ? (byte)6 : (byte)5;
        BinaryPrimitives.WriteInt16LittleEndian(handle.AsSpan(2), key.DatabaseId);
        BinaryPrimitives.WriteInt32LittleEndian(handle.AsSpan(4), objectId);
        BinaryPrimitives.WriteInt32LittleEndian(handle.AsSpan(8), HashCode.Combine(key.Kind, key.SetOptions, key.LanguageId, key.DateFormat, key.DateFirst));
        handle[12] = 0x0C;
        handle[16] = 1;
        return handle;
    }

    /// <summary>
    /// A module's SQL handle in real's shape: the type byte 3, the database id,
    /// the object id, eight bytes the simulator derives from the object id
    /// (real's aren't recovered), and the procedure number — 1 for a
    /// procedure, 0 otherwise (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    internal static byte[] ModuleSqlHandle(short databaseId, SchemaObject module)
    {
        var handle = new byte[BuiltInResources.SqlHandleLength];
        handle[0] = 3;
        BinaryPrimitives.WriteInt16LittleEndian(handle.AsSpan(2), databaseId);
        BinaryPrimitives.WriteInt32LittleEndian(handle.AsSpan(4), module.ObjectId);
        BinaryPrimitives.WriteInt64LittleEndian(handle.AsSpan(8), (long)(uint)HashCode.Combine(databaseId, module.ObjectId) << 8);
        BinaryPrimitives.WriteInt32LittleEndian(handle.AsSpan(16), module is Procedure ? 1 : 0);
        return handle;
    }

    /// <summary>The statement placed at <paramref name="startOffset"/> to <paramref name="endOffset"/>, or null. Caller holds the plan's lock.</summary>
    public CachedStatementStats? FindStatement(int startOffset, int endOffset)
    {
        foreach (var statement in this.Statements)
        {
            if (statement.StartOffset == startOffset && statement.EndOffset == endOffset)
                return statement;
        }
        return null;
    }

    /// <summary>Adds the statement placed at <paramref name="startOffset"/> to <paramref name="endOffset"/>. Caller holds the plan's lock.</summary>
    public CachedStatementStats AddStatement(int startOffset, int endOffset, DateTime now, string statementText, QueryStoreShape shape, byte parameterizationType, long? contextSettingsId)
    {
        var added = new CachedStatementStats(startOffset, endOffset, this.PlanGeneration, now, statementText, shape, parameterizationType, contextSettingsId);
        this.Statements.Add(added);
        return added;
    }
}

/// <summary>
/// A simulation's plan cache as <c>sys.dm_exec_cached_plans</c> sees it:
/// every batch and module real would have compiled and kept, with what their
/// statements measured. Separate from the parse caches that speed the
/// simulator itself up, which keep what is cheap to replay rather than what
/// real keeps. Bounded at <see cref="Capacity"/> entries, past which the
/// oldest goes first.
/// </summary>
internal sealed class CachedPlanRegistry
{
    public const int Capacity = 10_000;

    /// <summary>The plans, read without a lock by every batch's lookup; added to and removed from under <see cref="gate"/>.</summary>
    private readonly ConcurrentDictionary<CachedPlanKey, CachedPlan> entries = new();

    private readonly Lock gate = new();

    /// <summary>How many plans <see cref="entries"/> holds, kept under <see cref="gate"/>.</summary>
    private int count;

    /// <summary>Keys in the order they were added, which eviction takes from the front; may hold keys already removed.</summary>
    private readonly Queue<CachedPlanKey> order = new();

    /// <summary>The plan held under <paramref name="key"/>, or null.</summary>
    public CachedPlan? Find(in CachedPlanKey key) => this.entries.GetValueOrDefault(key);

    /// <summary>
    /// The plans compiled into the cache, all told — what the
    /// <c>SQL Compilations/sec</c> performance counter reports. Written under
    /// the gate.
    /// </summary>
    public long Compilations;

    /// <summary>
    /// Adds <paramref name="plan"/> under its key, unless another session added
    /// one there first, which is returned instead; <paramref name="added"/>
    /// says which.
    /// </summary>
    public CachedPlan Add(CachedPlan plan, out bool added)
    {
        lock (this.gate)
        {
            if (this.entries.TryGetValue(plan.Key, out var existing))
            {
                added = false;
                return existing;
            }
            if (this.count >= Capacity)
                this.EvictOldest();
            this.Compilations++;
            _ = this.entries.TryAdd(plan.Key, plan);
            this.count++;
            this.order.Enqueue(plan.Key);
            if (this.order.Count > 2 * Capacity)
                this.Compact();
            added = true;
            return plan;
        }
    }

    private void EvictOldest()
    {
        while (this.order.TryDequeue(out var oldest))
        {
            if (this.entries.TryRemove(oldest, out var evicted))
            {
                evicted.Removed = true;
                this.count--;
                return;
            }
        }
    }

    /// <summary>Drops the keys already removed from the eviction order.</summary>
    private void Compact()
    {
        var live = this.order.Where(this.entries.ContainsKey).Distinct().ToArray();
        this.order.Clear();
        foreach (var key in live)
            this.order.Enqueue(key);
    }

    /// <summary>The plans held now.</summary>
    public CachedPlan[] Snapshot() => [.. this.entries.EnumerateValues()];

    /// <summary>Removes the plans <paramref name="matches"/> picks, or every plan when it is null.</summary>
    public void Remove(Func<CachedPlan, bool>? matches)
    {
        lock (this.gate)
        {
            foreach (var (key, plan) in this.entries)
            {
                if ((matches is null || matches(plan)) && this.entries.TryRemove(key, out _))
                {
                    plan.Removed = true;
                    this.count--;
                }
            }
            if (matches is null)
                this.order.Clear();
        }
    }
}
