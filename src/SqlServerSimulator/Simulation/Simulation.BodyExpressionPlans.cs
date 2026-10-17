using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the value or condition at the cursor with <paramref name="parse"/>
    /// — a <c>SET</c>'s or <c>DECLARE</c>'s value, a <c>RETURN</c>'s, an
    /// <c>IF</c>'s or <c>WHILE</c>'s condition — or, in a scalar function's
    /// body, takes the tree an earlier call's parse of the same text built and
    /// jumps past its tokens. Only <c>SELECT</c> and DML statements have plans,
    /// so a body of <c>DECLARE</c> / <c>SET</c> / <c>IF</c> / <c>RETURN</c>
    /// otherwise reads every expression again per call, and per row of a query
    /// calling it; the statements around the expression still dispatch as
    /// written, so the per-statement clock, <c>@@ROWCOUNT</c>, the nesting
    /// level and how an error is attributed are the parsed body's.
    /// </summary>
    /// <remarks>
    /// Kept only for a tree whose parse does nothing else
    /// (<see cref="BooleanExpression.ParsesWithoutEffects"/>) and parsed
    /// outside skip mode, on the function itself
    /// (<see cref="Schemas.ScalarFunction.BodyExpressions"/>), and taken again
    /// under the schema version, database and parse-time settings it parsed
    /// under — in skip mode too, where the walk of an untaken branch would
    /// parse it to the same tree. A function body's variables are its
    /// parameters and its own declarations, so a name binds alike on every
    /// call.
    /// </remarks>
    private static T ParseBodyExpression<T>(ParserContext context, Func<ParserContext, T> parse)
        where T : ExpressionNode
    {
        var batch = context.Batch;
        if (batch.UdfFrame is not { Function: { } function }
            || batch.CreateTimeBinding
            || batch.BindErrors is not null
            || batch.DependencySink is not null
            || batch.FunctionBodyShape is not null
            || batch.InlinedCalls is not null
            || context.CurrentDatabase is not { } database)
        {
            return parse(context);
        }
        var connection = context.Connection;
        var start = context.SaveCheckpoint();
        var schemaVersion = Volatile.Read(ref connection.Simulation.SchemaVersion);
        if (function.BodyExpressions.TryGetValue(start.MemoPosition, out var plan)
            && plan.Parsed is T kept
            && plan.SchemaVersion == schemaVersion
            && ReferenceEquals(plan.Database, database)
            && plan.CompatibilityLevel == database.CompatibilityLevel
            && ReferenceEquals(plan.CollationName, database.CollationName)
            && plan.QuotedIdentifiers == context.QuotedIdentifiers
            && plan.AnsiNulls == connection.AnsiNulls
            && plan.ConcatNullYieldsNull == connection.ConcatNullYieldsNull
            && ReferenceEquals(plan.DateFormat, connection.DateFormat)
            && context.CanJumpTo(plan.End))
        {
            context.JumpTo(plan.End);
            context.SimpleParameterizationBlocked |= plan.BlocksSimpleParameterization;
            return kept;
        }
        var blocked = context.SimpleParameterizationBlocked;
        var messages = connection.PendingMessages.Count;
        var parsed = parse(context);
        if (!batch.IsSkipping
            && context.HoldsTokenSequence
            && connection.PendingMessages.Count == messages
            && Volatile.Read(ref connection.Simulation.SchemaVersion) == schemaVersion
            && BooleanExpression.ParsesWithoutEffects(parsed))
        {
            function.KeepBodyExpression(start.MemoPosition, new BodyExpressionPlan(
                parsed, context.SaveCheckpoint(), schemaVersion, database, context, context.SimpleParameterizationBlocked && !blocked));
        }
        return parsed;
    }
}
