using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The <c>inserted</c> / <c>deleted</c> pair one table's or view's triggers of
/// one timing read, kept from firing to firing so a trigger statement's cached
/// plan — which binds the two <see cref="HeapTable"/> instances, as any plan
/// binds the tables it reads — replays against the next firing's rows. Each
/// firing that takes the pair gives both tables a new <see cref="Heap"/>
/// holding its own rows (<see cref="Fill"/>); a plan names the tables, never a
/// heap, so it reads whichever firing holds the pair as it runs.
/// </summary>
/// <remarks>
/// <para>
/// One firing holds the pair at a time (<see cref="TryTake"/>): another
/// session's firing, or a nested firing on the same parent while an outer one
/// runs, materializes tables of its own, as every firing once did, and its
/// statements over them parse rather than replay — a plan names the pair it
/// was recorded over (<see cref="StatementPlanEntry.PseudoTables"/>), and only
/// the batch holding that pair replays it.
/// </para>
/// <para>
/// The pair stands while the parent's columns array and the schema version it
/// was built under stand, so the tables always carry the shape a firing's rows
/// are encoded in; anything else builds a new pair, whose plans record afresh.
/// A <c>GLOBAL</c> cursor a body declares and leaves allocated reads the
/// tables after the firing ends, so the pair it reads is retired rather than
/// refilled under it.
/// </para>
/// </remarks>
internal sealed class PseudoTableSlot
{
    /// <summary>The reused <c>inserted</c> table.</summary>
    public readonly HeapTable Inserted;

    /// <summary>The reused <c>deleted</c> table.</summary>
    public readonly HeapTable Deleted;

    /// <summary>The parent's columns the pair was built over (an <c>INSTEAD OF</c> pair widens their nullability).</summary>
    private readonly HeapColumn[] sourceColumns;

    /// <summary>The <see cref="Simulation.SchemaVersion"/> the pair was built under.</summary>
    private readonly long schemaVersion;

    /// <summary>1 while a firing holds the pair.</summary>
    private int held;

    /// <summary>Set once a firing leaves a cursor over the pair allocated; the pair is never taken again.</summary>
    private volatile bool retired;

    /// <summary>Set while the pair is newly built, whose build drew its tables' object ids.</summary>
    private bool idsDrawnByBuild;

    /// <summary>The <c>GLOBAL</c> cursors the holding firing declared, checked as it lets the pair go.</summary>
    private List<Cursor>? globalCursors;

    private PseudoTableSlot(HeapTable inserted, HeapTable deleted, HeapColumn[] sourceColumns, long schemaVersion)
    {
        this.Inserted = inserted;
        this.Deleted = deleted;
        this.sourceColumns = sourceColumns;
        this.schemaVersion = schemaVersion;
        inserted.ReplacesHeapPerFiring = deleted.ReplacesHeapPerFiring = true;
    }

    /// <summary>
    /// Takes the pair kept in <paramref name="home"/> for one firing, building
    /// and keeping a new one with <paramref name="build"/> — an empty table of
    /// the given name — when there is none or it no longer fits
    /// <paramref name="sourceColumns"/> under <paramref name="schemaVersion"/>.
    /// Null when another firing holds it; the caller then materializes tables
    /// of its own.
    /// </summary>
    public static PseudoTableSlot? TryTake(ref PseudoTableSlot? home, HeapColumn[] sourceColumns, long schemaVersion, Func<string, HeapTable> build)
    {
        var kept = Volatile.Read(ref home);
        if (kept is not null && !kept.retired && kept.schemaVersion == schemaVersion && SameColumns(kept.sourceColumns, sourceColumns))
            return Interlocked.CompareExchange(ref kept.held, 1, 0) == 0 ? kept : null;

        var fresh = new PseudoTableSlot(build("inserted"), build("deleted"), sourceColumns, schemaVersion) { held = 1, idsDrawnByBuild = true };
        // Losing the race to another firing's new pair leaves this one unkept;
        // its plans are replaced as the kept pair's firings record theirs.
        _ = Interlocked.CompareExchange(ref home, fresh, kept);
        return fresh;
    }

    /// <summary>
    /// Whether <paramref name="kept"/> and <paramref name="offered"/> hold the
    /// same column objects — a view's columns are bound afresh for each
    /// statement writing through it, into a new array each time.
    /// </summary>
    private static bool SameColumns(HeapColumn[] kept, HeapColumn[] offered)
    {
        if (ReferenceEquals(kept, offered))
            return true;
        if (kept.Length != offered.Length)
            return false;
        for (var i = 0; i < kept.Length; i++)
        {
            if (!ReferenceEquals(kept[i], offered[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Gives both tables a new heap holding this firing's rows, in the order
    /// <paramref name="encode"/> writes them. False when the pair was built for
    /// this firing, whose build drew the tables' object ids; true when the
    /// caller still has them to draw.
    /// </summary>
    public bool Fill(Action<HeapTable, Heap> encode)
    {
        foreach (var table in (ReadOnlySpan<HeapTable>)[this.Inserted, this.Deleted])
        {
            var heap = new Heap(sessionPrivate: true) { ReclaimColumns = table.Heap.ReclaimColumns };
            encode(table, heap);
            table.Heap = heap;
        }
        var drawn = this.idsDrawnByBuild;
        this.idsDrawnByBuild = false;
        return !drawn;
    }

    /// <summary>Notes a <c>GLOBAL</c> cursor the holding firing's body declared.</summary>
    public void NoteGlobalCursor(Cursor cursor) => (this.globalCursors ??= []).Add(cursor);

    /// <summary>
    /// Lets the pair go as the firing ends — retiring it when a cursor the
    /// firing declared is still allocated on <paramref name="connection"/> —
    /// and drops a large firing's rows rather than keeping them to the next.
    /// </summary>
    public void Release(SimulatedDbConnection connection)
    {
        if (this.globalCursors is { } cursors)
        {
            this.globalCursors = null;
            foreach (var (_, cursor) in connection.Cursors)
            {
                if (cursors.Contains(cursor))
                    this.retired = true;
            }
        }
        if (!this.retired)
        {
            foreach (var table in (ReadOnlySpan<HeapTable>)[this.Inserted, this.Deleted])
            {
                if (table.Heap.Pages.Count > 1 || table.Heap.LobPages.Count != 0)
                    table.Heap = new Heap(sessionPrivate: true) { ReclaimColumns = table.Heap.ReclaimColumns };
            }
        }
        Volatile.Write(ref this.held, 0);
    }
}
