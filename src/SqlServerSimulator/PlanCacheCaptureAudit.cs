#if DEBUG
using System.Reflection;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Debug-build check that a plan entering the plan cache holds nothing that
/// belongs to the session or the execution that parsed it. Walks every field
/// reachable from the cached <see cref="Selection"/>s — through closures,
/// iterator state machines, collections and structs — stopping at the shared
/// server objects a plan may legitimately hold (tables, databases, schema
/// objects, collations, types), and raises naming the path to the first
/// execution-scoped object it meets.
/// </summary>
internal static class PlanCacheCaptureAudit
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> fieldsByType = new();

    /// <summary>
    /// Raises <see cref="InvalidOperationException"/> when any of
    /// <paramref name="plans"/> reaches a per-session or per-execution object.
    /// </summary>
    public static void Verify(List<Selection> plans, string commandText)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(object Value, string Path)>();
        foreach (var plan in plans)
            pending.Push((plan, "Selection"));
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
        HeapTable { IsTableVariable: true } => "a table variable",
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
