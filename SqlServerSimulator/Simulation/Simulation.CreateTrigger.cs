using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [OR ALTER] TRIGGER [schema.]name ON [schema.]parent
    /// { AFTER | FOR | INSTEAD OF } { INSERT | UPDATE | DELETE } [, ...]
    /// AS body</c>. Body source is captured between <c>AS</c> (exclusive)
    /// and the trailing statement boundary; re-tokenized per fire inside
    /// a child <see cref="BatchContext"/> with a <see cref="TriggerFrame"/>
    /// seeded with the inserted / deleted rowsets. Only AFTER (and its
    /// synonym FOR) is modeled — INSTEAD OF raises <see cref="NotSupportedException"/>
    /// at parse. Probe-confirmed against SQL Server 2025 (2026-05-13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trigger NAME lives in the schema namespace; collision with
    /// any existing object (table / view / function / proc / sequence /
    /// trigger) raises Msg 2714 (same rule as <see cref="TryParseCreateProcedure"/>).
    /// The parent table must already exist — Msg 8197 otherwise.
    /// </para>
    /// <para>
    /// CREATE OR ALTER upserts; ALTER requires the trigger to exist.
    /// Both replace the body / actions in place but preserve the
    /// <see cref="Trigger.ObjectId"/>.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateTrigger(ParserContext context, bool isAlter, bool createOrAlter)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var triggerName = BatchContext.ParseObjectName(context);
        if (!context.Batch.TryResolveSchema(triggerName, out var triggerSchema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(triggerName.ImmediateQualifier ?? Database.DefaultSchemaName);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var parentName = BatchContext.ParseObjectName(context);

        context.MoveNextRequired();

        // Timing: AFTER (contextual) / FOR (reserved synonym) / INSTEAD OF
        // (contextual + reserved). The simulator models AFTER only; INSTEAD
        // OF parses but raises NotSupportedException so apps with INSTEAD
        // OF triggers fail loudly rather than silently producing wrong
        // behavior.
        var timing = TriggerTiming.After;
        switch (context.Token)
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Instead }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Of })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                timing = TriggerTiming.InsteadOf;
                context.MoveNextRequired();
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.After }:
            case ReservedKeyword { Keyword: Keyword.For }:
                context.MoveNextRequired();
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // Actions list: INSERT / UPDATE / DELETE, comma-separated.
        var actions = TriggerActions.None;
        while (true)
        {
            actions |= context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Insert } => TriggerActions.Insert,
                ReservedKeyword { Keyword: Keyword.Update } => TriggerActions.Update,
                ReservedKeyword { Keyword: Keyword.Delete } => TriggerActions.Delete,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        // Optional WITH option list (parse-and-ignore: ENCRYPTION,
        // EXECUTE AS, APPEND, NOT FOR REPLICATION). For now just skip
        // tokens until AS, matching the lax stance the simulator takes
        // for proc body options.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            while (context.Token is not (null or ReservedKeyword { Keyword: Keyword.As }))
                context.MoveNextRequired();
        }

        // NOT FOR REPLICATION before AS is also valid (parse-and-ignore).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Replication })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var commandText = context.Command.CommandText;
        context.MoveNextOptional();
        var bodyStart = context.Token?.StartIndex ?? commandText.Length;
        var bodyEnd = commandText.Length;
        while (context.Token is not null)
        {
            bodyEnd = context.Token.EndIndex;
            context.MoveNextOptional();
        }
        var bodyText = commandText[bodyStart..bodyEnd];

        if (context.Batch.IsSkipping)
            return true;

        if (timing == TriggerTiming.InsteadOf)
            throw new NotSupportedException("INSTEAD OF triggers aren't modeled. Only AFTER (and the FOR synonym) triggers are supported; use AFTER if the actual semantic suffices, or apply the trigger's logic at the application layer.");

        // Resolve parent table. Real SQL Server allows AFTER triggers on
        // base tables only (views accept INSTEAD OF triggers, which the
        // simulator doesn't model). System tables / table variables /
        // temp tables aren't valid parents either.
        if (!context.Batch.TryResolveTable(parentName, out var parentTable)
            || parentTable.IsTableVariable
            || BatchContext.IsLocalTempName(parentTable.Name))
        {
            throw SimulatedSqlException.ObjectDoesNotExistForTrigger(parentName.ToString());
        }

        var existed = triggerSchema.Triggers.TryGetValue(triggerName.Leaf, out var existing);
        if (!isAlter && !createOrAlter)
        {
            if (existed
                || triggerSchema.HeapTables.ContainsKey(triggerName.Leaf)
                || triggerSchema.Functions.ContainsKey(triggerName.Leaf)
                || triggerSchema.Views.ContainsKey(triggerName.Leaf)
                || triggerSchema.Procedures.ContainsKey(triggerName.Leaf)
                || triggerSchema.Sequences.ContainsKey(triggerName.Leaf))
            {
                throw SimulatedSqlException.ThereIsAlreadyAnObject(triggerName.Leaf);
            }
        }
        if (isAlter && !existed)
            throw SimulatedSqlException.InvalidObjectName(triggerName);

        var objectId = existed ? existing!.ObjectId : context.CurrentDatabase.AllocateObjectId();
        var trigger = new Trigger(
            triggerSchema,
            triggerName.Leaf,
            objectId,
            parentTable,
            actions,
            timing,
            bodyText,
            createDate: existed ? existing!.CreateDate : context.Batch.CurrentStatement.UtcNow);
        triggerSchema.Triggers[triggerName.Leaf] = trigger;
        return true;
    }

    /// <summary>
    /// Parses <c>{ DISABLE | ENABLE } TRIGGER name ON table</c>. Toggles
    /// <see cref="Trigger.IsDisabled"/>; the matching DML still parses
    /// and writes normally but the trigger body is skipped while
    /// disabled. <c>DISABLE TRIGGER ALL</c> / <c>ENABLE TRIGGER ALL</c>
    /// (toggling every trigger on the table at once) is supported too.
    /// </summary>
    private static bool TryParseEnableOrDisableTrigger(ParserContext context, bool disable)
    {
        // Cursor is on DISABLE / ENABLE (ContextualKeyword). Advance and
        // require TRIGGER.
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Trigger })
            return false;

        context.MoveNextRequired();

        // Two shapes: a trigger name, or the literal keyword ALL. Both
        // are followed by ON table.
        var allTriggers = false;
        MultiPartName triggerName = default;
        if (context.Token is ReservedKeyword { Keyword: Keyword.All })
        {
            allTriggers = true;
            context.MoveNextRequired();
        }
        else
        {
            if (context.Token is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            triggerName = BatchContext.ParseObjectName(context);
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var parentName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(parentName, out var parentTable))
            throw SimulatedSqlException.InvalidObjectName(parentName);

        if (allTriggers)
        {
            foreach (var schema in context.CurrentDatabase.Schemas.Values)
            {
                foreach (var trigger in schema.Triggers.Values)
                {
                    if (ReferenceEquals(trigger.ParentTable, parentTable))
                        trigger.IsDisabled = disable;
                }
            }
            return true;
        }

        if (!context.Batch.TryResolveSchema(triggerName, out var triggerSchema)
            || !triggerSchema.Triggers.TryGetValue(triggerName.Leaf, out var matchedTrigger)
            || !ReferenceEquals(matchedTrigger.ParentTable, parentTable))
        {
            throw SimulatedSqlException.InvalidObjectName(triggerName);
        }
        matchedTrigger.IsDisabled = disable;
        return true;
    }
}
