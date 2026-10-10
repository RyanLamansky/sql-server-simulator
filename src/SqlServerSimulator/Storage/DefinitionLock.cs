using System.Collections.Concurrent;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The lock a statement creating, altering, dropping or renaming an object
/// takes on the object's name in its schema, held in Sch-M to the
/// transaction's end — real's X on the name's catalog row. A session
/// resolving the name while another transaction's change to it is
/// uncommitted waits for that change to commit or roll back, and so does a
/// second change to the name, whichever kind of object either one makes
/// (probed 2026-10-10 against SQL Server 2025: a call, <c>OBJECT_ID</c>, a
/// catalog view seeking the name or the dropped object's id, a <c>CREATE</c>
/// or <c>DROP</c> of the name all wait on a dropped function's row, while the
/// same reads of another name don't).
/// </summary>
/// <remarks>
/// Registered in <see cref="Registry"/> (a <see cref="Schema.DefinitionLocks"/>)
/// only while held or awaited: the final release takes it out
/// (<see cref="LockManager.Release"/>), so a schema with no change pending
/// reads as empty and a resolution pays one emptiness check.
/// </remarks>
internal sealed class DefinitionLock
{
    public readonly LockResource Resource;

    /// <summary>The schema's registry of names a change holds, which holds this lock under <see cref="Name"/> while it is held.</summary>
    public readonly ConcurrentDictionary<string, DefinitionLock> Registry;

    public readonly string Name;

    /// <summary>
    /// The ids of the objects the holder's statements created, altered or
    /// dropped under <see cref="Name"/>, which a catalog read seeking an
    /// <c>object_id</c> meets — a dropped object's among them, which no longer
    /// resolves. Replaced whole under <see cref="gate"/>.
    /// </summary>
    public int[] ObjectIds = [];

    /// <summary>
    /// The subset of <see cref="ObjectIds"/> that held the name before the
    /// holder's change — an object it altered, dropped or renamed rather than
    /// created.
    /// </summary>
    public int[] PriorObjectIds = [];

    private readonly Lock gate = new();

    public DefinitionLock(ConcurrentDictionary<string, DefinitionLock> registry, string name)
    {
        this.Registry = registry;
        this.Name = name;
        this.Resource = new LockResource { Definition = this };
    }

    /// <summary>
    /// Notes <paramref name="objectId"/> among <see cref="ObjectIds"/>, and
    /// with <paramref name="prior"/> among <see cref="PriorObjectIds"/>.
    /// </summary>
    public void NoteObject(int objectId, bool prior)
    {
        lock (this.gate)
        {
            if (Array.IndexOf(this.ObjectIds, objectId) < 0)
                this.ObjectIds = [.. this.ObjectIds, objectId];
            if (prior && Array.IndexOf(this.PriorObjectIds, objectId) < 0)
                this.PriorObjectIds = [.. this.PriorObjectIds, objectId];
        }
    }

    /// <summary>Whether <paramref name="objectId"/> held the name before the holder's change.</summary>
    public bool HeldBefore(int objectId) => Array.IndexOf(this.PriorObjectIds, objectId) >= 0;

    /// <summary>
    /// Takes this lock out of <see cref="Registry"/>, unless another instance
    /// has taken its place. Called under the lock manager's gate once nothing
    /// holds or awaits <see cref="Resource"/>.
    /// </summary>
    public void Unregister() => _ = this.Registry.TryRemove(new KeyValuePair<string, DefinitionLock>(this.Name, this));
}
