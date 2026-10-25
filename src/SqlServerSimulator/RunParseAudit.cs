#if DEBUG
using System.Collections;
using System.Reflection;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

/// <summary>
/// Debug-build check that the batch compile walk, abandoning its parse of a
/// statement for the run (<see cref="BatchContext.ParsesForRun"/>), puts back
/// everything that parse left in its parser, batch and statement frame, so its
/// own parse of the statement begins as if the other never ran. Compares every
/// field of the three before and after — a collection by identity and count —
/// and raises naming the first that differs.
/// </summary>
internal static class RunParseAudit
{
    /// <summary>
    /// What the walk's own parse settles afresh however it finds them: the
    /// debug token trail, the memos of the text — whether it mentions an
    /// option, the line last placed — the variables a statement reads, which
    /// the statement's first read clears, and the kind of write a target's
    /// alias was noted for, read only beside the alias, which is put back.
    /// </summary>
    private static readonly string[] Unrestored = ["tokens", "commandMentionsOption", "lineMemoIndex", "lineMemoLine", "StatementVariables", "RemoteWriteAliasKind"];

    /// <summary>The fields of <paramref name="walk"/>'s parser, batch and statement frame, as they stand.</summary>
    public static Dictionary<string, object?> Snapshot(BatchContext walk)
    {
        var values = new Dictionary<string, object?>();
        foreach (var target in (ReadOnlySpan<object>)[walk.Parser, walk, walk.CurrentStatement])
        {
            foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = field.GetValue(target);
                values[$"{target.GetType().Name}.{field.Name}"] = value is ICollection collection ? (value, collection.Count) : value;
            }
        }
        return values;
    }

    /// <summary>Raises when a field of <paramref name="walk"/>'s three differs from <paramref name="before"/>.</summary>
    public static void Verify(BatchContext walk, Dictionary<string, object?> before)
    {
        foreach (var (name, value) in Snapshot(walk))
        {
            if (!Equals(before[name], value) && Array.IndexOf(Unrestored, name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..]) < 0)
                throw new InvalidOperationException($"The compile walk's abandoned parse for the run left {name} changed.");
        }
    }
}
#endif
