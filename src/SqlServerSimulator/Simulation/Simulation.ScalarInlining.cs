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

    /// <summary>
    /// Bumped as <see cref="ClearPlanCache"/> empties the cache, which every
    /// <see cref="ModulePlan"/> compiled before then outlives.
    /// </summary>
    private long modulePlanGeneration;

    /// <summary>
    /// Compiles a procedure's or DML trigger's body as its call is about to run
    /// it, when it has no standing <paramref name="plan"/>, answering the
    /// inlining failures the compile sends ahead of the body's first statement
    /// (see <see cref="ModulePlan"/>). The statements the compile leaves to
    /// compile as they run go on <paramref name="body"/>, from the plan when it
    /// stands. A body that doesn't compile — a statement naming a table created
    /// after the module that no longer binds — sends nothing, keeps no plan, so
    /// the next call compiles it again, and hands back
    /// <paramref name="compileError"/>: every binder error the body holds, which
    /// real sends before the body's first statement runs (probed 2026-10-01
    /// against SQL Server 2025).
    /// </summary>
    /// <param name="body">The batch the body is about to run on.</param>
    /// <param name="database">The database the body binds in.</param>
    /// <param name="plan">The module's standing plan, which a compile replaces when <paramref name="keepsPlan"/>.</param>
    /// <param name="parent">A trigger's table, whose change the plan depends on too; null for a procedure.</param>
    /// <param name="recompile">Whether the call compiles the body whatever plan stands — <c>EXEC … WITH RECOMPILE</c> or a procedure created <c>WITH RECOMPILE</c>.</param>
    /// <param name="keepsPlan">Whether the compile becomes the module's plan, which <c>EXEC … WITH RECOMPILE</c>'s doesn't.</param>
    /// <param name="compileError">The errors that stop the body before it runs, or null when it compiled.</param>
    private List<SimulatedSqlException>? CompileModuleBody(BatchContext body, Database database, ref ModulePlan? plan, SchemaObject? parent, bool recompile, bool keepsPlan, out SimulatedSqlException? compileError)
    {
        var generation = Volatile.Read(ref this.modulePlanGeneration);
        if (!recompile && plan is { } standing && standing.Stands(Volatile.Read(ref this.SchemaVersion), generation))
        {
            body.StatementsCompiledOnRun = standing.CompiledOnRun;
            compileError = null;
            return null;
        }

        var schemaVersion = Volatile.Read(ref this.SchemaVersion);
        var compileContext = CompileContextFor(body, body.Parser.Command);
        compileError = this.CompileBatch(compileContext, key: null, out var failures, sendsOnce: false);
        if (compileError is not null)
        {
            body.StatementsCompiledOnRun = null;
            return null;
        }
        var compiledOnRun = compileContext.StatementsCompiledOnRun;
        body.StatementsCompiledOnRun = compiledOnRun;
        if (keepsPlan)
        {
            List<SchemaObject> dependencies = parent is null ? [] : [parent];
            AddBodyDependencies(database, body.Parser.Command.CommandText, dependencies, compileContext.InlinedCalls?.Calls);
            plan = new ModulePlan(schemaVersion, generation, ModulePlan.Track(database, dependencies), compiledOnRun);
        }
        return failures;
    }

    /// <summary>
    /// Adds what <paramref name="bodyText"/> names to <paramref name="dependencies"/>,
    /// and, through every function or view among them, what those bodies name in
    /// turn: a scalar function inlines with its body, so the plan depends on the
    /// objects that body reads (probed 2026-10-01 against SQL Server 2025:
    /// dropping a table only a called function reads recompiles the procedure).
    /// </summary>
    private static void AddBodyDependencies(Database database, string bodyText, List<SchemaObject> dependencies, List<InlinedScalarCall>? inlined)
    {
        var start = dependencies.Count;
        foreach (var named in ModuleDependencies.ObjectsNamedBy(database, bodyText))
        {
            if (!dependencies.Contains(named))
                dependencies.Add(named);
        }
        foreach (var call in inlined ?? [])
        {
            if (!dependencies.Contains(call.Function))
                dependencies.Add(call.Function);
        }
        for (var i = start; i < dependencies.Count; i++)
        {
            var text = dependencies[i] switch
            {
                UserDefinedFunction function => function.BodyText,
                View view => view.BodyText,
                _ => null,
            };
            if (text is null)
                continue;
            foreach (var named in ModuleDependencies.ObjectsNamedBy(database, text))
            {
                if (!dependencies.Contains(named))
                    dependencies.Add(named);
            }
        }
    }

    /// <summary>The line real reports an inlining failure on when the missing object's name is qualified.</summary>
    private const int QualifiedInliningFailureLine = 12;

    /// <summary>
    /// What an attempt to inline <paramref name="function"/> — or to expand an
    /// inline table-valued one — meets: the first
    /// object its body names that doesn't exist, attributed to the function at
    /// the reference's line in the text that created it (a qualified name's on
    /// line 12); else the failures of
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
                // A qualified name reports line 12 wherever it sits — real's own
                // constant, the line its module-definition binding reports a
                // missing qualified name at — and a bare one its own line
                // (probed 2026-09-30 and again 2026-10-04 against SQL Server
                // 2025, two-, three- and four-part names, bracketed or not).
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

/// <summary>
/// The plan a procedure's or DML trigger's body compiled to. Real compiles such
/// a body when a call first runs it, sending then the non-aborting Msg 208 of
/// each scalar function call it couldn't inline — ahead of the body's first
/// statement, past any <c>TRY</c> in it — and later calls reuse the plan and
/// send nothing, a nested call's included (probed 2026-10-01 against SQL Server
/// 2025). The plan stands until the cache is cleared
/// (<c>DBCC FREEPROCCACHE</c>), <c>sp_recompile</c> names the module or a table
/// it reads, or an object it depends on changes: a table it reads is altered,
/// dropped or recreated, a function it calls is altered, or one of those
/// functions' own objects is — but not an unrelated <c>CREATE</c> or
/// <c>DROP</c>, nor <c>sp_recompile</c> of a function it calls. A
/// statement the compile deferred compiles on the plan's first run of it, and
/// one carrying <c>OPTION (RECOMPILE)</c> on every run (see
/// <see cref="StatementsCompiledOnRun"/>).
/// </summary>
internal sealed class ModulePlan(long schemaVersion, long generation, ModulePlanDependency[] dependencies, StatementsCompiledOnRun? compiledOnRun)
{
    /// <summary>The schema version the plan was last found standing under, which spares a call under it the dependency check.</summary>
    private long schemaVersion = schemaVersion;
    private readonly long generation = generation;
    private readonly ModulePlanDependency[] dependencies = dependencies;

    /// <summary>The statements the body compiles as they run, shared by every call the plan serves.</summary>
    public readonly StatementsCompiledOnRun? CompiledOnRun = compiledOnRun;

    /// <summary>
    /// Whether the plan still stands under <paramref name="currentSchemaVersion"/>
    /// and the cache's <paramref name="currentGeneration"/>: no schema change at
    /// all, or none to an object it depends on.
    /// </summary>
    public bool Stands(long currentSchemaVersion, long currentGeneration)
    {
        if (currentGeneration != this.generation)
            return false;
        if (currentSchemaVersion == Volatile.Read(ref this.schemaVersion))
            return true;
        foreach (var dependency in this.dependencies)
        {
            if (!dependency.Unchanged())
                return false;
        }
        Volatile.Write(ref this.schemaVersion, currentSchemaVersion);
        return true;
    }

    /// <summary>Snapshots each of <paramref name="objects"/> as the plan depends on it now.</summary>
    public static ModulePlanDependency[] Track(Database database, List<SchemaObject> objects)
    {
        var tracked = new List<ModulePlanDependency>(objects.Count);
        foreach (var dependency in objects)
        {
            foreach (var (_, schema) in database.Schemas)
            {
                if (schema.SchemaId == dependency.SchemaId)
                {
                    tracked.Add(new ModulePlanDependency(schema, dependency));
                    break;
                }
            }
        }
        return [.. tracked];
    }
}

/// <summary>
/// One object a <see cref="ModulePlan"/> depends on, as it stood when the plan
/// compiled: the object its schema held under the name, its modification time,
/// and for a table its column set, which <c>ALTER TABLE</c> replaces.
/// </summary>
internal readonly struct ModulePlanDependency(Schema schema, SchemaObject dependency)
{
    private readonly Schema schema = schema;
    private readonly SchemaObject dependency = dependency;
    private readonly DateTime modifyDate = dependency.ModifyDate;
    private readonly Storage.HeapColumn[]? columns = (dependency as Storage.HeapTable)?.Columns;

    /// <summary>Whether the schema still holds the same object under its name, unmodified.</summary>
    public bool Unchanged() =>
        this.schema.TryFindInSharedNamespace(this.dependency.Name, out var current)
        && ReferenceEquals(current, this.dependency)
        && current.ModifyDate == this.modifyDate
        && ReferenceEquals((current as Storage.HeapTable)?.Columns, this.columns);
}
