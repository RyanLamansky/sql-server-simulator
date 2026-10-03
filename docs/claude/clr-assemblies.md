# CLR assemblies and `EXTERNAL NAME` routines

`CREATE ASSEMBLY` registers a .NET assembly from raw bytes; `CREATE FUNCTION` / `CREATE PROCEDURE` / `CREATE TRIGGER … AS EXTERNAL NAME` bind a scalar function, a table-valued function, a procedure or a trigger to a static method inside it, `CREATE AGGREGATE … EXTERNAL NAME` binds a user-defined aggregate to a class, and `CREATE TYPE … EXTERNAL NAME` a user-defined type.
A routine reaches back into the calling session through the context connection, `new SqlConnection("context connection=true")`.
Behavior below was probed against the live SQL Server 2025 reference (17.0.4065.4) unless flagged otherwise; the procedure, table-valued function, aggregate, user-defined type, trigger and context-connection behavior was probed 2026-09-28 with the same .NET Framework 4.8 classes `ClrFrameworkFixture` compiles.

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
CREATE FUNCTION <name> (<params>) RETURNS TABLE (<columns>) [ORDER (<columns>)] [WITH <options>] AS EXTERNAL NAME <assembly>.<class>.<method>
CREATE PROCEDURE <name> [<params>] [WITH <options>] AS EXTERNAL NAME <assembly>.<class>.<method>
CREATE AGGREGATE <name> (<params>) RETURNS <type> EXTERNAL NAME <assembly>.<class>
DROP AGGREGATE [IF EXISTS] <name> [, …]
CREATE TYPE <name> EXTERNAL NAME <assembly>.<class>
CREATE TRIGGER <name> ON { <table> | <view> | DATABASE } [WITH <options>] { AFTER | FOR | INSTEAD OF } <events> AS EXTERNAL NAME <assembly>.<class>.<method>
```

`PERMISSION_SET` defaults to `SAFE`.
`AUTHORIZATION` parses and is discarded — assembly ownership by a named principal isn't modeled, so every assembly reports `principal_id` 1 (dbo).
`FROM '<path>'` raises `NotSupportedException`: the simulator has no server-side filesystem.

The class segment is commonly bracketed (`asm.[Namespace.Class].Method`) because a namespace-qualified name contains dots; the object-name parser keeps a bracketed segment whole, so both spellings resolve.

`CREATE AGGREGATE` is the odd one out: its parameter list needs its parentheses (`@v int` bare is Msg 102), `EXTERNAL NAME` follows `RETURNS` with no `AS` (Msg 156 at `AS`), and it names a class alone — a method segment is Msg 102 at its dot.

Assemblies are **database-scoped**, living in `Database.Assemblies` rather than on a `Schema`, and carry an `assembly_id` rather than an `object_id`.
User assembly ids start at 65536, a fresh database's first on real (probed 2026-09-28; a server that has seen other assemblies was observed handing out 65538).

## Storage and lifetime

`Schemas/SqlAssembly.cs` retains the supplied bytes verbatim so `sys.assembly_files.content` round-trips exactly what the DDL provided.
The executable form is materialized lazily on first use into a dedicated **collectible** `AssemblyLoadContext`; `DROP ASSEMBLY` unloads it, so a `DROP` / re-`CREATE` cycle under the same name starts from a clean context rather than resurrecting the old types.
Unloading is cooperative — the CLR reclaims the context once no managed references survive — so correctness never depends on it completing.

## The load-context shim

A .NET Framework SQLCLR assembly references `System.Data, Version=4.0.0.0` for both `System.Data.SqlTypes` and `Microsoft.SqlServer.Server`.
.NET's own `System.Data` facade forwards the first family onward, but sends the second to `System.Data.SqlClient` — an assembly that doesn't exist — and has no `SqlContext` or `SqlPipe` at all.
So every registered assembly loads into a `SqlAssemblyLoadContext` (`Clr/ClrHost.cs`) that answers two names itself:

- **`Microsoft.SqlServer.Server`** is the `SqlServerSimulator.ClrShim` project: `SqlContext`, `SqlPipe`, `SqlDataRecord`, `SqlMetaData`, `SqlTriggerContext`, the routine attributes and their enums (`TriggerAction` with Framework's whole DDL roster), `IBinarySerialize` and `InvalidUdtException`, and the in-process provider's `System.Data.SqlClient` types — `SqlConnection`, `SqlCommand`, `SqlParameter` and its collection, `SqlDataReader`, `SqlTransaction`, `SqlException` / `SqlError` and `SqlInfoMessageEventArgs` — with Framework's member signatures for everything it carries (see Not modeled yet for what it doesn't).
  The simulator embeds its build as a resource and never references it, so none of its public types reach a consumer; its name matches the NuGet package's, so a .NET-targeted assembly built against that package lands on the same types.
- **`System.Data`** is generated at first use: a manifest-only assembly whose every type is a forwarder — the runtime facade's own list, each entry pointed at the assembly it really resolves to, plus one per public type of the shim, which is what sends a Framework assembly's `System.Data.SqlClient` references there rather than to the client assembly the facade names.
  Forwarding rather than copying is what keeps `SqlInt32` and friends the very types `ClrTypeMarshaller` handles.

Both load once per process into a non-collectible context the collectible per-assembly contexts share, so `DROP ASSEMBLY` never unloads them, and nothing is built until an assembly referencing either name first loads.
The shim reaches the simulator through one internal bridge (`SimulatorBridge.Enter` / `Exit`) bound by reflection, passing only framework types; the routine's context is thread-static there, opened around every call into an assembly that references either name.

With the attribute types resolvable, binding reads `SqlFunction(FillRowMethodName = …)`, `SqlUserDefinedAggregate(Format.…)`, `SqlUserDefinedType(…)` and `SqlMethod(IsMutator = …)` as `CustomAttributeData`, matched by full name and never instantiated.

The tests reach this path with a real Framework-shaped assembly: `ClrFrameworkFixture` compiles its C# 7.3 source through Roslyn against the .NET Framework 4.8 reference assemblies the test project copies into `net48ref/`, the same source the probes registered on SQL Server.
`ClrAssemblyFixture`'s emitted .NET-targeted assemblies cover the scalar binding and the static verification.

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

The C# compiler's lambda cache — `<>c.<>9__0_0`, a static field it never marks `initonly` — is exempt from Msg 6211: real registers a SAFE assembly holding one (probed 2026-09-28).

`EXTERNAL_ACCESS` and `UNSAFE` opt out of the API restrictions, matching real's permission ladder; the malformed / reference / MVID / duplicate-name checks apply at every permission set.

The denylist is **type-level, not namespace-level**, for `System.IO` / `System.Reflection` / `System.Runtime.InteropServices`: every compiled assembly carries `System.Reflection.Assembly*Attribute` and `ComVisibleAttribute` type references from its own custom attributes, so denying those namespaces wholesale would reject ordinary assemblies — including `regex_clr.dll`.
Whole-namespace prefixes are denied only where no attribute traffic exists (`System.Net`, `System.Reflection.Emit`, `System.Runtime.Loader`, `Microsoft.Win32`, `System.Diagnostics.Process`, `System.Security.Permissions`).
`System.Environment` is admitted for a like reason: every C# iterator method compiles to a class whose `GetEnumerator` reads `Environment.CurrentManagedThreadId`, and an iterator is the ordinary way to write a table-valued function's init method, which real registers as `SAFE`.

**This is defense in depth, not a sandbox.**
A metadata denylist cannot stop a determined assembly — string-driven reflection and unlisted APIs remain reachable — and .NET offers no in-process isolation to fall back on.
The real control is `EnableClr`.

## Type mapping

Strict and one-to-one, matching real (probe-confirmed that `varchar` does **not** bind to `SqlString`, and `bit` / `bigint` do **not** bind to `SqlInt32`).

| T-SQL | CLR |
| --- | --- |
| `nvarchar` / `nchar` (any length, incl. MAX) | `SqlString` (`nchar` probed as a table-valued function column) |
| `int` / `bigint` / `smallint` / `tinyint` | `SqlInt32` / `SqlInt64` / `SqlInt16` / `SqlByte` |
| `bit` | `SqlBoolean` |
| `float` / `real` | `SqlDouble` / `SqlSingle` |
| `decimal` / `numeric` | `SqlDecimal` |
| `money` / `smallmoney` | `SqlMoney` |
| `datetime` / `smalldatetime` | `SqlDateTime` |
| `varbinary` / `binary` | `SqlBinary` |
| `uniqueidentifier` | `SqlGuid` |
| `xml` | `SqlXml` |

Every row round-trips a value in and back out, `ClrAssemblyTests.ClrFunction_TypeBinding_RoundTripsAndCarriesNull` driving one identity routine per pair off `ClrAssemblyFixture.EchoTypes`.
The two pairs sharing a CLR type (`money` / `smallmoney`, `datetime` / `smalldatetime`, `varbinary` / `binary`) are separate rows there because the *declared* T-SQL type is what the return conversion reads.

Binding happens at CREATE time so the diagnostics fire there rather than at first call, matching real: **Msg 6528** (unknown assembly), **Msg 6505** (unknown type, state 2), **Msg 6506** (unknown method — real's text has no terminating period), **Msg 6550** (arity mismatch), **Msg 6551** (return type), **Msg 6552** (parameter type, state 3).
A CLR module's errors at CREATE name it as their procedure, save an aggregate's, which name none.

NULL arguments marshal to the CLR struct's own `Null` sentinel, not to a CLR `null` — a SQLCLR routine is expected to test `IsNull` itself.
Real only short-circuits NULL input when the routine opted into `RETURNS NULL ON NULL INPUT`, which the simulator does not accept on a CLR routine.

A string return value or output parameter longer than its declared `nvarchar(n)` is not cut: real raises the server's own `TruncationException` inside Msg 6522 (Msg 6260 from `FillRow`), sized in bytes.

## What a throw reports

Anything a routine throws surfaces as **Msg 6522** — state 1 from a procedure, state 2 from a scalar function, a table-valued function's init method and an aggregate, save a function marked to read data or taking a `max`-typed parameter, which is state 1 — and a throw from `FillRow` as **Msg 6260** state 1.
The text carries the exception as the server's host writes it (`Clr/ClrExceptionReport.cs`): `type: message`, then the type alone, then an `   at …` line per frame, every line CRLF-ended, and an argument exception's parameter on a `Parameter name:` line of its own as .NET Framework words it.
Only the frames a routine author can see are kept — the routine's assembly and the public members of the shim — so the reflection plumbing never shows, as the server's hosting frames never do.
The optimizing JIT turns a routine's last call into a tail call, which removes the routine's own frame, so the report restores the method the simulator invoked as its last frame; a frame a tail call removed from between the two can't be recovered.

## Procedures

`CREATE PROCEDURE … AS EXTERNAL NAME` binds after the assembly / class / method lookups: the parameter count (Msg 6550), the return type — `void`, `int`, `int?` or `SqlInt32`, else **Msg 6567** — then each parameter, where an `OUTPUT` parameter must be a CLR `ref` / `out` one and the reverse (**Msg 6580** followed by the Msg 6552 its type would raise).
`WITH EXECUTE AS` is accepted; `RECOMPILE`, `ENCRYPTION` and `NATIVE_COMPILATION` are **Msg 155** state 37.
`ALTER` of a T-SQL procedure into a CLR one is **Msg 6530** naming the leaf, and the reverse the ordinary Msg 2010; the same pair holds between T-SQL and CLR functions.
A CLR procedure takes no group number: `CREATE PROCEDURE p;2 … EXTERNAL NAME` creates `p` itself, which then answers to any `EXEC p;N` — though a name already taken is Msg 2714 at state 51 there.

A call binds its arguments as a T-SQL procedure's do, save that a conversion failure is Msg 8114 at state 1 rather than 5.
What the procedure sends through `SqlContext.Pipe` joins the call's outcomes in order:

- `Send(string)` is an informational message at class 0, **state 2**, line 0, naming the procedure as the call spelled it; over 4000 characters, or NULL, the pipe throws.
- `Send(SqlDataRecord)` and `SendResultsStart` / `SendResultsRow` / `SendResultsEnd` are result sets typed by the record's `SqlMetaData`, character columns in the database collation.
  The pipe's misuse refusals (a row before its start, a message while a result set is open) throw its own exceptions and texts.
- A result set left open when the procedure returns is sent then; one the procedure throws out of is sent with the rows it had and cut short by the error, and not at all when it had none.

`SqlDataRecord` accepts each typed setter only for the column types it can write and `SetValue` only a value whose CLR type one of them takes — an `int` into a `bigint` column is `InvalidCastException` — while a string or binary value longer than its column is cut silently, and a decimal with more scale than its column drops the extra digits rather than rounding.
`ref` / `out` values write back only when the procedure returns, to the caller's variable at its own width; the return value is the status, 0 for `void`.
A CLR procedure feeds `INSERT … EXEC` and `EXEC … WITH RESULT SETS` like a T-SQL one, and `sp_describe_first_result_set` refuses it with **Msg 11515**.

## Table-valued functions

`RETURNS TABLE (<columns>)` declares the result; the method named returns the rows as an `IEnumerable` or an `IEnumerator` (else Msg 6551), and the method its `SqlFunction(FillRowMethodName = …)` names splits each row object into one `out` parameter per column.
Before any binding the result table refuses what a streamed row can't carry: `varchar` / `char` / `text` / `ntext` / `image` (**Msg 6514** state 3), `IDENTITY` (6514 state 2), `timestamp` / `rowversion` (6514 state 1, named in capitals), `NOT NULL`, `DEFAULT` and a column `CHECK` (**Msg 6526**), and a key (**Msg 6525**); `WITH SCHEMABINDING` is Msg 487.
Then: no `FillRowMethodName` is **Msg 10306**, a missing method Msg 6506, a `FillRow` whose parameter count isn't one more than the column count **Msg 6208**, and the first column whose `out` parameter doesn't bind to its type **Msg 6258**.
The `ORDER (…)` clause parses and has no effect.
The function is called from `FROM` and `APPLY` like the T-SQL kinds, one-part names included, `DEFAULT` arguments reading the declared default; a NULL collection is no rows.

## Aggregates

`CREATE AGGREGATE` binds the class, reporting in order: a missing class (**Msg 6556**), a class without `SqlUserDefinedAggregate` (**Msg 6255**), a `Format.Native` class holding a reference-type field (**Msg 6225**), the first of `Init` / `Accumulate` / `Merge` / `Terminate` that is missing or misshaped (**Msg 6558**, an `Accumulate` of another arity and a `Terminate` of another type than `RETURNS` included), then an `Accumulate` parameter whose type doesn't bind (Msg 6552 as `CREATE for "…"`).
Msgs 6556 and 6558 are followed by **Msg 6597**; a parameter default is **Msg 10726**.
`CREATE AGGREGATE` / `DROP AGGREGATE` fire no DDL trigger event.

An aggregate lives among the schema's functions — one namespace, Msg 2714 on a clash — but answers only to `DROP AGGREGATE` (`DROP FUNCTION` is Msg 3705 either way round, a missing name Msg 3701 as `aggregate function`) and `EXEC` of it is **Msg 2809**.
A call must be schema-qualified (a one-part name is Msg 195) and take the declared argument count (Msg 174); it may lead with `DISTINCT` or `ALL`, runs under `GROUP BY` and `HAVING` and in a window of `PARTITION BY` alone (an `ORDER BY` there is Msg 156), and obeys the ordinary aggregate binding rules (Msg 130, 147, 8120).
Each group gets a fresh instance, `Init`, one `Accumulate` per row — NULLs included, with no Msg 8153 — and `Terminate`, which an empty input still reaches.

## User-defined types

`CREATE TYPE … EXTERNAL NAME assembly.[class]` binds the class and registers the type among the database's alias types, which is what gives it their namespace (Msg 219 on a taken name), `TYPE_ID`, `DROP TYPE` and its Msg 3732 while a column or parameter uses it; `Schemas/ClrUserDefinedType.cs` holds the binding and `Storage/ClrUdtSqlType.cs` the storage type, one instance per registered type.
A third name part is Msg 102 at its dot, a missing assembly **Msg 6267** (not the routines' Msg 6528), a missing class **Msg 6556**, and a class already mapped by another type **Msg 8188**, which ends the batch.
Then the class, in this order:

| Refusal | Error |
| --- | --- |
| No `SqlUserDefinedType` attribute | **Msg 6255** state 2 |
| `Format.Native` class not `LayoutKind.Sequential` | **Msg 6229** |
| `Format.Native` reference-typed field | **Msg 6225** |
| `Format.Native` value field it can't carry (`decimal`, `DateTime`, `char`, an enum) | **Msg 6222** |
| `Format.UserDefined` without `IBinarySerialize` | **Msg 6226** |
| `Format.UserDefined` `MaxByteSize` outside -1 and 1–8000 | **Msg 6244** |
| No `INullable` / no static `Null` / no static `Parse(SqlString)` | **Msg 6577** / **6557** / **6558**, each followed by Msg 6597 |

`[Serializable]` isn't required (probed: a class without it registers).
The order among the refusals is the simulator's; each probed class failed one check only.

### Storage

A value is the class's serialization, which is what `CAST(x AS varbinary)`, `DATALENGTH`, the wire and a duplicate-key message (upper-case hex) show.
`Format.Native` (`Clr/ClrNativeLayout.cs`) writes every instance field in declaration order at a fixed width, byte-identical to real across every primitive, the `System.Data.SqlTypes` structs and a nested struct (probed through `CAST(… AS varbinary)`): integers big-endian with the sign bit flipped, unsigned ones plain, a float's bits with the sign bit flipped when positive and every bit inverted when negative, a SqlTypes struct its not-null byte then the value.
Its width is the type's `max_length` and it reports `is_fixed_length` 1.
`Format.UserDefined` stores what `IBinarySerialize.Write` writes into a buffer bounded by `MaxByteSize`; writing past it throws the `SqlTypeException` real's buffer throws, inside the class's own `Write`.
An unlimited `MaxByteSize` (-1) is `max_length` -1 and stores off-row.

### Conversions and comparison

The type shares `hierarchyid`'s row and column of the type-pair grids, which real's answers matched:

- A character string converts to it implicitly through `Parse`, including an assignment from a literal, and back only explicitly through `ToString()`; a binary converts both ways explicitly as the serialized bytes, a native type's wrong length being **Msg 6235** (which ends the batch) and a `binary(n)` wider than the value **Msg 6207**; `xml` converts both ways through the class's `XmlSerializer` document; `TRY_CAST` / `TRY_CONVERT` read a throwing `Parse` as NULL.
- Every other conversion is Msg 529, naming the type `database.schema.type`; an implicit one Msg 257 naming it bare; a different CLR type, or `hierarchyid`, **Msg 206** — two CLR types named `schema.type`; arithmetic Msg 403; `CONCAT` Msg 257.
- A type marked `IsByteOrdered` compares, sorts, groups, keys an index and a constraint, and joins by its bytes.
  One that isn't is `SqlType.IsIncomparable`: Msg 403 for a comparison, 249 in `ORDER BY` / `GROUP BY`, 421 for `DISTINCT`, 1978 for an index key and 1919 for a key constraint.
- A temp table can't use it (Msg 2715 state 6): the type lives in the user database, as an alias type does.
  `SELECT … INTO` a temp table meets the same wall as Msg 6220, which ends the batch (probed 2026-09-28 against SQL Server 2025).

### Members

`Parser/Expressions/ClrTypeMemberCall.cs` binds a member while the statement compiles, off the receiver's type — a variable, a column the query scope, an `UPDATE`'s target or a `CREATE TABLE` list binds, or any expression of the type — ahead of the name-driven `hierarchyid` / `xml` / spatial dispatch whose method names (`ToString`, `value` …) a class may declare too:

- `x.Property` / `x.Field`, `x.Method(…)` and `Type::StaticMethod(…)` / `Type::[StaticProperty]` (one- or two-part type name); a type-scope name that isn't a type is Msg 243 state 4.
- A missing property is **Msg 6592**, a missing method **Msg 6506** state 10, the wrong argument count Msg 174, an instance member through `::` **Msg 6584**, a mutator read for its value **Msg 6200**, and a member of a member's system-typed result Msg 258.
- The result types map as the SQLCLR routines' do, plain CLR primitives included; a string is `nvarchar(4000)` — longer is the server's truncation error inside Msg 6522 at **state 1** — and a CLR type is itself.
- A NULL receiver reads NULL without calling in; a NULL argument of a CLR type passes its `Null` instance.
- A throw is Msg 6522 state 2 naming the type.
  `Parse` failing over a written constant is state 1 where real meets it folding the constant as the batch compiles, which it does for a statement naming permanent tables or views and no `#temp` table or table variable — `CAST('bad' AS Point)` or `'bad'` written to a column of the type, anywhere a top-level constant folds; a variable's or a column's value, a statement naming no table, a temporary object or only a catalog view, and a constant inside a subquery stay state 2 (probed 2026-09-28 against SQL Server 2025).
  `ConstantFolding.FoldsClrParseFailure` restates it at the sites that run a statement's constants up front.

`SET @v.Property = …`, `SET @v.Mutator(…)`, `UPDATE t SET col.Property = …` and `UPDATE t SET col.Mutator(…)` (`Parser/Expressions/ClrTypeMutation.cs`) deserialize the value, assign or call, and store the result; a method not marked `SqlMethod(IsMutator = true)` is **Msg 6201**, and a NULL receiver **Msg 5302** naming the member and the receiver as written, which ends the batch.

A CLR routine's parameter or return of the type binds to the class itself, a T-SQL routine's parameter, variable, column and `SELECT … INTO` column carry it, and `sp_executesql` declares it.
A client reads the serialized bytes as `byte[]`; over the TDS endpoint the column is a `UDTTYPE` naming the type's database, schema and assembly-qualified class, which a client loads to materialize the value (SqlClient raises `FileNotFoundException` where the assembly isn't on its path, as it does against real).

## Triggers

`CREATE TRIGGER … AS EXTERNAL NAME assembly.class.method` binds a DML trigger on a table or view (`AFTER` or `INSTEAD OF`) or a database-scope DDL trigger to a `void`, parameterless static method: a value-returning one is **Msg 6500**, one taking parameters **Msg 6531**, and `WITH ENCRYPTION` **Msg 10324**; `ALTER` of a T-SQL trigger into a CLR one is Msg 6530 and the reverse Msg 2010, as for the other modules.
The trigger fires where a T-SQL one would (`Simulation.ClrTrigger.cs`, reached from `RunOneTriggerBody`), under the same nesting, ordering and atomic-scope rules — see [`triggers.md`](triggers.md).

`SqlContext.TriggerContext` describes the fire: `TriggerAction` is the DML verb or the DDL event type's number (`sys.trigger_event_types.type`, which is what Framework's enum numbers), `ColumnCount` the parent's column count (0 for DDL), `IsUpdatedColumn(i)` true for every column of an `INSERT` or `DELETE` and for the `SET` clause's of an `UPDATE`, out of range throwing `IndexOutOfRangeException` from the context's own frame, and `EventData` the DDL event's document (`null` for DML).
`SqlContext.Pipe` sends as a procedure's does, save that a message reports **line 1**; a result set reaches the client among the firing statement's outcomes.
A procedure reads a null `TriggerContext` and a function the no-data-access refusal, as `Pipe`, save one marked to read data, which reads both as null.
A throw is Msg 6522 state 1 at line 1 naming the trigger, followed by Msg 3621 at line 1, and ends the firing statement and batch and its transaction as a T-SQL body's error does (a TRY block catches it with `XACT_STATE()` 0).

## The context connection

`new SqlConnection("context connection=true")` opens the calling session to the routine: each command's text is a batch of its own there (`Simulation.ClrContextConnection`), inside the caller's transaction and security context, one level deeper in `@@NESTLEVEL` than the routine, with no procedure frame (a value-form `RETURN` is Msg 178).
The keyword and its value are case-insensitive; any other keyword but `Type System Version` beside it is the provider's `InvalidOperationException`, and any other connection string is the code-access-security `SecurityException` in a `SAFE` assembly.
Who may open it:

- A procedure or trigger always; a function or a table-valued function's init method only when its `SqlFunction` attribute sets `DataAccess` or `SystemDataAccess` to `Read` — otherwise `Open` throws the no-data-access refusal — and `FillRow`, an aggregate and a type's members never.
- A function marked so reads `SqlContext.Pipe` and `TriggerContext` as null; one marked `SystemDataAccessKind.Read` alone reads the catalog but no user table, view or function (**Msg 589** state 3), and a function's command that writes — DML to a table, `SELECT … INTO`, `CREATE TABLE`, the transaction statements, `PRINT` — is **Msg 443** at state 2.
- One per routine at a time ("The context connection is already in use."), one reader at a time on it, and a command on a connection holding a `SqlTransaction` must name it; `PacketSize` and `WorkstationId` refuse, and an open connection reports the session's database, an empty data source and the server version.

A command's outcomes follow the provider:

- `ExecuteNonQuery` runs the whole batch — past a statement's error, as a client batch does — then throws what it met as one `SqlException` (first error's number, class, state and procedure, line 0), and answers the rows its statements changed, -1 when none did.
- `ExecuteScalar` answers the first result set's first value, `DBNull` for a NULL and null for no row, and raises only the errors before that result set — and the one that cut it short before its first row.
- `ExecuteReader` walks the result sets; the error cutting one short surfaces from the `Read` that runs out of rows, one between result sets from `NextResult`, and the batch's later result sets stay readable after either.
  Each value's provider-specific form is its `SqlTypes` struct — a character string carrying its collation's locale — or its plain form for `date`, `time`, `datetime2` and `datetimeoffset`, and a `sql_variant` its base value's; a typed getter reads only its own column type (`InvalidCastException` otherwise), a NULL throws `SqlNullValueException`, and a read off a row "Invalid attempt to read when no data is present.".
- What a command prints reaches only the connection's `InfoMessage` handlers, as one event per command; a CLR procedure a command calls sends its pipe output there too.
- Parameters bind by name with or without their `@`; an untyped one takes its value's type (a string as `nvarchar` of its own length, a `decimal` at its own precision and scale), a sized one cuts a longer string or binary value, and output, input-output and return-value parameters come back in both forms.
  `CommandType.StoredProcedure` runs `EXEC` of the named procedure.

`SqlPipe.ExecuteAndSend` runs a command with everything it produces — result sets, row counts, messages, and its error ahead of the routine's own Msg 6522 — going to the client, and `SqlPipe.Send(SqlDataReader)` sends what a reader has left: the rows after the one it is on, then every later result set.

A trigger's commands carry its frame, so they read `INSERTED`, `DELETED`, `EVENTDATA()` and `COLUMNS_UPDATED()` — which dynamic SQL inside them does not (Msg 208).
The commands of one call share its `#temp` scope, database (`ChangeDatabase` is a `USE`) and `SET` options, all reverting when the routine returns.

The transaction held when the routine was entered — an explicit one, or a trigger's firing statement — may not be ended inside it: a `ROLLBACK` is **Msg 3994** state 2 and a `COMMIT` that would end it **Msg 3990**, and either, or an error that would roll it back (`XACT_ABORT`, which a trigger runs under, or a batch-aborting conversion error), leaves it ended with `@@TRANCOUNT` unchanged.
When the routine returns, an ended transaction is **Msg 3991** and a changed `@@TRANCOUNT` **Msg 3992**; when it throws, either is **Msg 6549** — 6522's report under its own wording, closed by "User transaction, if any, will be rolled back." — and each rolls the transaction back and ends the batch, or under a `TRY` leaves it uncommittable for the batch's end to report (Msg 3998).
`SqlConnection.BeginTransaction` is `BEGIN TRANSACTION` in the session, nesting inside the caller's; a transaction the routine began with none held on entry rolls back quietly when it returns.

## Catalog surface

- **`sys.assemblies`** — one row per registered assembly plus the `Microsoft.SqlServer.Types` system row real always carries (assembly_id 1, principal_id 4, `UNSAFE_ACCESS`, `is_user_defined` 0).
  That system row is what `sys.assembly_types` joins against; before CLR shipped, this view was empty and that join yielded nothing.
- **`sys.assembly_files`** — the verbatim bytes plus SHA-256 / SHA-512 digests. Real also carries a row for the system assembly; the simulator has no bytes to project for it, so only user assemblies appear.
- **`sys.assembly_modules`** — one row per bound routine or trigger (`assembly_class` / `assembly_method`), an aggregate's method NULL. `null_on_null_input` is constant 0; `execute_as_principal_id` NULL.
- **`sys.objects`** / **`sys.triggers`** — `FS` / `CLR_SCALAR_FUNCTION`, `FT` / `CLR_TABLE_VALUED_FUNCTION`, `PC` / `CLR_STORED_PROCEDURE`, `AF` / `AGGREGATE_FUNCTION` and `TA` / `CLR_TRIGGER`, none with a `sys.sql_modules` row or an `OBJECT_DEFINITION`, and `sp_helptext` Msg 15197 (probe-confirmed).
- **`sys.types`** / **`sys.assembly_types`** / **`sys.type_assembly_usages`** — a CLR type is `system_type_id` 240 with `is_assembly_type` 1 and its `MaxByteSize` as `max_length`, listed after the three system CLR types with its `assembly_class`, `is_binary_ordered`, `is_fixed_length` and `assembly_qualified_name`.
  `sys.columns`, `sys.parameters`, `INFORMATION_SCHEMA.COLUMNS` (`data_type` and `domain_name` the type's name, `character_maximum_length` its size), `TYPEPROPERTY('…', 'Precision')`, `sp_help` (a NULL `Storage_type`) and `sp_columns` describe it the same way.
- **`sys.parameters`** — a CLR module's parameters report their declared defaults (`has_default_value` 1, the constant in `default_value`) where a T-SQL module's never do; a scalar function and an aggregate carry the nameless output row 0 for their return type.
- **`sys.columns` / `INFORMATION_SCHEMA.ROUTINE_COLUMNS`** — a table-valued function's declared columns. **`INFORMATION_SCHEMA.ROUTINES`** — `ROUTINE_BODY` `EXTERNAL`, an aggregate's `DATA_TYPE` its return type.
- **`sp_help`** — `assembly stored procedure` (with real's leading space), `assembly table function` with its column set, `aggregate function` with its return row.
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
- **`PERMISSION_SET` is recorded, not enforced at run time.** It selects which static checks run at registration; it cannot confine a loaded assembly (see above).
- **A reported stack holds only the frames the simulator can see as the author's.**
  Real also shows its own internal frames — `SqlMetaData.Construct`, `System.Data.SqlServer.Internal.ClrLevelContext` — which have no counterpart, and the shim's public frames are named after its own members, which match Framework's only where the member is the one that throws.
- **A CLR routine's own exceptions match real's; ones .NET's base library raises carry .NET's wording and frames.**
  A `FormatException` from `int.Parse` reads `The input string 'x' was not in a correct format.` where Framework's reads `Input string was not in a correct format.`, and a stack real reports through `System.Number` shows only the author's frames here; real's own marshalling frames (`SqlBytes.Write`, `XmlSerializer` internals) never show.
  Code in a registered class also runs under the host's culture, so a `DateTime.ToString()` there can render differently (ICU's narrow no-break space before `AM`).
- **An aggregate's state never leaves memory.**
  One instance accumulates each whole group, so `Merge` is never called and a `Format.UserDefined` aggregate's `Read` / `Write` never run; real may serialize state between rows, which an aggregate that loses a field in `Write` would show.
- **A context-connection error's report shows the provider's public frames only.**
  Real's names `SqlConnection.OnError`, `SqlInternalConnectionSmi` and the rest of the in-process plumbing, which has no counterpart here.
- **A function's context-connection command that writes is refused whole before any of it runs**, where real refuses the statement when it runs — which shows only through `ExecuteScalar`, which reads no further than its first result set.
- **An untyped `decimal` parameter reports base type `decimal`** through `SQL_VARIANT_PROPERTY`, where real's reports `numeric`.
- **A command's result sets are read to the end when it runs**, so a routine that writes between two `Read` calls can't change what the reader returns, as it could on real.
- **`sp_describe_first_result_set`'s Msg 11515 comes from the metadata-only mode**, so a plain `SET FMTONLY ON` followed by `EXEC` of a CLR procedure raises it too; that shape is unprobed.
- Auto-generated `assembly_id` values start at 65536 and increment, which is what a fresh database showed (probed 2026-09-28); a server that has seen other assemblies hands out later ids.
- The `Microsoft.SqlServer.Types` system row reports a fixed SQL Server 2025 RTM `create_date` / `modify_date` rather than a resource-database build stamp.

## Not modeled yet

- **Context-connection shapes**: a function's `SELECT` over a side-effecting built-in (real's Msg 443 names it `SELECT WITHOUT QUERY`) and its `EXEC sp_executesql` (Msg 557) run; `GetSchemaTable`, `ExecuteXmlReader`, the async methods and `FireInfoMessageEventOnUserErrors`; a `SqlTransaction` the server's own rollback ended; a type's members and an aggregate marked to read data; and the `hierarchyid` and spatial columns as their `Microsoft.SqlServer.Types` instances (a reader serves their bytes).
- **Connections other than the context connection** from an `EXTERNAL_ACCESS` or `UNSAFE` assembly throw `NotSupportedException` inside Msg 6522 rather than connecting.
- CLR type members beyond the modeled shapes: a member through a three-part column name, an `UPDATE … FROM` alias target's mutator, `SqlMethod(OnNullCall = false)` / `InvokeIfReceiverIsNull`, `SqlFacet` on a member, `ValidationMethodName`, and the `IsDeterministic` / `IsPrecise` flags a persisted computed column or index would read.
- A CLR type as `sql_variant`'s base, in a partition function or an index's `INCLUDE`, an `ALTER ASSEMBLY` that re-shapes a registered class, and a client-side materialized instance (`GetValue` returns the bytes).
- `OBJECT_ID(name, 'TA')` and the other CLR object-type codes as the filter argument.
- `SqlMetaData`'s constructors taking a `SortOrder` or a UDT type.
- The `SqlUserDefinedAggregate` flags `IsNullIfEmpty` / `IsInvariantTo*` and `MaxByteSize` — read by real's optimizer and serializer, ignored here.
- `INSERT` into a CLR table-valued function reports Msg 208 where real reports its "derived table is not updatable" error, a gap shared with the T-SQL kinds.
- Plain-CLR parameter and return forms real also accepts (`string`, `int?`, `SqlChars`, `SqlBytes`) — only the `System.Data.SqlTypes` family binds, save a procedure's `int` / `int?` status.
- `ALTER ASSEMBLY`, `CREATE ASSEMBLY … FROM '<path>'`, assembly `AUTHORIZATION`, `sp_add_trusted_assembly`, and assembly signing / `clr strict security` enforcement.
- BACPAC round-trip of `SqlAssembly` model elements.
- Out-of-process execution.
  Measured cost of a cross-process round trip is ~56 µs versus ~0.12 µs for an in-process cached delegate — roughly 470× — and a child process is not a sandbox without per-OS restriction work (seccomp/namespaces, restricted tokens, `sandbox_init`), so it is only worth building together with that.
