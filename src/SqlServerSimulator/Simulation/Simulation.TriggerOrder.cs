using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// <c>sp_settriggerorder @triggername, @order, @stmttype [, @namespace]</c> —
    /// pins a trigger to fire first or last among the AFTER triggers a given
    /// action runs on its table. Named and positional argument forms both work,
    /// and <c>@order</c> / <c>@stmttype</c> are case-insensitive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordering is per action: making a multi-action trigger first for INSERT
    /// leaves its UPDATE position alone. <c>@order = 'None'</c> clears both
    /// slots for that action. At most one trigger per table may hold each slot
    /// per action — a second claimant raises <strong>Msg 15130</strong>, though
    /// re-ordering the trigger that already holds it is fine.
    /// </para>
    /// <para>
    /// Rejections, all probe-confirmed against SQL Server 2025: an
    /// <c>@order</c> / <c>@stmttype</c> outside the accepted sets is
    /// <strong>Msg 15600</strong>; a trigger that doesn't handle the requested
    /// action is <strong>Msg 15125</strong>; an INSTEAD OF trigger is
    /// <strong>Msg 15133</strong> (at most one exists per action, so ordering
    /// is meaningless); an unresolvable name is <strong>Msg 15165</strong>.
    /// <c>@namespace</c> is accepted and ignored — it selects DATABASE / SERVER
    /// scope for DDL triggers, which don't fire yet.
    /// </para>
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSetTriggerOrder(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (triggerName, order, statementType) = ParseSetTriggerOrderArgs(arguments);
        if (string.IsNullOrEmpty(triggerName) || order is null || statementType is null)
            throw SimulatedSqlException.InvalidTriggerOrderParameter();

        var action = statementType switch
        {
            var s when BuiltInToken.Equals(s, "INSERT") => TriggerActions.Insert,
            var s when BuiltInToken.Equals(s, "UPDATE") => TriggerActions.Update,
            var s when BuiltInToken.Equals(s, "DELETE") => TriggerActions.Delete,
            _ => throw SimulatedSqlException.InvalidTriggerOrderParameter(),
        };
        var isFirst = BuiltInToken.Equals(order, "First");
        var isLast = BuiltInToken.Equals(order, "Last");
        if (!isFirst && !isLast && !BuiltInToken.Equals(order, "None"))
            throw SimulatedSqlException.InvalidTriggerOrderParameter();

        var trigger = ResolveTriggerForOrdering(batch, triggerName);
        if (trigger.Timing == TriggerTiming.InsteadOf)
            throw SimulatedSqlException.InsteadOfTriggerCannotBeOrdered(triggerName);
        if ((trigger.Actions & action) == 0)
            throw SimulatedSqlException.TriggerIsNotATriggerForAction(triggerName, statementType);

        // A slot is only contested when a *different* trigger on the same
        // parent already holds it; re-pinning the incumbent is a no-op move.
        foreach (var peer in EnumerateTriggersOn(batch, trigger.Parent))
        {
            if (ReferenceEquals(peer, trigger))
                continue;
            if ((isFirst && (peer.FirstForActions & action) != 0) || (isLast && (peer.LastForActions & action) != 0))
                throw SimulatedSqlException.TriggerOrderAlreadyExists(order, statementType);
        }

        // Setting one slot vacates the other: a trigger can't be both.
        trigger.FirstForActions = isFirst ? trigger.FirstForActions | action : trigger.FirstForActions & ~action;
        trigger.LastForActions = isLast ? trigger.LastForActions | action : trigger.LastForActions & ~action;
        yield break;
    }

    /// <summary>
    /// Resolves <c>@triggername</c> (bare or schema-qualified) to a DML
    /// trigger, raising <strong>Msg 15165</strong> when nothing matches.
    /// </summary>
    private static Trigger ResolveTriggerForOrdering(BatchContext batch, string triggerName)
    {
        var leaf = triggerName;
        var dot = triggerName.LastIndexOf('.');
        if (dot >= 0)
            leaf = triggerName[(dot + 1)..].Trim('[', ']');
        leaf = leaf.Trim('[', ']');
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var candidate in schema.Triggers.Values)
            {
                if (batch.CurrentDatabase.Collation.Equals(candidate.Name, leaf))
                    return candidate;
            }
        }
        throw SimulatedSqlException.CouldNotFindObjectOrNoPermission(triggerName);
    }

    /// <summary>Every DML trigger attached to <paramref name="parent"/>.</summary>
    private static IEnumerable<Trigger> EnumerateTriggersOn(BatchContext batch, object parent)
    {
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var candidate in schema.Triggers.Values)
            {
                if (ReferenceEquals(candidate.Parent, parent))
                    yield return candidate;
            }
        }
    }

    private static (string? TriggerName, string? Order, string? StatementType) ParseSetTriggerOrderArgs(List<ProcArgument> arguments)
    {
        string? triggerName = null, order = null, statementType = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: triggerName = CatalogStringArg(arg); break;
                    case 1: order = CatalogStringArg(arg); break;
                    case 2: statementType = CatalogStringArg(arg); break;
                    // The fourth positional is @namespace (DATABASE / SERVER
                    // scope for DDL triggers), accepted and ignored.
                    case 3: break;
                    default: throw SimulatedSqlException.InvalidTriggerOrderParameter();
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "triggername"): triggerName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "order"): order = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "stmttype"): statementType = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "namespace"): break;
                default: throw SimulatedSqlException.InvalidTriggerOrderParameter();
            }
        }
        return (triggerName, order, statementType);
    }
}
