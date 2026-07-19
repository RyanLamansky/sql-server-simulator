# Heap page lifecycle: reclamation, reuse, DBCC SHRINK

The storage-layer basics (8KB pages, row encoding, LOB chains, flat page list) live in the root CLAUDE.md architecture section; this covers the reclamation/shrink behavior and its divergences.

**Reclaimed heap space is reused; page lists shrink only from the tail**: superseded row bytes + off-row LOB chains are freed and reused (`HeapPage.Compact` / `Heap.FreeLobChain`), so memory tracks the *peak concurrent* working set.
A fully-dead interior page is reused in place but never removed from `Heap.Pages`, and a reclaimed slot keeps a 2-byte zero-extent directory entry — mid-list removal would break the stable `(page, slot)` addresses cursors, version Rids, and forward pointers depend on.

`DBCC SHRINKDATABASE`/`SHRINKFILE` trim only the *trailing* run of dead/freed-LOB pages (`Heap.TrimTrailingDeadPages` / `TrimTrailingFreeLobPages`, after a version-store GC); interior dead + version-/lock-pinned tail pages stay.
SHRINKDATABASE emits no result set; SHRINKFILE returns the per-file row with sizes from heap page totals (no physical file model).

A versioning-on **autocommit** UPDATE/DELETE reclaims its superseded chains via a statement-end GC pass when no snapshot is open.

**The live page counts are surfaced to the catalog**: `Heap.Pages.Count` (data pages) and `Heap.LobPages.Count` (LOB-chain pages) back `sys.allocation_units.total_pages` / `used_pages` / `data_pages`, and their per-database sum (`BuiltInResources.SumDataFilePages`) sizes `sys.database_files` / `sys.master_files` and `FILEPROPERTY(<db>_Data, 'SpaceUsed')`.
Because reclaimed interior pages stay in `Pages` (only the tail trims), these counts reflect the peak concurrent working set, not a post-GC minimum — a divergence from real SQL Server's IAM-tracked allocation.
See [`catalog-views.md`](catalog-views.md) for the self-consistency contract.
