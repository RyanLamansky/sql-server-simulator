using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The batches whose compile has sent its scalar inlining failures, by
    /// database and text, each with the <see cref="SchemaVersion"/> it compiled
    /// under: real sends them only as a plan compiles, and a batch that
    /// compiled reuses its plan, so running it again sends none (probed
    /// 2026-09-30 against SQL Server 2025, dynamic SQL included).
    /// <see cref="ClearPlanCache"/> forgets them as it forgets the plans.
    /// </summary>
    private readonly ConcurrentDictionary<(string Database, string Text), long> sentInliningFailures = new();

    /// <summary>How many keys <see cref="sentInliningFailures"/> holds, counted as <see cref="planCacheCount"/> is.</summary>
    private int sentInliningFailureCount;

    /// <summary>
    /// The non-aborting errors a compile owes for the scalar function calls it
    /// inlined, each with how many of the compile's binder errors precede it:
    /// every failure of every call <paramref name="calls"/> kept, in binding
    /// order, a call to a function real can't inline sending one apiece.
    /// </summary>
    private List<(int Position, SimulatedSqlException Failure)>? InliningFailuresOf(BatchContext batch, InlinedScalarCalls calls)
    {
        List<(int, SimulatedSqlException)>? failures = null;
        foreach (var call in calls.Calls)
        {
            if (call.Dropped)
                continue;
            foreach (var failure in this.ScalarInliningFailures(batch, call.Function))
                (failures ??= []).Add((call.ErrorPosition, failure.Raise()));
        }
        return failures;
    }

    /// <summary>
    /// The outcomes a compile that inlined nothing for
    /// <paramref name="failures"/> sends ahead of what its batch runs: each a
    /// non-aborting error, the last of which <c>@@ERROR</c> reads until the
    /// first statement ends.
    /// </summary>
    private static IEnumerable<SimulatedErrorOutcome> CompileFailuresSent(BatchContext batch, List<SimulatedSqlException>? failures)
    {
        if (failures is null)
            yield break;
        foreach (var failure in failures)
        {
            batch.Connection.LastErrorNumber = failure.Number;
            yield return new SimulatedErrorOutcome(failure, raisedWhileCompiling: true);
        }
    }

    /// <summary>
    /// Whether the compile of <paramref name="text"/> in
    /// <paramref name="database"/> sends its inlining failures: true the first
    /// time under the current <see cref="SchemaVersion"/>, false once sent.
    /// </summary>
    private bool SendsInliningFailures(Database database, string text)
    {
        var version = Volatile.Read(ref this.SchemaVersion);
        var key = (database.Name, text);
        if (this.sentInliningFailures.TryGetValue(key, out var sentUnder))
        {
            if (sentUnder == version)
                return false;
            this.sentInliningFailures[key] = version;
            return true;
        }
        if (Volatile.Read(ref this.sentInliningFailureCount) < PlanCacheCapacity && this.sentInliningFailures.TryAdd(key, version))
            _ = Interlocked.Increment(ref this.sentInliningFailureCount);
        return true;
    }

    /// <summary>Forgets the inlining failures sent for <paramref name="database"/>'s batches, or every database's.</summary>
    private void ForgetSentInliningFailures(Database? database)
    {
        foreach (var (key, _) in this.sentInliningFailures)
        {
            if ((database is null || BuiltInToken.Equals(key.Database, database.Name)) && this.sentInliningFailures.TryRemove(key, out _))
                _ = Interlocked.Decrement(ref this.sentInliningFailureCount);
        }
    }

    /// <summary>The line real reports an inlining failure on when the missing object's name is qualified.</summary>
    private const int QualifiedInliningFailureLine = 13;

    /// <summary>
    /// What an attempt to inline <paramref name="function"/> — or to expand an
    /// inline table-valued one — meets: the first
    /// object its body names that doesn't exist, attributed to the function at
    /// the reference's line in the text that created it (a qualified name's on
    /// line 13); else the failures of
    /// the scalar functions the body calls in turn, which real inlines with it
    /// (probed 2026-09-30 against SQL Server 2025). Settled once per
    /// <see cref="SchemaVersion"/> by reading the body as it binds at
    /// <c>CREATE</c>, without running it.
    /// </summary>
    /// <remarks>
    /// A body whose column no longer exists fails on real too, sending its
    /// whole binder report twice; that isn't modeled, and such a body inlines
    /// here. A call inside the function's own analysis — a cycle through
    /// another function — reads as inlining.
    /// </remarks>
    internal InliningFailure[] ScalarInliningFailures(BatchContext batch, UserDefinedFunction function)
    {
        var version = Volatile.Read(ref this.SchemaVersion);
        if (Volatile.Read(ref function.InliningFailuresSchemaVersion) == version)
            return function.InliningFailures;
        if (Monitor.IsEntered(function))
            return [];
        lock (function)
        {
            if (function.InliningFailuresSchemaVersion == version)
                return function.InliningFailures;
            var connection = batch.Connection;
            using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // BodyText is the function's own stored body
            bodyCommand.CommandText = function.BodyText;
#pragma warning restore CA2100
            var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
            foreach (var param in function.Parameters)
                variables[param.Name] = new VariableSlot(param.Type, param.DeclaredMaxLength, Storage.SqlValue.Null(param.Type), parameter: null) { SpelledNumeric = param.SpelledNumeric };
            var attempt = new InlinedScalarCalls(errors: null, body: true);
            var analysis = new BatchContext(bodyCommand, variables, new UdfFrame(function is ScalarFunction scalar ? scalar.ReturnType : Storage.SqlType.Int32)) { SuppressDiagnosticsResolution = true, CalledFunctionBody = true, InlinedCalls = attempt };
            var moduleScope = ModuleDatabaseScope.Enter(connection, function.Schema.Database);
            var savedQuotedIdentifiers = connection.QuotedIdentifiers;
            var savedAnsiNulls = connection.AnsiNulls;
            connection.QuotedIdentifiers = function.UsesQuotedIdentifier;
            connection.AnsiNulls = function.UsesAnsiNulls;
            try
            {
                _ = this.BindWithoutRunning(analysis, []);
            }
            catch (SimulatedSqlException)
            {
                // A body that no longer parses or binds raises when it runs.
            }
            finally
            {
                connection.QuotedIdentifiers = savedQuotedIdentifiers;
                connection.AnsiNulls = savedAnsiNulls;
                moduleScope.Exit();
            }

            List<InliningFailure> failures = [];
            // An inline table-valued function's own missing object is its
            // binding error, which the referencing statement reports itself.
            if (function is ScalarFunction && attempt.MissingObject is var (name, line))
            {
                // A qualified name reports line 13 wherever it sits — real's own
                // constant, as Msg 1065's 15 is — and a bare one its own line
                // (probed 2026-09-30 against SQL Server 2025, two-, three- and
                // four-part names, bracketed or not).
                failures.Add(new InliningFailure(name, name.Count > 1 ? QualifiedInliningFailureLine : line + function.BodyLineOffset, function.Name));
            }
            else
            {
                foreach (var call in attempt.Calls)
                {
                    if (!call.Dropped)
                        failures.AddRange(this.ScalarInliningFailures(batch, call.Function));
                }
            }
            function.InliningFailures = [.. failures];
            Volatile.Write(ref function.InliningFailuresSchemaVersion, version);
            return function.InliningFailures;
        }
    }
}
