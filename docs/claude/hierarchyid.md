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

## Storage — canonical OrdPath bytes

The in-memory representation **is** SQL Server's OrdPath binary encoding (the byte form a real server stores). `SqlValue` holds a `byte[]` in its reference slot; the page codec, `CAST … AS varbinary`, the TDS UDT wire arm, and `DATALENGTH` are all the same buffer — zero re-encoding, byte-identical to a real server. The segment-array (`int[][]`) form is a transient decode used only by `ToString()` / the instance methods.

- `SqlValue.FromHierarchyId(int[][])` — encodes the path to OrdPath bytes (used by `Parse`, `GetRoot`, and every method that builds a result path).
- `SqlValue.FromHierarchyIdBytes(byte[])` — stores raw bytes verbatim (BACPAC import, ADO.NET byte parameters, the reverse CAST after it validates). **Opaque passthrough**: bytes in an unmodeled tier still round-trip through storage/export even though `ToString()` can't decode them.
- `SqlValue.AsHierarchyId` — decodes bytes → `int[][]` on demand.
- `SqlValue.AsHierarchyIdBytes` — the raw OrdPath bytes (zero-copy).

Comparison / equality / `ORDER BY` / hash are **unsigned bytewise** on those bytes — OrdPath's defining property is that memcmp equals depth-first pre-order traversal, so this is byte-for-byte real's index-key order for free. `HierarchyIdSqlType.Encode/Decode/GetVariableByteCount` (`Storage/HierarchyIdType.cs`) are a verbatim byte copy; the root `/` is 0 bytes and `DATALENGTH('/') = 0`.

`sys.types` row: `system_type_id=240`, `user_type_id=128`, `max_length=892`.

## OrdPath codec — `Storage/HierarchyIdOrdPath.cs`

`HierarchyIdOrdPath.Encode(int[][])` / `.Decode(bytes)` are the single canonical codec. Each label of a path is a self-delimiting bit sequence — a prefix-free tier code, value bits interleaved with fixed structural bits, then a terminator bit; labels concatenate with no separator and the stream zero-pads to a byte boundary. A **dotted sub-ordinal** (`/1/2.3/` = `[[1],[2,3]]`) encodes every non-final label of a segment as `ordinal + 1` with terminator `0`, the last label normally with terminator `1` — the order-preserving trick that sorts a dotted continuation after the plain node and before its next sibling. The decoder reads dotted forms back via the terminator bit (the previous decoder could not).

**Modeled tier domain: ordinals −4168 .. 5199** (twelve tiers, every boundary byte-anchored by a live SQL Server 2025 probe, 2026-07-17). `V` = value-bit group MSB→LSB, digit = fixed structural bit, final bit = terminator; ordinal = base + value:

| Prefix | Layout | Ordinal range |
|---|---|---|
| `01` | `VV 1` | 0..3 |
| `100` | `VV 1` | 4..7 |
| `101` | `VVV 1` | 8..15 |
| `110` | `VV 0 V 1 VVV 1` | 16..79 |
| `1110` | `VVV 0 VVV 0 V 1 VVV 1` | 80..1103 |
| `11110` | `VVVVV 0 VVV 0 V 1 VVV 1` | 1104..5199 |
| `0011` | `VVVV 1` | −8..−1 |
| `0010` | `VV 0 V 1 VVV 1` | −72..−9 |
| `00011011` | `VVV 0 VVV 0 V 1 VVV 1` | −1096..−73 |
| `00011010` | same layout | −2120..−1097 |
| `00011001` | same layout | −3144..−2121 |
| `00011000` | same layout | −4168..−3145 |

Ordinals outside that window (the wider 6-byte tiers, e.g. `/100000/` prefix `111110`, or below −4168) raise `NotSupportedException` on encode/decode; storage still round-trips their bytes opaquely.

**Reverse CAST `CAST(varbinary AS hierarchyid)` is strict** — probe-confirmed 2026-07-17 that SQL Server rejects any non-canonical byte string (wrong pad bits: `0x59` vs canonical `0x58`; all-zero non-empty `0x00`; garbage prefix; trailing bytes) with the .NET-UDR error (Msg 6522). `HierarchyIdOrdPath.DecodeCanonical` enforces this by decoding then re-encoding and requiring byte equality, so canonicalization is checked by construction. `0x` (empty) → root `/`.

Anchored test vectors live in `HierarchyIdOrdPathTests` (Tests.Internal); the byte-identical CAST + reverse-CAST + multi-tier ORDER BY probes live in `HierarchyIdTests`; the wire form in `HierarchyIdWireTests` (Tests.SqlClient).

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
