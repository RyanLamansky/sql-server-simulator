using System.Collections.Concurrent;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Command texts that compiled without error, each with the
    /// <see cref="SchemaVersion"/> it compiled under, so a repeated batch skips
    /// <see cref="CompileBatch"/>. Keyed and capped like the plan cache: a batch
    /// that binds against the same schema under the same key compiles the same
    /// way, except one that resolved a session's <c>#temp</c> table, which isn't
    /// recorded. A hit only ever skips the pass, so an input the key leaves out
    /// can cost a batch its up-front error, never raise one it shouldn't.
    /// </summary>
    private readonly ConcurrentDictionary<PlanCacheKey, long> compiledBatches = new();

    /// <summary>
    /// How many keys <see cref="compiledBatches"/> holds, counted as
    /// <see cref="planCacheCount"/> is.
    /// </summary>
    private int compiledBatchCount;

    /// <summary>
    /// Compiles a batch before any of it runs, as real does, so an error that
    /// real raises while compiling stops the batch before its first statement
    /// rather than where the error sits. The batch's text walks the dispatch
    /// loop on <paramref name="compileBatch"/>, a throwaway context, the way a
    /// module body binds at <c>CREATE</c>: every statement parses and binds,
    /// nothing runs, and a statement naming an object that doesn't exist yet
    /// defers to when it runs. Real reports every binder error the batch holds,
    /// unless its parse phase fails first (a syntax error, an undeclared
    /// variable), which it reports alone (probed 2026-09-24 against SQL Server
    /// 2025).
    /// </summary>
    /// <returns>The error or errors that stop the batch, or <see langword="null"/> when it compiled.</returns>
    /// <remarks>
    /// Real keeps compiling the statements after one it defers; the walk stops at
    /// a deferred DML target, because its recovery scan can't tell where that
    /// statement ended, so an error past it surfaces when its statement runs.
    /// </remarks>
    private SimulatedSqlException? CompileBatch(BatchContext compileBatch, PlanCacheKey? key)
    {
        var schemaVersion = Volatile.Read(ref this.SchemaVersion);
        if (key is { } cached && this.compiledBatches.TryGetValue(cached, out var compiledUnder) && compiledUnder == schemaVersion)
            return null;

        var errors = new List<SimulatedSqlException>();
        compileBatch.CurrentStatement.UtcNow = DateTime.UtcNow;
        try
        {
            _ = this.BindWithoutRunning(compileBatch, errors);
        }
        catch (SimulatedSqlException parsePhase)
        {
            return parsePhase;
        }

        if (errors.Count > 0)
            return SimulatedSqlException.Aggregate(errors);

        if (key is { } compiled && !compileBatch.ResolvedTempTable)
        {
            if (this.compiledBatches.ContainsKey(compiled))
                this.compiledBatches[compiled] = schemaVersion;
            else if (Volatile.Read(ref this.compiledBatchCount) < PlanCacheCapacity && this.compiledBatches.TryAdd(compiled, schemaVersion))
                _ = Interlocked.Increment(ref this.compiledBatchCount);
        }
        return null;
    }

    /// <summary>
    /// The throwaway context <see cref="CompileBatch"/> walks
    /// <paramref name="executing"/>'s text on: the same command, a copy of the
    /// variables and table variables its parameters seeded (so the walk's own
    /// <c>DECLARE</c>s don't collide with the run's), and the same frame and
    /// error attribution.
    /// </summary>
    private static BatchContext CompileContextFor(BatchContext executing, SimulatedDbCommand command)
    {
        var variables = new Dictionary<string, VariableSlot>(executing.Variables, BatchContext.VariableNameComparer);
        var compile = executing.ProcFrame is { } frame
            ? new BatchContext(command, variables, new ProcFrame(frame.ProcedureName, frame.IsDynamicSql))
            : new BatchContext(command, variables);
        foreach (var (name, table) in executing.TableVariables)
            compile.TableVariables[name] = table;
        compile.LineOffset = executing.LineOffset;
        compile.ErrorProcedureName = executing.ErrorProcedureName;
        compile.ForceTempTableScope = executing.ForceTempTableScope;
        return compile;
    }
}
