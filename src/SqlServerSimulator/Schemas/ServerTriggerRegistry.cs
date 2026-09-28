namespace SqlServerSimulator.Schemas;

/// <summary>
/// The server-scope triggers (<c>CREATE TRIGGER … ON ALL SERVER</c>) of one
/// <see cref="Simulation"/>, keyed by name.
/// </summary>
/// <remarks>
/// Every session open reads <see cref="All"/> to decide whether a logon
/// trigger fires, so the no-trigger case must cost one field read: the
/// <see cref="All"/> snapshot is rebuilt on each (rare) change instead of asking a
/// <c>ConcurrentDictionary</c> whether it is empty, which takes every one of
/// its locks when it is.
/// </remarks>
internal sealed class ServerTriggerRegistry
{
    private readonly Dictionary<string, DdlTrigger> byName = new(BuiltInToken.Comparer);

    /// <summary>
    /// Every server-scope trigger, in <c>object_id</c> (creation) order: a
    /// snapshot replaced whole on each change, never mutated in place.
    /// </summary>
    public volatile DdlTrigger[] All = [];

    public bool TryGetValue(string name, out DdlTrigger trigger)
    {
        lock (this.byName)
            return this.byName.TryGetValue(name, out trigger!);
    }

    /// <summary>Stores <paramref name="trigger"/> under its name, returning the one it replaced.</summary>
    public DdlTrigger? Set(DdlTrigger trigger)
    {
        lock (this.byName)
        {
            _ = this.byName.TryGetValue(trigger.Name, out var previous);
            this.byName[trigger.Name] = trigger;
            this.Rebuild();
            return previous;
        }
    }

    public bool Remove(string name, out DdlTrigger? removed)
    {
        lock (this.byName)
        {
            if (!this.byName.Remove(name, out removed))
                return false;
            this.Rebuild();
            return true;
        }
    }

    private void Rebuild()
    {
        var all = this.byName.Values.ToArray();
        Array.Sort(all, static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
        this.All = all;
    }
}
