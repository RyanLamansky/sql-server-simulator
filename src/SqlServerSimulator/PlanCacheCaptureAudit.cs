#if DEBUG
using System.Reflection;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Debug-build check that a plan entering the plan cache holds nothing that
/// belongs to the session or the execution that parsed it. Walks every field
/// reachable from the cached <see cref="Selection"/>s and DML statement plans
/// (<see cref="DmlStatementPlan"/>) — through closures,
/// iterator state machines, collections and structs — stopping at the shared
/// server objects a plan may legitimately hold (tables, databases, schema
/// objects, collations, types), and raises naming the path to the first
/// execution-scoped object it meets.
/// <para>
/// The walk sees references, not values, so a second check covers what a parse
/// <em>copies</em> out of the principal it runs as — a mask settled for it, a
/// permission check it passed: the parse of a statement the cache may keep is
/// watched (<see cref="WatchPrincipalReads"/>), and a plan whose parse read the
/// session's effective principal outside a step its replay repeats
/// (<see cref="SuspendPrincipalWatch"/>) is refused as it enters the cache
/// (<see cref="VerifyPrincipalIndependent"/>).
/// </para>
/// </summary>
internal static class PlanCacheCaptureAudit
{
    /// <summary>The parse being watched on this thread, innermost first.</summary>
    [ThreadStatic]
    private static PrincipalReadWatch? watch;

    /// <summary>
    /// One parse's watch over <see cref="Security"/>: where it first read the
    /// effective principal, if it did. Another session's identity — a linked
    /// server's session opened while parsing — isn't this parse's to watch.
    /// </summary>
    internal sealed class PrincipalReadWatch(SessionSecurityContext security, PrincipalReadWatch? outer)
    {
        public readonly SessionSecurityContext Security = security;
        public readonly PrincipalReadWatch? Outer = outer;
        public int Suspended;
        public string? FirstRead;
    }

    /// <summary>Starts watching the parse about to run on this thread; <see cref="EndPrincipalWatch"/> ends it.</summary>
    public static PrincipalReadWatch WatchPrincipalReads(SessionSecurityContext security) =>
        watch = new PrincipalReadWatch(security, watch);

    /// <summary>Ends <paramref name="ended"/>, answering where it first read the principal, if it did.</summary>
    public static string? EndPrincipalWatch(PrincipalReadWatch ended)
    {
        if (!ReferenceEquals(watch, ended))
            throw new InvalidOperationException("A principal-read watch ended out of order.");
        watch = ended.Outer;
        return ended.FirstRead;
    }

    /// <summary>Called on every read of <paramref name="security"/>'s effective principal.</summary>
    public static void NotePrincipalRead(SessionSecurityContext security)
    {
        if (watch is { Suspended: 0, FirstRead: null } current && ReferenceEquals(current.Security, security))
            current.FirstRead = Environment.StackTrace;
    }

    /// <summary>
    /// Excuses the principal reads until disposed: a step the replay repeats as
    /// its own principal, or a parse whose outcome no principal changes.
    /// </summary>
    public static PrincipalWatchSuspension SuspendPrincipalWatch()
    {
        if (watch is { } current)
            current.Suspended++;
        return new PrincipalWatchSuspension(watch);
    }

    /// <summary>The scope <see cref="SuspendPrincipalWatch"/> opens.</summary>
    internal readonly struct PrincipalWatchSuspension(PrincipalReadWatch? suspended) : IDisposable
    {
        public void Dispose()
        {
            if (suspended is not null)
                suspended.Suspended--;
        }
    }

    /// <summary>
    /// Raises <see cref="InvalidOperationException"/> when the parse of a plan
    /// entering the cache read the principal it ran as (<paramref name="firstRead"/>,
    /// from <see cref="EndPrincipalWatch"/>).
    /// </summary>
    public static void VerifyPrincipalIndependent(string? firstRead, string commandText)
    {
        if (firstRead is not null)
            throw new InvalidOperationException($"A cached plan for `{commandText}` was parsed reading the session's principal outside a step its replay repeats, at:{Environment.NewLine}{firstRead}");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> fieldsByType = new();

    /// <summary>
    /// Raises <see cref="InvalidOperationException"/> when any of
    /// <paramref name="plans"/> reaches a per-session or per-execution object.
    /// </summary>
    public static void Verify(IEnumerable<object> plans, string commandText)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(object Value, string Path)>();
        foreach (var plan in plans)
            pending.Push((plan, plan.GetType().Name));
        while (pending.TryPop(out var item))
        {
            var (value, path) = item;
            var type = value.GetType();
            if (Forbidden(value) is { } reason)
                throw new InvalidOperationException($"A cached plan for `{commandText}` reaches {reason} via {path}.");
            if (!type.IsValueType && !seen.Add(value))
                continue;
            if (IsShared(type))
                continue;
            switch (value)
            {
                case Delegate d:
                    foreach (var single in d.GetInvocationList())
                    {
                        if (single.Target is { } target)
                            pending.Push((target, $"{path}->{single.Method.Name}"));
                    }
                    continue;
                case Array array:
                    if (IsLeaf(type.GetElementType()!))
                        continue;
                    var index = 0;
                    foreach (var element in array)
                    {
                        if (element is not null)
                            pending.Push((element, $"{path}[{index}]"));
                        index++;
                    }
                    continue;
            }
            foreach (var field in fieldsByType.GetOrAdd(type, AllInstanceFields))
            {
                if (field.GetValue(value) is { } child && !IsLeaf(child.GetType()))
                    pending.Push((child, $"{path}.{field.Name}"));
            }
        }
    }

    private static string? Forbidden(object value) => value switch
    {
        BatchContext => "the parsing BatchContext",
        ParserContext => "the parsing ParserContext",
        StatementContext => "a StatementContext",
        SimulatedDbConnection => "a SimulatedDbConnection",
        SimulatedDbCommand => "a SimulatedDbCommand",
        SimulatedDbTransaction => "a SimulatedDbTransaction",
        SessionToken => "a SessionToken",
        SessionSecurityContext => "a SessionSecurityContext",
        VariableSlot => "a VariableSlot of the parsing batch",
        UndoLog => "an UndoLog",
        PhantomFenceState => "a SERIALIZABLE fence's settled flag",
        ProcFrame => "a procedure call's frame",
        UdfFrame => "a function call's frame",
        TriggerFrame => "a trigger firing's frame",
        // A trigger's kept inserted / deleted belongs to its parent, not to a
        // batch; a plan names it and reads whichever firing holds it.
        HeapTable { IsTableVariable: true, ReplacesHeapPerFiring: false } => "a table variable",
        HeapTable table when BatchContext.IsLocalTempName(table.Name) => "a #temp table",
        _ => null,
    };

    private static bool IsShared(Type type) =>
        typeof(SchemaObject).IsAssignableFrom(type)
        || typeof(Collation).IsAssignableFrom(type)
        || typeof(SqlType).IsAssignableFrom(type)
        || type == typeof(Heap)
        || type == typeof(Database)
        || type == typeof(Schema)
        || type == typeof(Simulation)
        || type == typeof(LockResource)
        || typeof(MemberInfo).IsAssignableFrom(type)
        || typeof(Assembly).IsAssignableFrom(type);

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(Guid) || (type.IsArray && IsLeaf(type.GetElementType()!));

    // A debug-build diagnostic over whatever types a plan happens to hold; the
    // trimmer never sees a Debug build.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075")]
    private static FieldInfo[] AllInstanceFields(Type type)
    {
        List<FieldInfo> fields = [];
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!IsLeaf(field.FieldType))
                    fields.Add(field);
            }
        }

        return [.. fields];
    }
}
#endif
