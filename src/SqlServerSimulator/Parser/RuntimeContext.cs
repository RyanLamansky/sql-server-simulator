using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Runtime context for one <see cref="Expression.Run(RuntimeContext)"/> call: the column
/// resolver that maps multi-part names to values for the current row, plus
/// the <see cref="BatchContext"/> the expression is executing in. Bundled
/// into a single parameter so most expressions reach for
/// <see cref="ResolveColumn"/> and ignore <see cref="Batch"/>, while late-
/// bound expressions (<see cref="Expressions.CurrentTimeFunction"/>,
/// <see cref="Expressions.LastIdentityExpression"/>,
/// <see cref="Expressions.RowCountExpression"/>,
/// <see cref="Expressions.IdentCurrent"/>) read per-statement / per-session /
/// per-database state through <see cref="Batch"/> at evaluation time —
/// correct even when the expression was parsed in a different batch (e.g. a
/// column default's <c>getutcdate()</c> parsed at CREATE TABLE and run on
/// every later INSERT).
/// </summary>
internal readonly struct RuntimeContext(Func<MultiPartName, SqlValue> resolveColumn, BatchContext batch)
{
    /// <summary>Maps a (possibly qualified) column name to its value for the current row.</summary>
    public readonly Func<MultiPartName, SqlValue> ResolveColumn = resolveColumn;

    /// <summary>The batch this expression is executing in.</summary>
    public readonly BatchContext Batch = batch;

    /// <summary>
    /// Builds a context with a different <see cref="ResolveColumn"/> while
    /// preserving the batch. Useful in executors that build per-row
    /// resolvers and recurse into nested expressions.
    /// </summary>
    public RuntimeContext WithResolver(Func<MultiPartName, SqlValue> resolveColumn) =>
        new(resolveColumn, this.Batch);
}
