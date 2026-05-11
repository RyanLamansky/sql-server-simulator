using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs a <c>PRINT &lt;expression&gt;</c> statement. The
    /// expression is parsed unconditionally (advancing the cursor) and
    /// evaluated only when not in skip mode so an un-taken <c>IF</c> branch
    /// doesn't surface runtime errors from the value computation. Whatever
    /// value the expression produces is discarded — the simulator doesn't
    /// expose an <c>InfoMessage</c> event on <see cref="SimulatedDbConnection"/>
    /// (<c>DbConnection</c> doesn't define one, and a public surface for
    /// observing PRINT output isn't justified yet). Probed against SQL Server
    /// 2025 (2026-05-11): PRINT resets <c>@@ROWCOUNT</c> to 0 (the dispatcher
    /// applies the reset on return); NULL operand emits an empty message;
    /// long strings truncate at 8000 / 4000 chars depending on collation —
    /// none of which the simulator needs to model when output is discarded.
    /// </summary>
    /// <remarks>
    /// Type validity follows from normal expression evaluation: <c>PRINT 'val=' + 5</c>
    /// raises Msg 245 from the <c>+</c> operator (matches probe). One known
    /// fidelity gap: real SQL Server raises Msg 1046 ("Subqueries are not
    /// allowed in this context") when a scalar subquery appears in the PRINT
    /// operand; the simulator silently evaluates it.
    /// </remarks>
    private static void ParsePrintStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume PRINT
        var expression = Expression.Parse(context);
        if (batch.IsSkipping)
            return;
        // Evaluate for side effects (surfacing any runtime errors from the
        // operand's type / coercion path) and discard the result.
        _ = expression.Run(new RuntimeContext(NoColumnResolver, batch));
    }
}
