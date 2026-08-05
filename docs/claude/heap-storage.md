# Heap page lifecycle: reclamation, reuse, DBCC SHRINK

The storage-layer basics (8KB pages, row encoding, LOB chains, flat page list) live in the root CLAUDE.md architecture section; this covers the reclamation/shrink behavior and its divergences.

**Reclaimed heap space is reused; page lists shrink only from the tail**: superseded row bytes + off-row LOB chains are freed and reused (`HeapPage.Compact` / `Heap.FreeLobChain`), so memory tracks the *peak concurrent* working set.
A fully-dead interior page is reused in place but never removed from `Heap.Pages`, and a reclaimed slot keeps a 2-byte zero-extent directory entry — mid-list removal would break the stable `(page, slot)` addresses cursors, version Rids, and forward pointers depend on.

`DBCC SHRINKDATABASE`/`SHRINKFILE` trim only the *trailing* run of dead/freed-LOB pages (`Heap.TrimTrailingDeadPages` / `TrimTrailingFreeLobPages`, after a version-store GC); interior dead + version-/lock-pinned tail pages stay.
SHRINKDATABASE emits no result set; SHRINKFILE returns the per-file row with sizes from heap page totals (no physical file model).

A versioning-on **autocommit** UPDATE/DELETE reclaims its superseded chains via a statement-end GC pass when no snapshot is open.

**The scan walks slots inline.**
`Heap.EnumerateRowsWithAddress` is the path every table scan in the engine runs, so it reads each slot directory entry **once** (`HeapPage.TryReadLiveSlot` returns liveness, payload and the forward bit together) and iterates slots itself rather than through a per-page enumerator.
The individual accessors it replaces re-read the same 2-byte entry up to four times per row, and the nested iterator added a `MoveNext` per row on top.
The forward-target set is probed only when it holds something — it is empty for any heap no `UPDATE` has relocated a row in, and its key is a tuple, so testing `Count` first keeps a hash probe off every scanned row.
Both reads stay per row rather than being hoisted, so a heap mutated mid-enumeration is seen exactly as it was before.
Measured on a 228k-row `SELECT COUNT(*)`: **71 ms → 11 ms**, which is the floor under every scan-bound query in the battery.

**The reuse candidates are walked without snapshotting them.**
`Heap.TryReuseReclaimablePage` runs on the insert path — once for every row the tail page can't hold, which on a bulk load is once per page — and the candidate set is a `ConcurrentDictionary`.
Reading its `Keys` property takes *every* one of the dictionary's locks and copies the keys into a fresh collection; the walk enumerates the dictionary directly instead, which is the lock-free weakly-consistent enumeration the set was chosen for, and short-circuits on `IsEmpty` for the overwhelmingly common heap nothing has deleted from.
Removing candidates mid-walk is what that enumerator supports, so the stale-index and exhausted-page removals stay where they were.

**The slot total is maintained, not walked.**
`Heap.RowCount` is a field the four seams that move it keep current — `InsertCore` (each insert appends exactly one slot, whether to the tail page, a reused reclaimable page or a fresh one), `TrimTrailingDeadPages` (whole pages off the tail), and `TRUNCATE` plus the undo log's truncation restore, which replace `Heap.Pages` wholesale and re-derive through `Heap.RecomputeRowCount`.
Nothing else changes a page's slot count: a DELETE tombstones its slot in place, an undo un-tombstones it, and `HeapPage.Compact` preserves slot indices by design.
The join planner reads the count once per join level per execution (the seek-vs-hash ratio, and the hash build's row-list sizing), which made the O(pages) walk a per-query cost that grew with the table.
`RecomputeRowCount` is also the walk `Tests.Internal`'s `HeapRowCountTests` asserts the maintained value against, after inserts, deletes, a relocating UPDATE, a rolled-back insert / delete / TRUNCATE, and a `DBCC SHRINKDATABASE`.
It counts *slots* rather than live rows — a tombstone keeps its directory entry — which is what the walk it replaced counted.

**`HeapPage.InstallForward` asserts its extent.**
The 6-byte forward reference is written over the slot's existing payload, so a shorter extent would spill into the neighbouring slot.
Every SQL-reachable row clears that floor (the encoder's header alone — flags, fixed offset, column count, NULL bitmap — is at least 6 bytes), so a `Debug.Assert` on `SlotExtent` is a tripwire on the encoder's minimum rather than a runtime check.

**The live page counts are surfaced to the catalog**: `Heap.Pages.Count` (data pages) and `Heap.LobPages.Count` (LOB-chain pages) back `sys.allocation_units.total_pages` / `used_pages` / `data_pages`, and their per-database sum (`BuiltInResources.SumDataFilePages`) sizes `sys.database_files` / `sys.master_files` and `FILEPROPERTY(<db>_Data, 'SpaceUsed')`.
Because reclaimed interior pages stay in `Pages` (only the tail trims), these counts reflect the peak concurrent working set, not a post-GC minimum — a divergence from real SQL Server's IAM-tracked allocation.
See [`catalog-views.md`](catalog-views.md) for the self-consistency contract.
