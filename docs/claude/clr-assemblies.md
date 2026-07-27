# CLR assemblies and `EXTERNAL NAME` routines

`CREATE ASSEMBLY` registers a .NET assembly from raw bytes; `CREATE FUNCTION … AS EXTERNAL NAME` binds a scalar function to a static method inside it.
Behavior below was probed against the live SQL Server 2025 reference (17.0.4065.4) unless flagged otherwise.

## The `EnableClr` gate

`Simulation.EnableClr` is an `init`-only `bool`, default `false`.
With it off, `CREATE ASSEMBLY` raises `NotSupportedException` naming the property, and no bytes are ever read.

**This is a host-trust decision, not a fidelity one.**
Real SQL Server confines a `SAFE` assembly with Code Access Security.
.NET removed CAS and ships no in-process replacement, so a registered assembly runs with the host process's full trust regardless of the `PERMISSION_SET` its DDL names.
The gate exists because a `Simulation` reachable over the network endpoint would otherwise let any client that can issue DDL execute arbitrary code in the host process.

The gate is deliberately stricter than real: probe-confirmed, real SQL Server's `clr enabled` option does **not** gate `CREATE ASSEMBLY` at all — registration succeeds with the option set to 0, and only *execution* raises Msg 6263.

`sys.configurations` reports `clr enabled` as `EnableClr ? 1 : 0`, and `clr strict security` drops from real's default of 1 to 0 once CLR is enabled (the simulator gates on the host opt-in, not on assembly signing, so reporting 1 would claim an enforcement it does not perform).
That pairing is what lets mssql-django's `enable_clr()` run: it reads `clr enabled` from `sys.configurations` and only falls through to `sp_configure` when the value is 0, so no configuration-write model is needed.

## Grammar

```
CREATE ASSEMBLY <name> [AUTHORIZATION <owner>] FROM 0x<hex> [WITH PERMISSION_SET = { SAFE | EXTERNAL_ACCESS | UNSAFE }]
DROP ASSEMBLY [IF EXISTS] <name> [, …] [WITH NO DEPENDENTS]
CREATE FUNCTION <name> (<params>) RETURNS <type> AS EXTERNAL NAME <assembly>.<class>.<method>
```

`PERMISSION_SET` defaults to `SAFE`.
`AUTHORIZATION` parses and is discarded — assembly ownership by a named principal isn't modeled, so every assembly reports `principal_id` 1 (dbo).
`FROM '<path>'` raises `NotSupportedException`: the simulator has no server-side filesystem.

The class segment is commonly bracketed (`asm.[Namespace.Class].Method`) because a namespace-qualified name contains dots; the object-name parser keeps a bracketed segment whole, so both spellings resolve.

Assemblies are **database-scoped**, living in `Database.Assemblies` rather than on a `Schema`, and carry an `assembly_id` rather than an `object_id`.
User assembly ids start at 65536 (real was observed handing out 65538 for a first user assembly).

## Storage and lifetime

`Schemas/SqlAssembly.cs` retains the supplied bytes verbatim so `sys.assembly_files.content` round-trips exactly what the DDL provided.
The executable form is materialized lazily on first use into a dedicated **collectible** `AssemblyLoadContext`; `DROP ASSEMBLY` unloads it, so a `DROP` / re-`CREATE` cycle under the same name starts from a clean context rather than resurrecting the old types.
Unloading is cooperative — the CLR reclaims the context once no managed references survive — so correctness never depends on it completing.

**Never read custom attributes off a loaded SQLCLR assembly.**
A .NET Framework-built SQLCLR assembly decorates its routines with `Microsoft.SqlServer.Server.SqlFunctionAttribute`, which type-forwards to `System.Data.SqlClient` — an assembly that does not exist on modern .NET, so any `GetCustomAttributes` call throws `FileNotFoundException`.
Binding goes through the `EXTERNAL NAME` triple instead, which names type and method directly and needs no attribute resolution.
This is load-bearing: it is the single reason a Framework 2.0 assembly runs on .NET 10 at all.

## Static verification

`Clr/ClrAssemblyMetadata.cs` validates candidates from metadata only, via `System.Reflection.Metadata` — nothing is loaded, so a rejected assembly never gets to run a module initializer.

| Check | Error |
| --- | --- |
| Not a managed PE / not an assembly / not IL-only | **Msg 6544** — `… is malformed or not a pure .NET assembly. Unverifiable PE Header/native stub.` |
| `AssemblyRef` outside the framework allow-list | **Msg 6503** — `Assembly '<lowercase identity>.' was not found in the SQL catalog.` |
| P/Invoke declaration (`ImplMap` rows), SAFE only | **Msg 6218** — failed verification |
| Reference to a denied type or namespace, SAFE only | **Msg 6218** — failed verification |
| Mutable (non-`initonly`, non-`literal`) static field, SAFE only | **Msg 6211** |
| Module MVID already registered under another name | **Msg 6285** |
| Name already registered | **Msg 6246** |

`EXTERNAL_ACCESS` and `UNSAFE` opt out of the API restrictions, matching real's permission ladder; the malformed / reference / MVID / duplicate-name checks apply at every permission set.

The denylist is **type-level, not namespace-level**, for `System.IO` / `System.Reflection` / `System.Runtime.InteropServices`: every compiled assembly carries `System.Reflection.Assembly*Attribute` and `ComVisibleAttribute` type references from its own custom attributes, so denying those namespaces wholesale would reject ordinary assemblies — including `regex_clr.dll`.
Whole-namespace prefixes are denied only where no attribute traffic exists (`System.Net`, `System.Reflection.Emit`, `System.Runtime.Loader`, `Microsoft.Win32`, `System.Diagnostics.Process`, `System.Security.Permissions`).

**This is defense in depth, not a sandbox.**
A metadata denylist cannot stop a determined assembly — string-driven reflection and unlisted APIs remain reachable — and .NET offers no in-process isolation to fall back on.
The real control is `EnableClr`.

## Type mapping

Strict and one-to-one, matching real (probe-confirmed that `varchar` does **not** bind to `SqlString`, and `bit` / `bigint` do **not** bind to `SqlInt32`).

| T-SQL | CLR |
| --- | --- |
| `nvarchar` / `nchar` (any length, incl. MAX) | `SqlString` |
| `int` / `bigint` / `smallint` / `tinyint` | `SqlInt32` / `SqlInt64` / `SqlInt16` / `SqlByte` |
| `bit` | `SqlBoolean` |
| `float` / `real` | `SqlDouble` / `SqlSingle` |
| `decimal` / `numeric` | `SqlDecimal` |
| `money` / `smallmoney` | `SqlMoney` |
| `datetime` / `smalldatetime` | `SqlDateTime` |
| `varbinary` / `binary` | `SqlBinary` |
| `uniqueidentifier` | `SqlGuid` |
| `xml` | `SqlXml` |

Binding happens at CREATE time so the diagnostics fire there rather than at first call, matching real: **Msg 6528** (unknown assembly), **Msg 6505** (unknown type, state 2), **Msg 6506** (unknown method — real's text has no terminating period), **Msg 6550** (arity mismatch), **Msg 6551** (return type), **Msg 6552** (parameter type, state 3).

NULL arguments marshal to the CLR struct's own `Null` sentinel, not to a CLR `null` — a SQLCLR routine is expected to test `IsNull` itself.
Real only short-circuits NULL input when the routine opted into `RETURNS NULL ON NULL INPUT`, which the simulator does not accept on a CLR routine.

Anything the routine throws surfaces as **Msg 6522**, carrying the exception type and message.

## Catalog surface

- **`sys.assemblies`** — one row per registered assembly plus the `Microsoft.SqlServer.Types` system row real always carries (assembly_id 1, principal_id 4, `UNSAFE_ACCESS`, `is_user_defined` 0).
  That system row is what `sys.assembly_types` joins against; before CLR shipped, this view was empty and that join yielded nothing.
- **`sys.assembly_files`** — the verbatim bytes plus SHA-256 / SHA-512 digests. Real also carries a row for the system assembly; the simulator has no bytes to project for it, so only user assemblies appear.
- **`sys.assembly_modules`** — one row per bound routine (`assembly_class` / `assembly_method`). `null_on_null_input` is constant 0; `execute_as_principal_id` NULL.
- **`sys.objects`** — a CLR scalar function is type `FS` / `CLR_SCALAR_FUNCTION`, with **no** `sys.sql_modules` row (probe-confirmed).
- **`ASSEMBLYPROPERTY(name, property)`** → `sql_variant`. Supports `CLRName`, `PublicKey`, `Culture`, `VersionMajor` / `VersionMinor` / `VersionBuild` / `VersionRevision`, `SimpleName`, `Architecture`, `MvID`. Unknown assembly or property → NULL.

`clr_name`'s embedded version reads `0.0.0.0` for an unsigned assembly even though `VersionMajor` and friends report the real manifest version off the same bytes — probe-confirmed against `regex_clr.dll`, whose manifest says `1.0.5100.29893` while real projects `regex_clr, version=0.0.0.0, culture=neutral, publickeytoken=null, processorarchitecture=msil`.
Version participates in binding only for strong-named assemblies, so a simple name binds version-agnostically.
The strong-named case is unprobed.

## Divergences

- **Only .NET Framework-targeted assemblies load on real; the simulator accepts any framework target.**
  Real resolves every `AssemblyRef` against a fixed catalog of .NET Framework assemblies and raises Msg 6503 for anything else — probe-confirmed for .NET 10 (`system.data.common, version=10.0.0.0`) *and* for .NET Standard 2.0 (`netstandard, version=2.0.0.0, culture=neutral, publickeytoken=cc7b13ffcd2ddd51`).
  netstandard is not rejected for being new: the catalog simply has no `netstandard.dll`, so the reference fails before any IL is considered.
  Authoring an assembly real will accept therefore still means targeting `net4x` — which is why `regex_clr.dll` is a Framework 2.0 binary that has never needed re-targeting.
  The simulator runs on .NET, so all three resolve and the allow-list admits them.
  This is the over-permissive direction, and it is what lets the tests emit a fixture assembly without a .NET Framework toolchain.
- **`REGEXP_LIKE` is not reserved.**
  SQL Server 2025 reserves `REGEXP_LIKE` as a native predicate at **compatibility level 170**, so `dbo.REGEXP_LIKE(…)` — the unbracketed form mssql-django emits for Django `__regex` lookups — raises Msg 156 there and needs `dbo.[REGEXP_LIKE](…)`.
  At compat 160 and below the unbracketed form resolves normally (probe-confirmed both ways on the same server).
  The simulator defaults to compat 170 but does not reserve the keyword, so it accepts the unbracketed form at every level — over-permissive at 170, matching at ≤160.
  Closing this belongs with the native `REGEXP_LIKE` predicate, which is tracked separately in [`backlog.md`](backlog.md).
- **`PERMISSION_SET` is recorded, not enforced at run time.** It selects which static checks run at registration; it cannot confine a loaded assembly (see above).
- Auto-generated `assembly_id` values start at 65536 and increment; they won't match a real server's.
- The `Microsoft.SqlServer.Types` system row reports a fixed SQL Server 2025 RTM `create_date` / `modify_date` rather than a resource-database build stamp.

## Not modeled yet

- **CLR stored procedures, table-valued functions, aggregates, and UDTs.**
  These reference `Microsoft.SqlServer.Server.SqlContext` / `SqlPipe` / `SqlDataRecord` / `SqlMetaData`, which lived in .NET Framework's `System.Data.dll` and are absent from .NET's facade — a routine using them fails to load unless the load context supplies a substitute `System.Data` that type-forwards `SqlTypes` onward and adds the missing namespace.
  Scalar functions need no such shim, which is why they ship first.
- Plain-CLR parameter and return forms real also accepts (`string`, `int?`, `SqlChars`, `SqlBytes`) — only the `System.Data.SqlTypes` family binds.
- `ALTER ASSEMBLY`, `CREATE ASSEMBLY … FROM '<path>'`, assembly `AUTHORIZATION`, `sp_add_trusted_assembly`, and assembly signing / `clr strict security` enforcement.
- BACPAC round-trip of `SqlAssembly` model elements.
- Out-of-process execution.
  Measured cost of a cross-process round trip is ~56 µs versus ~0.12 µs for an in-process cached delegate — roughly 470× — and a child process is not a sandbox without per-OS restriction work (seccomp/namespaces, restricted tokens, `sandbox_init`), so it is only worth building together with that.
