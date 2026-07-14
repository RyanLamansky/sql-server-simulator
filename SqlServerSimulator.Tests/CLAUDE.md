# Claude Working Notes — `SqlServerSimulator.Tests`

Auto-loaded when working in this directory. Test-shape conventions; assumes the root [`CLAUDE.md`](../CLAUDE.md) (architecture / probe workflow) is also in context.

## Default test shape

**Fold setup + mutation + assertion into one call.** A pure-success test with a single assertion is one expression:

```csharp
[TestMethod]
public void TableLevel_Composite_Match_Succeeds()
    => AreEqual(1, new Simulation().ExecuteScalar("""
        create table p (a int not null, b int not null, primary key (a, b));
        create table c (id int not null primary key, ra int not null, rb int not null,
                        foreign key (ra, rb) references p(a, b));
        insert p values (1, 2);
        insert c values (10, 1, 2);
        select count(*) from c
        """));
```

`Simulation.ExecuteScalar` / `ExecuteNonQuery` accept a multi-statement raw string; the final `SELECT`'s first value is the scalar return. Don't split CREATE + INSERT + SELECT into three calls unless a later assertion needs an intermediate observation.

**Batching trap — `ExecuteNonQuery` returns the *sum* of rows-affected across all DML in the batch** (faithful to TDS DONE tokens; DDL like CREATE TABLE doesn't contribute). So `AreEqual(1, sim.ExecuteNonQuery("…; insert 2 rows; delete 1 row"))` measures 3, not the DELETE's 1, and fails for the wrong reason. Safe to fold setup into the asserted call only for `ExecuteScalar` (DML counts don't leak in) and `AssertSqlError` (throws). When the assertion *is* the `ExecuteNonQuery` row count, keep setup in a separate call so the measured statement is isolated.

Canonical example: [`CheckConstraintTests.cs`](CheckConstraintTests.cs). The full set of helpers ships in [`Extensions.cs`](Extensions.cs).

## Multi-assertion shape

When the test asserts on intermediate state, one `ExecuteNonQuery` for setup + per-assertion `ExecuteScalar` against a shared `Simulation`:

```csharp
[TestMethod]
public void OnDeleteSetNull_NullsChildFkColumn()
{
    var sim = new Simulation();
    _ = sim.ExecuteNonQuery("""
        create table p (id int not null primary key);
        create table c (id int not null primary key, p_id int null references p(id) on delete set null);
        insert p values (10), (20);
        insert c values (1, 10), (2, 10), (3, 20);
        delete p where id = 10
        """);
    AreEqual(2, sim.ExecuteScalar("select count(*) from c where p_id is null"));
    AreEqual(1, sim.ExecuteScalar("select count(*) from c where p_id = 20"));
}
```

For tests with explicit `BEGIN TRAN` / `ROLLBACK` across multiple assertion points, open one `DbConnection` (`sim.CreateOpenConnection()`) and reuse it — a fresh connection drops the active transaction.

## Failure paths: use `AssertSqlError`

Don't hand-roll `Throws<DbException>` + `ex.Data["HelpLink.EvtID"]` comparison. The extension is in `Extensions.cs`:

```csharp
var ex = new Simulation().AssertSqlError("""
    create table p (id int not null primary key);
    create table c (id int not null primary key, p_id int not null references p(id));
    insert c values (1, 99)
    """, 547);
Assert.Contains("FOREIGN KEY constraint", ex.Message);
```

The overload `AssertSqlError(commandText, errorNumber, expectedMessage)` asserts the full message verbatim when the wording is the entire point of the test.

## Assertion idioms

- **`using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;`** at the top of new test files. The unqualified `AreEqual` / `IsTrue` / `IsFalse` / `Throws` / `Contains` / `DoesNotContain` / `StartsWith` is the dominant style (~85% of existing tests). `Assert.X` still works inline when there's a name collision (e.g. `Assert.Contains` next to a `Contains` local).
- **`Assert.Contains(needle, haystack)`** for substring checks — not `StringAssert.Contains(haystack, needle)`. The argument order is opposite to `StringAssert` and the analyzer (**MSTEST0046**) fails the build on the older form.
- **`Assert.DoesNotContain(needle, haystack)`** for negative substring assertions — not `Assert.IsFalse(haystack.Contains(needle))`. **MSTEST0037** fails the build.
- **`IsTrue(bool)` / `IsFalse(bool)`** for boolean asserts — not `AreEqual(true, x)` / `AreEqual(false, x)`. **MSTEST0037** again.
- **Async tests**: capture `TestContext.CancellationToken` and pass it explicitly through async APIs (**MSTEST0049**). The `public TestContext TestContext { get; set; } = null!;` member is required on the class.

## Discards (IDE0058)

Project-wide rule: every non-void call whose return value isn't used must be prefixed with `_ =`. The analyzer fails the build otherwise.

```csharp
_ = sim.ExecuteNonQuery("insert t values (1)");           // returns row count
_ = conn.CreateCommand("begin tran").ExecuteNonQuery();   // returns row count
_ = context.SaveChanges();                                // returns int (EF Core tests)
_ = context.Items.Add(new Item { ... });                  // returns EntityEntry
```

`ExecuteScalar` returning `object?` only needs `_ =` when discarded — wrap with `AreEqual(...)` when asserting.

## Collation / Unicode fixtures

- **Literal `N'…'`** for any character with a distinctive visible glyph (`€`, `ƒ`, `Ÿ`, `ア`, `café`) — the literal *is* the explanation, no `U+xxxx` comment needed.
- **`nchar(N)` (decimal, never `0xNN`)** only for invisible / ambiguous characters: NBSP (U+00A0, renders like a space), Private Use Area (no glyph), surrogate pairs (`nchar(55357) + nchar(56832)` for 😀). Prefer decimal for readability, but hex `nchar(0x…)` now resolves too (`nchar(0x41)` → `'A'`): the parser reads `0xNN` as varbinary and the varbinary→int coercion converts it big-endian to the code point.
- **Skip the `cast(<literal> as varchar(N))` / `char(N)` on INSERT** — assignment-time coercion handles `N'…'` → `varchar(N)` and `nchar(N)` → `char(N)` automatically; the cast is noise.

## Probe code is not a style reference

Probe scaffolds in `/tmp/<feature>/` are throwaway data-collection scripts: bare `using` blocks, `Console.WriteLine`, no analyzer enforcement. When the graduated tests land in this directory, **don't mirror the probe's shape** — go directly to the dense form above. Probes get deleted after the feature bundle commits.

## Multi-batch statements

A few CREATEs must be the first statement in a batch (Msg 111 — `CREATE/ALTER PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA`). They can't share an `ExecuteNonQuery` string with prior CREATE TABLEs. Use the helper:

```csharp
sim.ExecuteBatches(
    "create table t (id int)",
    "create procedure p1 as select * from t",  // must-be-first
    "create procedure p2 as select count(*) from t");
```

One ADO.NET command per element, shared open connection — equivalent to splitting into three `ExecuteNonQuery` calls but in one statement.

## AssemblyHooks

Every test project has [`AssemblyHooks.cs`](AssemblyHooks.cs) with a `[TestClass] [AssemblyInitialize]` to warm shared initialization once before the parallel test run. Don't remove it — without the warm-up the first parallel batch races on shared static init (analyzer Roslyn cache is the most extreme case at ~3× slowdown, but the pattern generalizes).
