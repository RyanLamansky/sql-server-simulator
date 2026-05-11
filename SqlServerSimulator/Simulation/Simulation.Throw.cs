using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and dispatches a <c>THROW</c> statement in both forms:
    /// <list type="bullet">
    /// <item><c>THROW;</c> — no args. Re-raises the in-flight error from
    /// the enclosing CATCH; must be inside a CATCH (Msg 10704 otherwise,
    /// probe-confirmed). Reconstructs the <see cref="SimulatedSqlException"/>
    /// from <see cref="BatchContext.InFlightError"/>.</item>
    /// <item><c>THROW number, message, state;</c> — value form. Raises a
    /// new <see cref="SimulatedSqlException"/> with the supplied number
    /// (50000-2147483647 in real SQL Server; the simulator doesn't enforce
    /// the range until apps need it), message (string-typed expression),
    /// and state (tinyint-typed expression). Severity is always class 16
    /// per real SQL Server — probe-confirmed against SQL Server 2025
    /// (2026-05-12) that <c>THROW 50001, 'custom', 7</c> reports Class 16
    /// State 7.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// In a TRY frame the exception is caught by the per-statement dispatch
    /// wrapper and stored into <see cref="BatchContext.InFlightError"/>;
    /// outside a TRY frame it propagates out of the batch.
    /// </para>
    /// <para>
    /// Skip-mode gating: <c>THROW;</c> in a skipped branch is a compile-time
    /// check (Msg 10704 — fires even from un-taken IF branches matching the
    /// pattern of Msg 135 / 178). The runtime raise itself is gated on
    /// <c>!IsSkipping</c>.
    /// </para>
    /// <para>
    /// The value form supports literal and variable expressions for each
    /// argument; runtime evaluation goes through standard
    /// <see cref="Expression.Run"/>, so coercion via the type system is the
    /// same as elsewhere. Formatted-message arguments (<c>%d</c> / <c>%s</c>
    /// placeholders) — real SQL Server's <c>FORMATMESSAGE</c>-style
    /// substitution — aren't modeled in this bundle; defer to a follow-on
    /// alongside <c>RAISERROR</c>.
    /// </para>
    /// </remarks>
    private static void ParseThrowStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume THROW

        // Re-raise form: bare THROW followed by a statement boundary.
        if (IsStatementBoundary(context.Token))
        {
            // Compile-time check (fires even in skipped branches, matching
            // real SQL Server's behavior for Msg 178 / Msg 135).
            if (batch.CatchDepth == 0)
                throw SimulatedSqlException.ThrowMustBeInsideCatch();

            if (batch.IsSkipping)
                return;

            // Reconstruct the in-flight error and re-raise. Inside a CATCH
            // InFlightError is non-null by construction (the CATCH only ran
            // because the matching TRY caught something).
            var err = batch.InFlightError!.Value;
            throw SimulatedSqlException.ThrowRaised(err.Number, err.Message, err.State);
        }

        // Value form: parse three comma-separated expressions.
        var numberExpr = Expression.Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var messageExpr = Expression.Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var stateExpr = Expression.Parse(context);

        if (batch.IsSkipping)
            return;

        var runtime = new RuntimeContext(NoColumnResolver, batch);
        var numberValue = numberExpr.Run(runtime).CoerceTo(SqlType.Int32);
        var messageValue = messageExpr.Run(runtime).CoerceTo(SqlType.NVarchar);
        var stateValue = stateExpr.Run(runtime).CoerceTo(SqlType.TinyInt);

        // SQL Server accepts NULL on any THROW arg (it converts to a
        // run-time error of its own). Apps rarely hit this — surface a
        // generic Msg-style error rather than modeling the exact path.
        if (numberValue.IsNull || messageValue.IsNull || stateValue.IsNull)
            throw SimulatedSqlException.ThrowRaised(0, "THROW: arguments cannot be NULL.", 1);

        throw SimulatedSqlException.ThrowRaised(numberValue.AsInt32, messageValue.AsString, stateValue.AsByte);
    }
}
