using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [OR ALTER] TRIGGER [schema.]name ON [schema.]parent
    /// { AFTER | FOR | INSTEAD OF } { INSERT | UPDATE | DELETE } [, ...]
    /// AS body</c>. Body source is captured between <c>AS</c> (exclusive)
    /// and the trailing statement boundary; re-tokenized per fire inside
    /// a child <see cref="BatchContext"/> with a <see cref="TriggerFrame"/>
    /// seeded with the inserted / deleted rowsets. AFTER triggers attach
    /// to heap tables only (views raise Msg 8197 — probe-confirmed);
    /// INSTEAD OF triggers attach to either a heap table or a view. At
    /// most one INSTEAD OF trigger per action per target is permitted
    /// (Msg 2111). Probe-confirmed against SQL Server 2025 (2026-05-13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trigger NAME lives in the schema namespace; collision with
    /// any existing object (table / view / function / proc / sequence /
    /// trigger) raises Msg 2714 (same rule as <see cref="TryParseCreateProcedure"/>).
    /// The parent must already exist — Msg 8197 otherwise.
    /// </para>
    /// <para>
    /// CREATE OR ALTER upserts; ALTER requires the trigger to exist.
    /// Both replace the body / actions / timing in place but preserve the
    /// <see cref="SchemaObject.ObjectId"/>.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateTrigger(ParserContext context, bool isAlter, bool createOrAlter)
    {
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
            throw SimulatedSqlException.MustBeFirstStatementInBatch(isAlter ? "ALTER TRIGGER" : "CREATE TRIGGER");

        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var triggerName = BatchContext.ParseObjectName(context);
        RejectQualifiedModuleName(triggerName, "TRIGGER");
        if (!context.Batch.TryResolveSchema(triggerName, out var triggerSchema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(triggerName.ImmediateQualifier ?? Database.DefaultSchemaName);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // Branch on the parent-scope token: ON DATABASE → database-scope
        // DDL trigger (see DdlTrigger.cs and Simulation.InvokeDdlTrigger.cs); a
        // Name → DML trigger attached to a heap-table or view parent.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Database })
        {
            return ParseDdlTriggerBody(context, triggerName, triggerSchema, isAlter, createOrAlter);
        }

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var parentName = BatchContext.ParseObjectName(context);

        context.MoveNextRequired();

        // Optional WITH option list, which precedes the timing in real SQL
        // Server's grammar (ON table [WITH options] { FOR | AFTER | INSTEAD OF }).
        // ENCRYPTION parses-and-ignores; EXECUTE AS is captured for the per-fire
        // frame push. Comma-separated; ends at the timing keyword.
        string? executeAsClause = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            while (true)
            {
                switch (context.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.Execute or Keyword.Exec }:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                        executeAsClause = context.Token switch
                        {
                            Name principal => principal.Value,
                            Literal { Value: { IsNull: false } quoted } => quoted.AsString,
                            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                        };
                        context.MoveNextRequired();
                        break;
                    case UnquotedString { ContextualKeyword: ContextualKeyword.Encryption }:
                        context.MoveNextRequired();
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                if (context.Token is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }

        // Timing: AFTER (contextual) / FOR (reserved synonym) / INSTEAD OF
        // (contextual + reserved). INSTEAD OF replaces the DML on the
        // parent with the trigger body; AFTER fires post-heap-write.
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

        // NOT FOR REPLICATION before AS is also valid (parse-and-ignore).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // REPLICATION lives in the reserved Keyword enum, so the tokenizer
            // surfaces it as ReservedKeyword — not as the
            // ContextualKeyword.Replication UnquotedString form. Accept either
            // to survive both classification paths.
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Replication }
                and not UnquotedString { ContextualKeyword: ContextualKeyword.Replication })
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
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

        // Resolve the parent. INSTEAD OF accepts a heap table or a view;
        // AFTER accepts a heap table only (Msg 8197 on view target,
        // probe-confirmed). Table variables / temp tables aren't valid
        // parents in either case.
        SchemaObject parent;
        string parentKind;
        if (context.Batch.TryResolveView(parentName, out var parentView))
        {
            if (timing != TriggerTiming.InsteadOf)
                throw SimulatedSqlException.ObjectDoesNotExistForTrigger(parentName.ToString());
            parent = parentView;
            parentKind = "view";
        }
        else if (context.Batch.TryResolveTable(parentName, out var parentTable)
            && !parentTable.IsTableVariable
            && !BatchContext.IsLocalTempName(parentTable.Name))
        {
            parent = parentTable;
            parentKind = "table";
        }
        else
        {
            throw SimulatedSqlException.ObjectDoesNotExistForTrigger(parentName.ToString());
        }

        // At most one INSTEAD OF trigger per action per target (Msg 2111).
        // ALTER / CREATE OR ALTER replacing an existing trigger by the
        // same name is permitted; collision is only with a *different*
        // trigger covering an overlapping action.
        if (timing == TriggerTiming.InsteadOf)
        {
            foreach (var schema in context.CurrentDatabase.Schemas.Values)
            {
                foreach (var t in schema.Triggers.Values)
                {
                    if (!ReferenceEquals(t.Parent, parent)) continue;
                    if (t.Timing != TriggerTiming.InsteadOf) continue;
                    if (context.Batch.CurrentDatabase.Collation.Equals(t.Name, triggerName.Leaf)) continue;
                    var overlap = t.Actions & actions;
                    if (overlap == 0) continue;
                    throw SimulatedSqlException.InsteadOfTriggerAlreadyExists(
                        triggerName.Leaf, parentKind, parentName.ToString(), FirstActionName(overlap));
                }
            }
        }

        var existed = triggerSchema.Triggers.TryGetValue(triggerName.Leaf, out var existing);
        if (!isAlter && !createOrAlter && triggerSchema.HasNameInSharedNamespace(triggerName.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(triggerName.Leaf);
        // Replacement rules, in real's own order (the parent-object resolution
        // above already reported Msg 8197 for a target that doesn't exist, which
        // real reports ahead of these — probe-confirmed): a name another object
        // kind holds is Msg 2010, the same gate ALTER VIEW / FUNCTION /
        // PROCEDURE take (see ResolveModuleAlterTarget); a trigger attached to a
        // different parent is Msg 2110; and a name nothing holds is Msg 208 for
        // a bare ALTER, or a plain create for CREATE OR ALTER.
        if ((isAlter || createOrAlter) && !existed && triggerSchema.HasNameInSharedNamespace(triggerName.Leaf))
            throw SimulatedSqlException.CannotAlterIncompatibleObjectType(triggerName);
        if (isAlter && !existed)
            throw SimulatedSqlException.InvalidObjectName(triggerName);
        if (existed && (isAlter || createOrAlter) && !ReferenceEquals(existing!.Parent, parent))
            throw SimulatedSqlException.CannotAlterTriggerOnDifferentObject(triggerName, parentName);
        // Sch-M on the existing trigger instance's SchemaLock before
        // replacement — same pattern as ALTER PROCEDURE.
        if (existed)
            context.Batch.AcquireStatementLock(existing!.SchemaLock, LockMode.SchemaModification);

        var objectId = existed ? existing!.ObjectId : context.CurrentDatabase.AllocateObjectId();
        // Newlines before the body start, so per-fire body errors report a
        // line relative to the whole CREATE statement (probe-confirmed).
        var trigger = new Trigger(
            triggerSchema,
            triggerName.Leaf,
            objectId,
            parent,
            actions,
            timing,
            bodyText,
            createDate: existed ? existing!.CreateDate : context.Batch.CurrentStatement.UtcNow,
            bodyLineOffset: CountNewlines(commandText, context.Batch.CurrentStatement.StartIndex, bodyStart))
        {
            DefinitionText = BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, bodyEnd, isAlter, createOrAlter),
            ExecuteAsClause = executeAsClause,
        };
        if (existed)
            trigger.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        triggerSchema.Triggers[triggerName.Leaf] = trigger;
        RecordDdlEvent(
            context,
            existed ? "ALTER_TRIGGER" : "CREATE_TRIGGER",
            triggerSchema.Name,
            triggerName.Leaf,
            "TRIGGER",
            parent.Name,
            parentKind == "view" ? "VIEW" : "TABLE");
        return true;
    }

    /// <summary>Maps a TriggerActions flag to the spelled-out action name
    /// for Msg 2111 wording — first set bit by INSERT / UPDATE / DELETE
    /// priority order matching SQL Server's diagnostic shape.</summary>
    private static string FirstActionName(TriggerActions actions) =>
        (actions & TriggerActions.Insert) != 0 ? "INSERT"
        : (actions & TriggerActions.Update) != 0 ? "UPDATE"
        : "DELETE";

    /// <summary>
    /// Parses <c>{ DISABLE | ENABLE } TRIGGER name ON parent</c>. Toggles
    /// <see cref="Trigger.IsDisabled"/>; the matching DML still parses
    /// and writes normally but the trigger body is skipped while
    /// disabled. <c>DISABLE TRIGGER ALL</c> / <c>ENABLE TRIGGER ALL</c>
    /// (toggling every trigger on the parent at once) is supported too.
    /// The parent may be a heap table or a view.
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
        // are followed by ON parent.
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

        // ON DATABASE toggles a database-scope DDL trigger instead of a DML
        // one; the ALL form covers every DDL trigger in the database.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Database })
        {
            if (context.Batch.IsSkipping)
                return true;
            if (allTriggers)
            {
                foreach (var ddlTrigger in context.CurrentDatabase.DdlTriggers.Values)
                    ddlTrigger.IsDisabled = disable;
                return true;
            }
            if (!context.CurrentDatabase.DdlTriggers.TryGetValue(triggerName.Leaf, out var matchedDdlTrigger))
                throw SimulatedSqlException.InvalidObjectName(triggerName);
            matchedDdlTrigger.IsDisabled = disable;
            return true;
        }

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var parentName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
            return true;

        SchemaObject parent = context.Batch.TryResolveView(parentName, out var parentView)
            ? parentView
            : context.Batch.TryResolveTable(parentName, out var parentTable)
                ? parentTable
                : throw SimulatedSqlException.InvalidObjectName(parentName);

        if (allTriggers)
        {
            foreach (var schema in context.CurrentDatabase.Schemas.Values)
            {
                foreach (var trigger in schema.Triggers.Values)
                {
                    if (ReferenceEquals(trigger.Parent, parent))
                        trigger.IsDisabled = disable;
                }
            }
            return true;
        }

        if (!context.Batch.TryResolveSchema(triggerName, out var triggerSchema)
            || !triggerSchema.Triggers.TryGetValue(triggerName.Leaf, out var matchedTrigger)
            || !ReferenceEquals(matchedTrigger.Parent, parent))
        {
            throw SimulatedSqlException.InvalidObjectName(triggerName);
        }
        matchedTrigger.IsDisabled = disable;
        return true;
    }

    /// <summary>
    /// Parses the body of a database-scope DDL trigger. Cursor enters on
    /// the <c>DATABASE</c> keyword (already matched by the caller); on
    /// successful return the trigger is registered in
    /// <see cref="Database.DdlTriggers"/>. Grammar:
    /// <c>… ON DATABASE [WITH …] { FOR | AFTER } &lt;event_type [, …]&gt; AS &lt;body&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Event types parse as bare identifiers (e.g. <c>DDL_DATABASE_LEVEL_EVENTS</c>,
    /// <c>CREATE_TABLE</c>, <c>ALTER_PROCEDURE</c>). The simulator stores
    /// the list verbatim — the source casing survives into
    /// <c>sys.trigger_events</c>, and both that projection and the fire-time
    /// <see cref="DdlTrigger.Covers"/> match case-insensitively. A name
    /// SQL Server's event-type catalog doesn't carry is accepted and never
    /// matches anything.
    /// </remarks>
    private static bool ParseDdlTriggerBody(ParserContext context, MultiPartName triggerName, Schema triggerSchema, bool isAlter, bool createOrAlter)
    {
        // Cursor on DATABASE. Advance to the next significant token.
        context.MoveNextRequired();

        // Optional WITH option list (parse-and-ignore — mirrors the DML
        // trigger path; AW emits no WITH options on its DDL trigger).
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            while (context.Token is not (null or
                ReservedKeyword { Keyword: Keyword.For } or
                UnquotedString { ContextualKeyword: ContextualKeyword.After }))
            {
                context.MoveNextRequired();
            }
        }

        if (context.Token is not (ReservedKeyword { Keyword: Keyword.For } or
            UnquotedString { ContextualKeyword: ContextualKeyword.After }))
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextRequired();

        // Event-type list — bare identifiers, comma-separated. UnquotedString
        // (which carries identifiers like DDL_DATABASE_LEVEL_EVENTS) is a
        // Name subclass; the Name arm covers both quoted and unquoted forms.
        var eventTypes = new List<string>();
        while (true)
        {
            if (context.Token is not Name eventToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            eventTypes.Add(eventToken.Value);
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Same body-capture pattern as TryParseCreateTrigger above: consume
        // through end of batch, slice the raw text for sys.sql_modules.
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

        // DDL triggers live in their own per-database dict, but the NAME
        // collision check still applies against the per-schema shared
        // namespace (probe-confirmed: a DDL trigger named [foo] collides
        // with a DML trigger or any other schema object named [foo] in
        // the same schema). triggerSchema is the resolved owner schema
        // from the caller (default dbo for unqualified names).
        var existed = context.CurrentDatabase.DdlTriggers.TryGetValue(triggerName.Leaf, out var existing);
        if (!isAlter && !createOrAlter && (existed || triggerSchema.HasNameInSharedNamespace(triggerName.Leaf)))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(triggerName.Leaf);
        if (isAlter && !existed)
            throw SimulatedSqlException.InvalidObjectName(triggerName);

        var objectId = existed ? existing!.ObjectId : context.CurrentDatabase.AllocateObjectId();
        var trigger = new DdlTrigger(
            triggerName.Leaf,
            objectId,
            triggerSchema.SchemaId,
            eventTypes,
            bodyText,
            createDate: existed ? existing!.CreateDate : context.Batch.CurrentStatement.UtcNow,
            bodyLineOffset: CountNewlines(commandText, context.Batch.CurrentStatement.StartIndex, bodyStart))
        {
            DefinitionText = BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, bodyEnd, isAlter, createOrAlter),
        };
        if (existed)
            trigger.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        context.CurrentDatabase.DdlTriggers[triggerName.Leaf] = trigger;
        if (!existed)
            context.Batch.CurrentStatement.DdlTriggerCreatedThisStatement = objectId;
        RecordDdlEvent(
            context,
            existed ? "ALTER_TRIGGER" : "CREATE_TRIGGER",
            triggerSchema.Name,
            triggerName.Leaf,
            "TRIGGER");
        return true;
    }
}
