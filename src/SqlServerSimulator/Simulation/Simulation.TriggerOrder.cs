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
    /// <c>@namespace</c> selects a database- or server-scope trigger instead,
    /// ordered per event by <see cref="SetScopedTriggerOrder"/>.
    /// </para>
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSetTriggerOrder(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        // The lines each refusal comes from in real's own procedure source
        // (probed 2026-10-04 and 2026-10-17 against SQL Server 2025) let the
        // errors carry them. The order, the statement type's presence and the
        // namespace are judged ahead of the procedure's transaction, as one
        // check; a class 15 refusal ends the procedure and leaves the
        // caller's batch running.
        var (triggerName, order, statementType, triggerNamespace) = ParseSetTriggerOrderArgs(arguments);
        var isFirst = order is not null && BuiltInToken.Equals(order, "First");
        var isLast = order is not null && BuiltInToken.Equals(order, "Last");
        var serverScope = triggerNamespace is not null && BuiltInToken.Equals(triggerNamespace, "SERVER");
        var scoped = serverScope || (triggerNamespace is not null && BuiltInToken.Equals(triggerNamespace, "DATABASE"));
        if (order is null || (!isFirst && !isLast && !BuiltInToken.Equals(order, "None")) || statementType is null || (triggerNamespace is not null && !scoped))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidTriggerOrderParameter(), 56);

        // Past its argument checks the procedure works in a transaction of its
        // own, committed after a refusal it raises.
        foreach (var outcome in InProcedureTransaction(batch, Ordered(batch, calledAs, triggerName, order, statementType, scoped, serverScope, isFirst, isLast), ProcedureTransactionEnding.CommitAfter))
            yield return outcome;
    }

    /// <summary><see cref="InvokeSpSetTriggerOrder"/>'s work once its transaction is open.</summary>
    private static IEnumerable<SimulatedStatementOutcome> Ordered(BatchContext batch, string calledAs, string? triggerName, string order, string statementType, bool scoped, bool serverScope, bool isFirst, bool isLast)
    {
        // A database- or server-scope trigger takes no schema prefix.
        if (scoped && triggerName is not null && triggerName.Contains('.', StringComparison.Ordinal))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.SchemaPrefixOnScopedTrigger(), 78);

        // @namespace selects a server- or database-scope trigger, a DML
        // statement type's included; without one the statement type must be
        // a DML action.
        if (scoped)
        {
            SetScopedTriggerOrder(batch, calledAs, triggerName, order, statementType, serverScope, isFirst, isLast);
            yield break;
        }

        var action = statementType switch
        {
            var s when BuiltInToken.Equals(s, "INSERT") => TriggerActions.Insert,
            var s when BuiltInToken.Equals(s, "UPDATE") => TriggerActions.Update,
            var s when BuiltInToken.Equals(s, "DELETE") => TriggerActions.Delete,
            _ => throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidTriggerOrderParameter(), 113),
        };

        var trigger = ResolveTriggerForOrdering(batch, calledAs, triggerName);
        if (trigger.Timing == TriggerTiming.InsteadOf)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InsteadOfTriggerCannotBeOrdered(triggerName!), 153);
        if ((trigger.Actions & action) == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.TriggerIsNotATriggerForAction(triggerName!, statementType), 151);

        // A slot is only contested when a *different* trigger on the same
        // parent already holds it; re-pinning the incumbent is a no-op move.
        foreach (var peer in EnumerateTriggersOn(batch, trigger.Parent))
        {
            if (ReferenceEquals(peer, trigger))
                continue;
            if ((isFirst && (peer.FirstForActions & action) != 0) || (isLast && (peer.LastForActions & action) != 0))
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.TriggerOrderAlreadyExists(order, statementType), 163);
        }

        // Setting one slot vacates the other: a trigger can't be both.
        trigger.FirstForActions = isFirst ? trigger.FirstForActions | action : trigger.FirstForActions & ~action;
        trigger.LastForActions = isLast ? trigger.LastForActions | action : trigger.LastForActions & ~action;
        yield break;
    }

    /// <summary>
    /// Resolves <c>@triggername</c> (bare or schema-qualified) to a DML
    /// trigger, raising <strong>Msg 15165</strong> when nothing matches, a NULL name
    /// reported as <c>(null)</c>.
    /// </summary>
    private static Trigger ResolveTriggerForOrdering(BatchContext batch, string calledAs, string? triggerName)
    {
        if (triggerName is null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CouldNotFindObjectOrNoPermission("(null)"), 142);
        var leaf = triggerName;
        var dot = triggerName.LastIndexOf('.');
        if (dot >= 0)
            leaf = triggerName[(dot + 1)..].Trim('[', ']');
        leaf = leaf.Trim('[', ']');
        foreach (var (_, schema) in batch.CurrentDatabase.Schemas)
        {
            foreach (var (_, candidate) in schema.Triggers)
            {
                if (batch.CurrentDatabase.Collation.Equals(candidate.Name, leaf))
                    return candidate;
            }
        }
        throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CouldNotFindObjectOrNoPermission(triggerName), 142);
    }

    /// <summary>Every DML trigger attached to <paramref name="parent"/>.</summary>
    private static IEnumerable<Trigger> EnumerateTriggersOn(BatchContext batch, object parent)
    {
        foreach (var (_, schema) in batch.CurrentDatabase.Schemas)
        {
            foreach (var (_, candidate) in schema.Triggers)
            {
                if (ReferenceEquals(candidate.Parent, parent))
                    yield return candidate;
            }
        }
    }

    private static (string? TriggerName, string? Order, string? StatementType, string? Namespace) ParseSetTriggerOrderArgs(List<ProcArgument> arguments)
    {
        string? triggerName = null, order = null, statementType = null, triggerNamespace = null;
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
                    case 3: triggerNamespace = CatalogStringArg(arg); break;
                    default: throw SimulatedSqlException.InvalidTriggerOrderParameter();
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "triggername"): triggerName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "order"): order = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "stmttype"): statementType = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "namespace"): triggerNamespace = CatalogStringArg(arg); break;
                default: throw SimulatedSqlException.InvalidTriggerOrderParameter();
            }
        }
        return (triggerName, order, statementType, triggerNamespace);
    }
}
