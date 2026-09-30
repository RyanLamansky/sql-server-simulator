# Heap page lifecycle: reclamation, reuse, DBCC SHRINK

The storage-layer basics (8KB pages, row encoding, LOB chains, flat page list) live in the root CLAUDE.md architecture section; this covers the reclamation/shrink behavior and its divergences.

**Reclaimed heap space is reused; page lists shrink only from the tail**: superseded row bytes + off-row LOB chains are freed and reused (`HeapPage.Compact` / `Heap.FreeLobChain`), so memory tracks the *peak concurrent* working set.
A fully-dead interior page is reused in place but never removed from `Heap.Pages`, and a reclaimed slot keeps a 2-byte zero-extent directory entry — mid-list removal would break the stable `(page, slot)` addresses cursors, version Rids, and forward pointers depend on.

`DBCC SHRINKDATABASE`/`SHRINKFILE` trim only the *trailing* run of dead/freed-LOB pages (`Heap.TrimTrailingDeadPages` / `TrimTrailingFreeLobPages`, after a version-store GC); interior dead + version-/lock-pinned tail pages stay.
SHRINKDATABASE emits no result set, only a Msg 5201 per file saying it had no free space to give back; SHRINKFILE returns the per-file row with sizes from heap page totals (no physical file model) — the rest of the DBCC family is in [`dbcc.md`](dbcc.md).

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

**A row the heap copies is encoded into a reused buffer.**
`RowEncoder.EncodeRow` allocates the row's exact-length array and hands it back, which is what a caller that retains the bytes needs — a result-set row list, an array literal, an undo-log payload.
A caller that hands them straight to a heap and drops them takes `RowEncoder.EncodeRowInto` instead: it writes into a caller-owned buffer, grows it only on a longer row, and returns the length, so a loop costs one buffer rather than one array per row.
Nothing downstream can retain that buffer — `Heap.Insert` / `UpdateAt` take a `ReadOnlySpan<byte>` and copy into the page, and the undo log captures the *old* payload it read from the page.
The bulk paths run through it: both bacpac row loops, `SELECT … INTO`, the TDS bulk load, and the three `ALTER TABLE` rebuild loops.
The buffer is cleared over the row's length before the value pass, which the encoding depends on — the NULL bitmap and the bit-column runs are written with `|=`, a NULL fixed-length column is skipped rather than written, and TagB (byte 1) is never written at all — and that is the same zeroing a fresh array would have paid for.
Measured on a bacpac import: WideWorldImporters-Full **2906 MB → 2456 MB** of allocation and AdventureWorks **692 MB → 622 MB**, both at unchanged wall clock.
The row-at-a-time DML paths (`INSERT` / `UPDATE` / `MERGE` and the FK-cascade, trigger and legacy-LOB writers) allocate per row, because they thread the image through `BatchContext.ProbeKeyLocksForInsert(HeapTable, byte[])` / `ProbeKeyLocksForUpdate`, which read it but take an array: those signatures and `RowDecoder.DecodeColumn` under them would have to take a span first, since handing them a reused buffer would also reach `Heap.Insert` with a length longer than the row.

**The encode is lossy in exactly one place, and the value form has to reproduce it.**
`varchar` / `char` / `text` encode through their collation's ANSI code page, whose encoder fallback is `?`, so a character the page can't carry is lost on the way to bytes — a narrowing real SQL Server performs too.
Every other family's value factory normalizes its payload at construction (`FromTime` quantizes to the declared precision, `FromDecimal` re-tags to the declared scale, `FromChar` pads by byte count, `FromDateTime` quantizes to 1/300 s, `FromMoney` scales to a long), so for those `Decode(Encode(v))` is the identity.
`RowEncoder.NarrowingColumns` / `RowEncoder.StorageForm` are the pair that lets a consumer holding already-projected `SqlValue` rows apply that one narrowing without building a page image — see [`data-reader.md`](data-reader.md#the-row-form-the-reader-reads).

**The live page counts are surfaced to the catalog**: `Heap.Pages.Count` (data pages) and `Heap.LobPages.Count` (LOB-chain pages) back `sys.allocation_units.total_pages` / `used_pages` / `data_pages`, and their per-database sum (`BuiltInResources.SumDataFilePages`) sizes `sys.database_files` / `sys.master_files` and `FILEPROPERTY(<db>, 'SpaceUsed')`.
Because reclaimed interior pages stay in `Pages` (only the tail trims), these counts reflect the peak concurrent working set, not a post-GC minimum — a divergence from real SQL Server's IAM-tracked allocation.
See [`catalog-views.md`](catalog-views.md) for the self-consistency contract.

## Row addresses reach expressions through a row locator

A heap source hands each row to the executor as a fresh `byte[]`, and expressions see a row only through the tuple's column resolver, so nothing downstream of a scan knew which `(page, slot)` a value came from.
`RowLocator` carries it: a consumer asks for a name no identifier can spell — a marker character in front of a column's leaf for its `TEXTPTR`, or in front of a source index for the bare address — and the name travels the ordinary resolver chain, correlation to an enclosing query included.
`SourceColumnMemo` binds it on its miss path as a source below -1, which `ResolveAcrossTuple` already branches on for the outer-scope fallthrough, so an ordinary column resolution pays nothing for it.

The address comes from `RowAddressMap` on `StatementContext.RowAddresses`, keyed by the yielded array's reference: every heap row producer a query source reads through — the lock-checked scan (heap and clustered order, snapshot versions), the unlocked scan, the index-seek and snapshot seek materializations, and the cursor's slot scan — records the array against the address it read, but only while the statement has installed a map.
A plan whose query block reads a locator installs one as it starts (`Selection.InstallsRowAddresses`, settled at parse by `ParserContext.ReadsRowLocators`), and `Selection.ExecuteWithRowAddresses` installs one around a body it runs with the address of each row appended to its projection.
With no map installed, each producer tests one local it read once per enumeration.

Because the address is a projected value, it passes through a `TOP`, a sort or a window stage with its row, which is what lets a write through a row-limited or windowed body name the base row behind each row the body yields ([`programmable.md`](programmable.md#updatable-views-dml-through-views)).
A body whose rows arrive through another view carries no address of its own: the view's rows are re-encoded, so the locator reads NULL there and the consumer falls back to pairing.

Measured on the index replay (2.1M records): **62.2 / 60.7 s** without the mechanism and **62.2 / 60.8 s** with it, and none of the producers' loops gained an allocation.

