# `hierarchyid` data type

AW-minimum-viable surface ships: storage, two static factories, five instance methods, comparison, and `ORDER BY` honoring path order.

## Surface

**Static factories**:
- `hierarchyid::Parse(str)` — parses canonical path syntax (`/`, `/1/`, `/1/2.5/3/`, `/-1/`, etc.).
- `hierarchyid::GetRoot()` — returns the empty path `/`.

**Instance methods**:
- `.GetLevel()` → `smallint` (segment count).
- `.GetAncestor(n)` → ancestor path n levels up; raises Msg 6522 on `n < 0` or `n > level`.
- `.GetDescendant(child1, child2)` → a fresh descendant path between two siblings; covers all four `(NULL, NULL)` / `(c, NULL)` / `(NULL, c)` / `(c1, c2)` combinations probe-confirmed against AW's `[HumanResources].[Employee]` data.
- `.IsDescendantOf(other)` → `bit`.
- `.ToString()` → `nvarchar(4000)` canonical path string.

**Operators**: comparison + `ORDER BY` follow lexicographic path order. Probe-canonical sequence: `/`, `/-1/`, `/1/`, `/1/1/`, `/1/1/1/`, `/1/2/`, `/2/`.

## Storage

`HierarchyIdValue` carries the path as `int[][]` — outer array is segment sequence, inner arrays are dot-joined label tuples (`/1/2.5/3/` → `[[1], [2, 5], [3]]`). `HierarchyIdSqlType` (`Storage/HierarchyIdType.cs`) handles parse / format / compare / encode / decode. `SqlValue.FromHierarchyId(int[][])` boxes the array; `SqlValue.AsHierarchyId` returns it back.

`sys.types` row: `system_type_id=240`, `user_type_id=128`, `max_length=892`.

## Parser wiring

`Expression.cs` gains two cases in the binary-operator loop:

- **`Operator { Character: ':' }`** — recognizes the `hierarchyid::` type-scope shape (requires a second `:` immediately after, plus the prior `Reference` token to be the bare 1-part name `hierarchyid`, case-insensitive). Dispatches to `HierarchyIdStaticCall.Parse`. The same dispatch path also handles `geography::` / `geometry::` (see [`spatial.md`](spatial.md)).

- **`Operator { Character: '.' }`** extended — when the dotted-name component matches one of the closed accept-list method names (`GetLevel` / `GetAncestor` / `GetDescendant` / `IsDescendantOf` / `ToString`) **and** the very next token is `(`, dispatch to `HierarchyIdMethodCall.Parse` instead of the existing multipart-`Reference` path. Uses `SaveCheckpoint` / `RestoreCheckpoint` to peek without committing.

Known gap from the closed-list approach: a column literally named `GetLevel` (etc.) followed by `.MethodName(...)` would route through hierarchyid dispatch instead of multipart-reference resolution. AW doesn't exercise this collision; documented limitation.

`HierarchyIdMethodCall.Run` is also polymorphic on receiver type — a spatial receiver calling `.ToString()` falls through to the spatial WKT path (see [`spatial.md`](spatial.md)) rather than the hierarchyid canonical-path format.

## Errors

**Msg 6522** verbatim covers:
- `hierarchyid::Parse` on invalid input (empty, missing slash, double-slash, non-numeric, etc.)
- `.GetAncestor(-1)`
- `.GetDescendant(self, x)` where `x` isn't a direct child of self
- `.GetDescendant(c1, c2)` where `c1 >= c2`

`.GetReparentedValue` / `.Read` / `.Write` raise `NotSupportedException` if encountered — AW doesn't reference them.

## BCP wire decoder

The bacpac loader's `HierarchyIdWireDecoder` covers AW's `[0..79]` positive-ordinal envelope via a 4-prefix order-preserving prefix code:

| Prefix | Tier | Range |
|---|---|---|
| `01 VV 1` | P0 | 0..3 |
| `100 VV 1` | P1 | 4..7 |
| `101 VVV 1` | P2 | 8..15 |
| `110 VV 0 V 1 VVV 1` | P3 | 16..79 |

Negative ordinals, ordinals ≥ 80, and dotted sub-ordinals raise `NotSupportedException` for a follow-up bundle to extend cleanly.

## CAST byte form — deferred

`CAST(@h AS varbinary)` produces simulator-native bytes (segment-count + per-segment label-count + each label as int32 LE), **not** SQL Server's documented variable-bit OrdPath encoding. CAST round-trips within the simulator work; cross-engine byte transfer (BCP files round-trip via the wire decoder, but emitting bytes that real SQL Server would accept as `CAST(0x… AS hierarchyid)`) is intentionally deferred.

Probing on 2026-05-14 confirmed the SQL Server encoding has a recursive Stern-Brocot-tree structure with embedded sub-tier markers. Research notes below carry the cracked tiers + remaining unknowns so the byte-identical work can resume cold.

---

## Byte-identical CAST — research notes (deferred follow-up)

**Scope**: replace `HierarchyIdSqlType.Encode` / `Decode` with the SQL Server format. Method semantics + parser wiring stay; only the byte serialization changes. Once it lands, the BCP loader can pass `hierarchyid` payloads through as-is without an intermediate decode step (subject to confirming that the BCP wire format and `CAST AS varbinary` form are the same — fresh probe via `bcp out` of a hierarchyid column would answer it).

### Probe data — single-label paths

Raw bytes for `hierarchyid::Parse('/N/')` cast to `varbinary`, probed against SQL Server 2025. Bit columns MSB-first per byte.

| N | bytes | hex | bits |
|---|------:|---|---|
| -200 | 3 | `1BE044` | `00011011 11100000 01000100` |
| -120 | 3 | `1BEA44` | `00011011 11101010 01000100` |
| -100 | 3 | `1BEC64` | `00011011 11101100 01100100` |
| -80 | 3 | `1BEEC4` | `00011011 11101110 11000100` |
| -73 | 3 | `1BEEFC` | `00011011 11101110 11111100` |
| -72 | 2 | `2088` | `00100000 10001000` |
| -71 | 2 | `2098` | `00100000 10011000` |
| -50 | 2 | `24E8` | `00100100 11101000` |
| -20 | 2 | `2CC8` | `00101100 11001000` |
| -17 | 2 | `2CF8` | `00101100 11111000` |
| -16 | 2 | `2D88` | `00101101 10001000` |
| -15 | 2 | `2D98` | `00101101 10011000` |
| -8 | 2 | `3880` | `00111000 10000000` |
| -7 | 2 | `3980` | `00111001 10000000` |
| -4 | 2 | `3C80` | `00111100 10000000` |
| -3 | 2 | `3D80` | `00111101 10000000` |
| -2 | 2 | `3E80` | `00111110 10000000` |
| -1 | 2 | `3F80` | `00111111 10000000` |
| 0 | 1 | `48` | `01001000` |
| 1 | 1 | `58` | `01011000` |
| 2 | 1 | `68` | `01101000` |
| 3 | 1 | `78` | `01111000` |
| 4 | 1 | `84` | `10000100` |
| 5 | 1 | `8C` | `10001100` |
| 6 | 1 | `94` | `10010100` |
| 7 | 1 | `9C` | `10011100` |
| 8 | 1 | `A2` | `10100010` |
| 9 | 1 | `A6` | `10100110` |
| 10 | 1 | `AA` | `10101010` |
| 14 | 1 | `BA` | `10111010` |
| 15 | 1 | `BE` | `10111110` |
| 16 | 2 | `C110` | `11000001 00010000` |
| 17 | 2 | `C130` | `11000001 00110000` |
| 18 | 2 | `C150` | `11000001 01010000` |
| 31 | 2 | `C3F0` | `11000011 11110000` |
| 32 | 2 | `C910` | `11001001 00010000` |
| 47 | 2 | `CBF0` | `11001011 11110000` |
| 48 | 2 | `D110` | `11010001 00010000` |
| 63 | 2 | `D3F0` | `11010011 11110000` |
| 64 | 2 | `D910` | `11011001 00010000` |
| 79 | 2 | `DBF0` | `11011011 11110000` |
| 80 | 3 | `E00440` | `11100000 00000100 01000000` |
| 81 | 3 | `E004C0` | `11100000 00000100 11000000` |
| 100 | 3 | `E02640` | `11100000 00100110 01000000` |
| 200 | 3 | `E0EC40` | `11100000 11101100 01000000` |
| 500 | 3 | `E64640` | `11100110 01000110 01000000` |
| 1000 | 3 | `EE2C40` | `11101110 00101100 01000000` |
| 1100 | 3 | `EEEE40` | `11101110 11101110 01000000` |
| 1103 | 3 | `EEEFC0` | `11101110 11101111 11000000` |
| 1104 | 3 | `F00088` | `11110000 00000000 10001000` |
| 1105 | 3 | `F00098` | `11110000 00000000 10011000` |
| 2000 | 3 | `F1C088` | `11110001 11000000 10001000` |
| 5000 | 3 | `F78D88` | `11110111 10001101 10001000` |
| 10000 | 6 | `F80000254220` | `11111000 00000000 00000000 00100101 01000010 00100000` |
| 17487 | 6 | `F800009F77E0` | `11111000 00000000 00000000 10011111 01110111 11100000` |
| 17488 | 6 | `F80000A00220` | `11111000 00000000 00000000 10100000 00000010 00100000` |
| 100000 | 6 | `F80005A45220` | `11111000 00000000 00000101 10100100 01010010 00100000` |
| 1000000 | 6 | `F8003C9B7220` | `11111000 00000000 00111100 10011011 01110010 00100000` |

### Probe data — multi-label + decimal segments

| Path | bytes | hex | bits |
|---|------:|---|---|
| `/` | 0 | (empty) | (empty) |
| `/1/2/` | 2 | `5B40` | `01011011 01000000` |
| `/1/1/` | 2 | `5AC0` | `01011010 11000000` |
| `/1/100/` | 3 | `5F0132` | `01011111 00000001 00110010` |
| `/16/16/` | 3 | `C11C11` | `11000001 00011100 00010001` |
| `/-1/-1/` | 3 | `3F9FC0` | `00111111 10011111 11000000` |
| `/-1/1/` | 2 | `3FAC` | `00111111 10101100` |
| `/1/-1/` | 2 | `59FC` | `01011001 11111100` |
| `/0.1/` | 2 | `52C0` | `01010010 11000000` |
| `/0.2/` | 2 | `5340` | `01010011 01000000` |
| `/1.1/` | 2 | `62C0` | `01100010 11000000` |
| `/1.2/` | 2 | `6340` | `01100011 01000000` |
| `/1.3/` | 2 | `63C0` | `01100011 11000000` |
| `/2.1/` | 2 | `72C0` | `01110010 11000000` |
| `/2.10/` | 2 | `7550` | `01110101 01010000` |

### Multi-label composition rule (cracked)

Each label's bit string ends with a `1` terminator bit (whether last label or not). Labels concatenate end-to-end with **no separator**. After the last label, the bit stream is padded to byte boundary with `0`s.

Verified with `/1/2/`: label-1 = `0101` + terminator `1` = `01011`; label-2 = `0110` + `1` = `01101`; concat = `0101101101` (10 bits); pad with 6 `0`s = `01011011 01000000` = `0x5B40` ✓.

Same rule on `/1/100/`: label-1 = `01011` (5 bits), label-100 = `111000000010011001` (18 bits, tier-4 below), concat = `01011111000000010011001` (23 bits), pad 1 `0` = `01011111 00000001 00110010` = `0x5F0132` ✓.

### Per-tier label templates (cracked: 0..3 / 4..7 / 8..15 / 16..79)

In each template, `V` marks a value-bit position (MSB → LSB), `1` and `0` mark fixed structural bits, and the final `1` is the label terminator.

**Tier P0 — positive N=0..3**: `01 VV 1` (5 bits) — value = N (range 0..3).
- `/0/` label = `01 00 1` → byte = `01001 000` = `0x48` ✓
- `/3/` label = `01 11 1` → byte = `01111 000` = `0x78` ✓

**Tier P1 — positive N=4..7**: `100 VV 1` (6 bits) — value = N - 4.
- `/4/` label = `100 00 1` → byte = `100001 00` = `0x84` ✓
- `/7/` label = `100 11 1` → byte = `100111 00` = `0x9C` ✓

**Tier P2 — positive N=8..15**: `101 VVV 1` (7 bits) — value = N - 8.
- `/8/` label = `101 000 1` → byte = `1010001 0` = `0xA2` ✓
- `/15/` label = `101 111 1` → byte = `1011111 0` = `0xBE` ✓

**Tier P3 — positive N=16..79**: 12-bit label with embedded sub-tier markers. Template: `110 VV 0 V 1 VVV 1` where the 6 value bits split as 2 + 1 + 3 (weights 32, 16 || 8 || 4, 2, 1, MSB → LSB). Value = N - 16.
- `/16/` label = `110 00 0 0 1 000 1` → bytes = `11000001 0001 0000` = `0xC110` ✓
- `/79/` label = `110 11 0 1 1 111 1` → bytes = `11011011 1111 0000` = `0xDBF0` ✓
- The fixed `0` at position 5 and `1` at position 7 are sub-tier discriminators (recursive Stern-Brocot structure).

### Per-tier label templates (unfinished work)

**Tier P4 — positive N=80..1103**: 18-bit label. Value-bit positions (from probe XOR analysis): `4, 5, 6, 8, 9, 10, 12, 14, 15, 16` (10 bits). Fixed structural bits: `0` at positions 0..3 = `1110`, `1` at position 7, `1` at position 11, `0` at position 13, terminator `1` at position 17. **Bit-weight ordering only partially confirmed**: bit 16 = LSB (weight 1, verified via /80/-/81/). Needs 3-4 more probes (`/82/`, `/84/`, `/96/`, `/112/`) to lock weights of bits 15, 14, 12, 10, 9, 8, 6, 5, 4.

**Tier P5 — positive N=1104..17487**: 20-bit label (probably). Prefix `11110`. `/1104/` = `1111000000000000 1000 1000` — last `1` at position 20, so label is 21 bits actually. Derive value-bit positions + weight ordering via XOR analysis on `/1105/`, `/2000/`, `/5000/`, `/17487/` (existing probe data has those — work just hasn't been done).

**Tier P6 — positive N=17488..(huge)**: ~42-bit label (6 bytes total). Prefix `1111100`. `/17488/` has the new tier prefix — verify and derive.

**Negative-N tiers**: probe data shows negatives DON'T simply invert the positive bit pattern. `/-1/` = `0x3F80` ≠ bitwise-not(/1/ = `0x58`). `/-72/` = `0x2088` (2 bytes, mirrors tier-P3's 2-byte range 16..79). Hypothesis: negatives use a parallel set of tier prefixes starting with `0` instead of `1`, with value bits possibly XOR'd against a tier-specific mask. Needs an XOR analysis pass like the one done for positives. `/-1/` through `/-72/` map to 2-byte labels; `/-73/` jumps to 3-byte = N5 territory in mirror.

**Decimal sub-ordinals**: probe shows `/0.1/` and `/0.2/` differ from `/0/` only in the lower bits AFTER the label-1 terminator. `/0/` = `0x48` = `01001000` (label `01001` + 3 pad). `/0.1/` = `0x52C0` = `01010010 11000000`. Diff: bits 4..9 in `/0.1/` = `010011`. Hypothesis: sub-ordinals extend the segment with a continuation marker bit somewhere, then add more label bits encoding the sub-ordinal's value with the same tier system. Needs decimal-only probes like `/1.0/` (does this normalize to `/1/`?), `/1.0.0/`, `/1.1.1/`, `/1.4/`, `/1.16/`, `/1.80/` to derive the rule.

### Implementation plan when resumed

1. **Pre-flight probe**: bcp-out a small hierarchyid column to confirm BCP wire format = CAST varbinary form (or document the divergence). The TDS UDT wire format is documented to be the same as CAST, but verify before committing.
2. **Finish tier P4 / P5 / P6 weight ordering**: 3-5 targeted probes per tier, XOR-analyze, lock the templates.
3. **Crack negative-N mirror**: probe `/-1/`, `/-3/`, `/-4/`, `/-8/`, `/-9/`, `/-15/`, `/-16/`, `/-17/`, `/-72/`, `/-73/`, `/-1103/`, `/-1104/`, `/-17487/`.
4. **Crack decimal sub-ordinals**: probe `/1.0/`, `/1.1/`...`/1.79/`, `/1.80/`. Look for tier boundaries within the sub-ordinal.
5. **Replace `HierarchyIdSqlType.Encode` / `Decode`**: write a bit-level writer/reader that emits the per-tier template and concatenates labels with the trailing-`1` + zero-pad rule. The existing `int[][]` internal representation can stay; only the byte form changes.
6. **Switch the existing test suite** to assert byte-level equality against the probe table above. Add 4-5 cross-engine fixture cases — bytes captured from real SQL Server, fed to `CAST(0x… AS hierarchyid)`, with the result asserted via `.ToString()`.
7. **Document the change** in CLAUDE.md and trim this section's deferred notes.

### Probe scaffold (one-shot rebuild)

Rebuild via `dotnet new console` + `Microsoft.Data.SqlClient` reference, connect to the reference SQL Server 2025, `SELECT CAST(hierarchyid::Parse('/N/') AS varbinary(900))` for each N. The full pre-deletion probe source was ~150 lines and is reconstructable in 5 minutes from this section's probe-table column structure alone.
