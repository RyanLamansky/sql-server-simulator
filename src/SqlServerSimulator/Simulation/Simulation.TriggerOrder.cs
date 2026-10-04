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
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSetTriggerOrder(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        // The lines each refusal comes from in real's own procedure source
        // (probed 2026-10-04 against SQL Server 2025) let the errors carry
        // them, attributed to sp_settriggerorder.
        var (triggerName, order, statementType, triggerNamespace) = ParseSetTriggerOrderArgs(arguments);
        if (triggerName is null)
            throw SimulatedSqlException.CouldNotFindObjectOrNoPermission("(null)").AtSystemProcedureLine(142);
        if (triggerName.Length == 0 || order is null || statementType is null)
            throw SimulatedSqlException.InvalidTriggerOrderParameter();

        // A database- or server-scope @namespace takes no schema prefix, a
        // DML action's included.
        if (triggerNamespace is not null && triggerName.Contains('.', StringComparison.Ordinal)
            && (BuiltInToken.Equals(triggerNamespace, "DATABASE") || BuiltInToken.Equals(triggerNamespace, "SERVER")))
        {
            throw SimulatedSqlException.SchemaPrefixOnScopedTrigger().AtSystemProcedureLine(78);
        }

        // @namespace selects a server- or database-scope trigger; a DML
        // action keeps the table-trigger path whatever it says.
        if (triggerNamespace is not null
            && !BuiltInToken.Equals(statementType, "INSERT") && !BuiltInToken.Equals(statementType, "UPDATE") && !BuiltInToken.Equals(statementType, "DELETE"))
        {
            var serverScope = BuiltInToken.Equals(triggerNamespace, "SERVER");
            if (!serverScope && !BuiltInToken.Equals(triggerNamespace, "DATABASE"))
                throw SimulatedSqlException.InvalidTriggerOrderParameter();
            SetScopedTriggerOrder(batch, triggerName, order, statementType, serverScope);
            yield break;
        }

        var action = statementType switch
        {
            var s when BuiltInToken.Equals(s, "INSERT") => TriggerActions.Insert,
            var s when BuiltInToken.Equals(s, "UPDATE") => TriggerActions.Update,
            var s when BuiltInToken.Equals(s, "DELETE") => TriggerActions.Delete,
            _ => throw SimulatedSqlException.InvalidTriggerOrderParameter().AtSystemProcedureLine(113),
        };
        var isFirst = BuiltInToken.Equals(order, "First");
        var isLast = BuiltInToken.Equals(order, "Last");
        if (!isFirst && !isLast && !BuiltInToken.Equals(order, "None"))
            throw SimulatedSqlException.InvalidTriggerOrderParameter().AtSystemProcedureLine(56);

        var trigger = ResolveTriggerForOrdering(batch, triggerName);
        if (trigger.Timing == TriggerTiming.InsteadOf)
            throw SimulatedSqlException.InsteadOfTriggerCannotBeOrdered(triggerName).AtSystemProcedureLine(153);
        if ((trigger.Actions & action) == 0)
            throw SimulatedSqlException.TriggerIsNotATriggerForAction(triggerName, statementType).AtSystemProcedureLine(151);

        // A slot is only contested when a *different* trigger on the same
        // parent already holds it; re-pinning the incumbent is a no-op move.
        foreach (var peer in EnumerateTriggersOn(batch, trigger.Parent))
        {
            if (ReferenceEquals(peer, trigger))
                continue;
            if ((isFirst && (peer.FirstForActions & action) != 0) || (isLast && (peer.LastForActions & action) != 0))
                throw SimulatedSqlException.TriggerOrderAlreadyExists(order, statementType).AtSystemProcedureLine(163);
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
        foreach (var (_, schema) in batch.CurrentDatabase.Schemas)
        {
            foreach (var (_, candidate) in schema.Triggers)
            {
                if (batch.CurrentDatabase.Collation.Equals(candidate.Name, leaf))
                    return candidate;
            }
        }
        throw SimulatedSqlException.CouldNotFindObjectOrNoPermission(triggerName).AtSystemProcedureLine(142);
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
