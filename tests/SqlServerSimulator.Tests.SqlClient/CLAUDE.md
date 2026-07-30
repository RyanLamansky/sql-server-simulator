# Claude Working Notes — `SqlServerSimulator.Tests.SqlClient`

Auto-loaded when working in this directory.
Wire-test conventions; assumes the root [`CLAUDE.md`](../../CLAUDE.md) is also in context, plus [`src/SqlServerSimulator/Network/CLAUDE.md`](../../src/SqlServerSimulator/Network/CLAUDE.md) and [`docs/claude/tds-endpoint.md`](../../docs/claude/tds-endpoint.md) when the change is protocol-shaped.

The sibling [`SqlServerSimulator.Tests`](../SqlServerSimulator.Tests/CLAUDE.md) conventions do **not** carry over: its terse `new Simulation().ExecuteScalar("…")` folding has neither a listener nor a client, so nothing here collapses to a single expression.

## Default test shape

A simulation, a listener, and a connection per test, with the listener on an ephemeral port so the parallel run can't collide:

```csharp
[TestMethod]
public async Task ParameterizedSelect_RoundTrips()
{
    var simulation = new Simulation();
    await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
    await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

    await using var command = new SqlCommand("select @a + @b", connection);
    _ = command.Parameters.AddWithValue("@a", 40);
    _ = command.Parameters.AddWithValue("@b", 2);
    AreEqual(42, await command.ExecuteScalarAsync(TestContext.CancellationToken));
}
```

`public TestContext TestContext { get; set; } = null!;` on the class, and `TestContext.CancellationToken` threaded through every async call (MSTEST0049).

**Seed in-process, assert over the wire.**
`Wire.ExecInProc(simulation, sql)` runs setup through the in-process ADO surface against the same simulation — most files here do it, because the wire path is the subject and the fixture only has to exist.

**For an expected value whose exact bytes are nontrivial, dual-read instead of hand-writing it**: run the same query in-process and over the wire against one simulation and assert they match, so the assertion can't encode a wrong constant.
`Wire.ReadAllInProc` / `Wire.AssertScalarParamRoundTrips` / `Wire.OutputInProc` exist for that.

## `Wire` helpers

[`Wire.cs`](Wire.cs) holds the shared plumbing. Two carry the suite:

- **`OpenAsync(listener, cancellationToken, extra = "")`** — an opened `SqlConnection`, where `extra` appends connection-string keywords.
- **`ExecInProc(simulation, sql)`** — fixture setup off the wire.

The rest are narrower: `ExecInProcParam` (seed one bound parameter, for large or binary payloads), `ReadAllInProc` / `Drain` (materialize rows), `AssertValueEqual` (byte-array-aware equality), `AssertScalarParamRoundTrips` (a `select @p` dual-read), `OutputInProc` (output-parameter writeback oracle), `ConnectionString` / `PooledConnectionString` (when a raw `SqlConnection` is needed, the latter capping the pool at one physical connection so a close/reopen exercises the reset bit).

Reach for an existing helper before hand-rolling — and when a new shape recurs across files, add it here rather than in each.

## The endpoint's certificate needs nothing from tests

`ListenLocalAsync(0, …)` is the right call even though every test makes its own listener: the endpoint's default certificate is created once per process and shared by all of them, so a per-test listener costs no key generation.
Nothing here should cache, supply, or persist a certificate to make that true — a test-side cache would only shadow the library's, and `AssemblyHooks` already warms it so the first touch doesn't land in the middle of the parallel run.

Two cases legitimately depart:

- **`Encrypt=Strict` pins.** SqlClient ignores `TrustServerCertificate` in strict mode and always validates, so [`StrictEncryptionTests`](StrictEncryptionTests.cs) exports the shared certificate's public part to a fixed-name file in the OS temp directory and names it in the connection string. One file serves every listener, because the certificate is process-wide.
- **The ownership contract supplies its own.** A test asserting that a supplied certificate survives listener disposal has to generate one, since the shared default is never disposed and would make the assertion vacuous.

## Cost discipline

Under method-level parallelism this assembly cannot finish faster than its slowest single test, so one long wait sets the floor for all of it — and a client-side default is the easiest way to introduce one by accident.

**A test expecting Msg 4060 at login sets `ConnectRetryInterval=1`**: SqlClient counts 4060 transient and retries the login once by default, sleeping the interval first, and the 10-second default becomes the whole assembly's wall clock.
The 1-second minimum is preferred over `ConnectRetryCount=0` because it keeps the retry exercised — the listener has to serve a second login attempt after rejecting the first — and the surfaced error collection is identical either way.

The standing long poles are the cancellation and command-timeout tests, which wait on real client timers; a new test that stays under them costs nothing measurable.
