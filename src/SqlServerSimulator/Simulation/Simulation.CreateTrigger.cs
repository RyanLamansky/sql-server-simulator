using SqlServerSimulator.Clr;
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
        context.Batch.ErrorProcedureName = triggerName.Leaf;
        RejectQualifiedModuleName(triggerName, "TRIGGER");
        if (!context.Batch.TryResolveSchema(triggerName, out var triggerSchema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(triggerName.ImmediateQualifier ?? Database.DefaultSchemaName);
        triggerSchema.Database.RejectWriteWhenReadOnly();

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // Branch on the parent-scope token: ON DATABASE → database-scope
        // DDL trigger (see DdlTrigger.cs and Simulation.InvokeDdlTrigger.cs); a
        // Name → DML trigger attached to a heap-table or view parent.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Database })
        {
            return ParseDdlTriggerBody(context, triggerName, triggerSchema, isAlter, createOrAlter, serverScope: false);
        }
        if (IsAllServer(context))
        {
            return ParseDdlTriggerBody(context, triggerName, triggerSchema, isAlter, createOrAlter, serverScope: true);
        }

        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var parentName = BatchContext.ParseObjectName(context);

        context.MoveNextRequired();

        // Optional WITH option list, which precedes the timing in real SQL
        // Server's grammar (ON table [WITH options] { FOR | AFTER | INSTEAD OF }).
        // EXECUTE AS is captured for the per-fire frame push.
        var options = ParseModuleOptions(context, ModuleOptionHost.Trigger, triggerName.Leaf);
        var executeAsClause = options.ExecuteAs;

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

        // NOT FOR REPLICATION before AS is also valid, and only recorded.
        var notForReplication = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
        {
            notForReplication = true;
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
        var (bodyStart, bodyText, externalName) = ReadTriggerBody(context, commandText);

        if (context.Batch.IsSkipping)
            return true;

        if (externalName is not null && options.Encryption)
            throw SimulatedSqlException.ClrTriggerEncryption();

        // Resolve the parent. INSTEAD OF accepts a heap table or a view;
        // AFTER accepts a heap table only (Msg 8197 on view target,
        // probe-confirmed). Table variables / temp tables aren't valid
        // parents in either case.
        SchemaObject parent;
        string parentKind;
        if (context.Batch.TryResolveView(parentName, out var parentView))
        {
            if (timing != TriggerTiming.InsteadOf)
                throw SimulatedSqlException.ObjectDoesNotExistForTrigger(parentName.ToString(), triggerName.Leaf);
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
            throw SimulatedSqlException.ObjectDoesNotExistForTrigger(parentName.ToString(), triggerName.Leaf);
        }

        // Bind the body against empty INSERTED / DELETED pseudo-tables shaped
        // like the parent, before any of the gates below and before the schema
        // dict is touched — probe-confirmed that real reports a body error
        // ahead of Msg 2714 / Msg 208 but behind the Msg 8197 parent
        // resolution above. The stand-in trigger exists only to carry the
        // parent into the frame (which is what `UPDATE(col)` resolves against)
        // and never reaches the schema, so it takes no object id.
        var bodyLineOffset = CountNewlines(commandText, 0, bodyStart);
        var clrEntry = externalName is { } dmlExternalName ? BindClrTrigger(context, dmlExternalName) : null;
        if (clrEntry is null)
        {
            var pseudoColumns = parent is View bindView ? bindView.OutputColumns : ((HeapTable)parent).Columns;
            var bindTrigger = new Trigger(
                triggerSchema, triggerName.Leaf, objectId: 0, parent, actions, timing, bodyText,
                createDate: context.Batch.CurrentStatement.UtcNow);
            context.Simulation.BindTriggerBodyAtCreate(
                context,
                triggerName.Leaf,
                new TriggerFrame(
                    bindTrigger,
                    MaterializePseudoTable(pseudoColumns, "inserted", [], context.Batch),
                    MaterializePseudoTable(pseudoColumns, "deleted", [], context.Batch),
                    columnsUpdatedMask: []),
                bodyText,
                bodyLineOffset);
        }

        // At most one INSTEAD OF trigger per action per target (Msg 2111).
        // ALTER / CREATE OR ALTER replacing an existing trigger by the
        // same name is permitted; collision is only with a *different*
        // trigger covering an overlapping action.
        // An INSTEAD OF trigger and a CASCADE on the same table and the same
        // verb are mutually exclusive: the cascade writes the child rows
        // itself, which is exactly what the trigger exists to intercept.
        // The conflict is action-matched and CASCADE-only — an INSTEAD OF
        // DELETE coexists with ON DELETE SET NULL / SET DEFAULT and with
        // ON UPDATE CASCADE, and INSTEAD OF INSERT never conflicts
        // (probe-confirmed across the matrix; Insite.Commerce's
        // `Brand_Instead_Of_Delete` beside `FK_Brand_Vendor ON DELETE SET NULL`
        // is the shipping shape that proves the SET NULL half).
        if (timing == TriggerTiming.InsteadOf
            && parent is HeapTable triggerTarget
            && HasConflictingCascade(triggerTarget, actions))
        {
            throw SimulatedSqlException.InsteadOfTriggerOnCascadingForeignKey(
                triggerName.Leaf, SchemaQualifyTableName(triggerTarget, context.CurrentDatabase));
        }

        if (timing == TriggerTiming.InsteadOf)
        {
            foreach (var (_, schema) in context.CurrentDatabase.Schemas)
            {
                foreach (var (_, t) in schema.Triggers)
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
        // A CLR trigger reports the clash at state 5, a T-SQL one at state 2
        // (probed 2026-09-28 against SQL Server 2025).
        if (!isAlter && !createOrAlter && triggerSchema.HasNameInSharedNamespace(triggerName.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(triggerName.Leaf, state: clrEntry is null ? (byte)2 : (byte)5);
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
        RejectClrTriggerKindChange(existed ? existing : null, clrEntry, triggerName);
        // A DML trigger is not a grantable securable of its own — real gates
        // both verbs on ALTER of the parent table / view (probe-confirmed).
        // Creating reports Msg 2104 naming the trigger as written; replacing an
        // existing one reports the Msg 3701 state 20 every module ALTER uses.
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(parent), parent.ObjectId, parent.SchemaId))
        {
            throw existed
                ? SimulatedSqlException.AlterObjectPermissionDenied("trigger", triggerName.Leaf)
                : SimulatedSqlException.CreateTriggerPermissionDenied(triggerName.ToString());
        }
        // Sch-M on the existing trigger instance's SchemaLock before
        // replacement — same pattern as ALTER PROCEDURE.
        if (existed)
            context.Batch.AcquireStatementLock(existing!.SchemaLock, LockMode.SchemaModification);

        var objectId = existed ? existing!.ObjectId : context.CurrentDatabase.AllocateObjectId();
        var trigger = new Trigger(
            triggerSchema,
            triggerName.Leaf,
            objectId,
            parent,
            actions,
            timing,
            bodyText,
            createDate: existed ? existing!.CreateDate : context.Batch.CurrentStatement.UtcNow,
            bodyLineOffset: bodyLineOffset)
        {
            DefinitionText = options.Encryption || clrEntry is not null ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            ClrEntry = clrEntry,
            ExecuteAsClause = executeAsClause,
            ExecuteAsPrincipalId = ResolveExecuteAsPrincipalId(context, executeAsClause),
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            NotForReplication = notForReplication,
        };
        if (existed)
            trigger.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        triggerSchema.Triggers[triggerName.Leaf] = trigger;
        RecordSlotUndo(context, triggerSchema.Triggers, triggerName.Leaf, existed ? existing : null);
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
        // one, and ON ALL SERVER a server-scope trigger; the ALL form covers
        // every trigger of that scope. A name the scope doesn't hold is
        // Msg 1088 state 119 (probed 2026-09-28 against SQL Server 2025).
        var serverScope = IsAllServer(context);
        if (serverScope || context.Token is ReservedKeyword { Keyword: Keyword.Database })
        {
            if (!allTriggers && triggerName.Count > 1)
                throw SimulatedSqlException.SchemaPrefixOnScopedTrigger();
            if (context.Batch.IsSkipping)
                return true;
            var scoped = serverScope ? context.Simulation.ServerTriggers.All : context.CurrentDatabase.DdlTriggers.EnumerateValues();
            if (allTriggers)
            {
                foreach (var ddlTrigger in scoped)
                    ddlTrigger.IsDisabled = disable;
                return true;
            }
            var matchedDdlTrigger = serverScope
                ? context.Simulation.ServerTriggers.TryGetValue(triggerName.Leaf, out var serverTrigger) ? serverTrigger : null
                : context.CurrentDatabase.DdlTriggers.TryGetValue(triggerName.Leaf, out var databaseTrigger) ? databaseTrigger : null;
            (matchedDdlTrigger ?? throw SimulatedSqlException.CannotFindScopedTrigger(triggerName.Leaf)).IsDisabled = disable;
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
            foreach (var (_, schema) in context.CurrentDatabase.Schemas)
            {
                foreach (var (_, trigger) in schema.Triggers)
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
    /// Whether the cursor sits on <c>ALL SERVER</c>, the server-scope parent of
    /// a <c>CREATE</c> / <c>DROP</c> / <c>ENABLE</c> / <c>DISABLE TRIGGER</c>.
    /// On success the cursor is left on <c>SERVER</c>; otherwise it hasn't moved.
    /// </summary>
    private static bool IsAllServer(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.All })
            return false;
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is Name { Value: var word } && word.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
            return true;
        context.RestoreCheckpoint(checkpoint);
        return false;
    }

    /// <summary>
    /// Parses the body of a database-scope DDL trigger (<c>ON DATABASE</c>) or a
    /// server-scope trigger (<c>ON ALL SERVER</c>). Cursor enters on the
    /// <c>DATABASE</c> / <c>SERVER</c> keyword (already matched by the caller);
    /// on successful return the trigger is registered in
    /// <see cref="Database.DdlTriggers"/> or <see cref="ServerTriggers"/>.
    /// Grammar: <c>… ON { DATABASE | ALL SERVER } [WITH …] { FOR | AFTER }
    /// &lt;event_type [, …]&gt; AS &lt;body&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Event types parse as bare identifiers (e.g. <c>DDL_DATABASE_LEVEL_EVENTS</c>,
    /// <c>CREATE_TABLE</c>, <c>LOGON</c>). The simulator stores the list
    /// verbatim — the source casing survives into <c>sys.trigger_events</c>,
    /// and both that projection and the fire-time <see cref="DdlTrigger.Covers"/>
    /// match case-insensitively. A name the event-type catalog doesn't carry
    /// is Msg 1084, and one the scope can't raise — a DML action, <c>LOGON</c>
    /// or a server-level event at database scope — Msg 1098 (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </remarks>
    private static bool ParseDdlTriggerBody(ParserContext context, MultiPartName triggerName, Schema triggerSchema, bool isAlter, bool createOrAlter, bool serverScope)
    {
        // Cursor on DATABASE / SERVER. Advance to the next significant token.
        context.MoveNextRequired();

        // A database- or server-level trigger belongs to no schema.
        if (triggerName.Count > 1)
            throw SimulatedSqlException.SchemaPrefixOnScopedTrigger();

        // Optional WITH option list, judged as the DML trigger's is. A
        // database-scope DDL trigger runs as its caller whatever EXECUTE AS
        // says; a server-scope one runs as the login it names.
        var options = ParseModuleOptions(context, ModuleOptionHost.Trigger, triggerName.Leaf);
        if (options.ExecuteAs is { } executeAs && executeAs.Equals("OWNER", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.ExecuteAsOwnerOnScopedTrigger();

        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.For }:
            case UnquotedString { ContextualKeyword: ContextualKeyword.After }:
                context.MoveNextRequired();
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Instead }:
                // INSTEAD OF parses, and the event after it is the syntax error
                // (probed 2026-09-28 against SQL Server 2025).
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Of })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                throw SimulatedSqlException.SyntaxErrorNear(context);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // Event-type list — bare identifiers, comma-separated. UnquotedString
        // (which carries identifiers like DDL_DATABASE_LEVEL_EVENTS) is a
        // Name subclass; the Name arm covers both quoted and unquoted forms.
        // A DML action is a keyword rather than a name, and names an event no
        // DDL trigger can take.
        var eventTypes = new List<string>();
        var invalidForScope = false;
        while (true)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Insert or Keyword.Update or Keyword.Delete }:
                    invalidForScope = true;
                    break;
                case Name eventToken:
                    if (eventToken.Value.Equals("LOGON", StringComparison.OrdinalIgnoreCase))
                        invalidForScope |= !serverScope;
                    else if (!TriggerEventTypes.TryResolve(eventToken.Value, out var entry))
                        throw SimulatedSqlException.InvalidEventType(eventToken.Value);
                    else
                        invalidForScope |= !serverScope && !TriggerEventTypes.IsDatabaseLevel(entry);
                    eventTypes.Add(eventToken.Value);
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        if (invalidForScope)
            throw SimulatedSqlException.EventTypeInvalidOnTarget();

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Same body-capture pattern as TryParseCreateTrigger above: consume
        // through end of batch, slice the raw text for sys.sql_modules.
        var commandText = context.Command.CommandText;
        context.MoveNextOptional();
        var (bodyStart, bodyText, externalName) = ReadTriggerBody(context, commandText);

        if (context.Batch.IsSkipping)
            return true;

        if (externalName is not null && serverScope)
            throw new NotSupportedException("CLR server-scope triggers (CREATE TRIGGER … ON ALL SERVER … AS EXTERNAL NAME) are not modeled.");
        if (externalName is not null && options.Encryption)
            throw SimulatedSqlException.ClrTriggerEncryption();

        var simulation = context.Simulation;
        // A server-scope trigger's body runs in master, so that is where it
        // binds and where its object id comes from.
        var homeDatabase = serverScope ? simulation.Databases[MasterDatabaseName] : context.CurrentDatabase;

        // Bind the body ahead of the collision gates, same ordering the DML
        // form uses. A DDL body has no INSERTED / DELETED, so the frame only
        // carries the stand-in trigger and an empty EVENTDATA document; the
        // stand-in never reaches the database and takes no object id.
        var bodyLineOffset = CountNewlines(commandText, 0, bodyStart);
        var clrEntry = externalName is { } ddlExternalName ? BindClrTrigger(context, ddlExternalName) : null;
        if (clrEntry is null)
        {
            var connection = context.Connection;
            var savedDatabase = connection.CurrentDatabase;
            connection.CurrentDatabase = homeDatabase;
            try
            {
                simulation.BindTriggerBodyAtCreate(
                    context,
                    triggerName.Leaf,
                    new TriggerFrame(
                        new DdlTrigger(triggerName.Leaf, objectId: 0, triggerSchema.SchemaId, eventTypes, bodyText,
                            createDate: context.Batch.CurrentStatement.UtcNow, bodyLineOffset, serverScope),
                        eventData: ""),
                    bodyText,
                    bodyLineOffset);
            }
            finally
            {
                connection.CurrentDatabase = savedDatabase;
            }
        }

        DdlTrigger? existing;
        bool existed;
        if (serverScope)
        {
            existed = simulation.ServerTriggers.TryGetValue(triggerName.Leaf, out var existingServer);
            existing = existed ? existingServer : null;
            if (!isAlter && !createOrAlter && existed)
                throw SimulatedSqlException.ThereIsAlreadyAnObject(triggerName.Leaf, state: 2);
            if (isAlter && !existed)
                throw SimulatedSqlException.InvalidObjectName(triggerName, state: 6);
        }
        else
        {
            // DDL triggers live in a namespace of their own: a schema object
            // or DML trigger of the same name is no conflict in either
            // direction, and neither is a server-scope trigger (probed
            // 2026-09-28 against SQL Server 2025).
            existed = context.CurrentDatabase.DdlTriggers.TryGetValue(triggerName.Leaf, out var existingDatabase);
            existing = existed ? existingDatabase : null;
            if (!isAlter && !createOrAlter && existed)
                throw SimulatedSqlException.ThereIsAlreadyAnObject(triggerName.Leaf, state: 2);
            if (isAlter && !existed)
                throw SimulatedSqlException.InvalidObjectName(triggerName, state: 6);
        }
        RejectClrTriggerKindChange(existing, clrEntry, triggerName);
        // A database-scope DDL trigger is gated on ALTER ANY DATABASE DDL
        // TRIGGER instead of a parent object's ALTER, and a server-scope one on
        // CONTROL SERVER (probed 2026-09-28 against SQL Server 2025: a db_owner
        // is refused with Msg 2104; 2026-09-29: a CONTROL SERVER grantee
        // creates and drops one).
        var permitted = serverScope
            ? simulation.SessionHoldsServerPermission(context.Connection, Permission.ControlServer)
            : PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnyDatabaseDdlTrigger);
        if (!permitted)
        {
            throw existed
                ? SimulatedSqlException.AlterObjectPermissionDenied("trigger", triggerName.Leaf)
                : SimulatedSqlException.CreateTriggerPermissionDenied(triggerName.ToString());
        }

        // A server-scope trigger's EXECUTE AS names a login: SELF is the one
        // running the CREATE, and execute_as_principal_id its server principal
        // id (probed 2026-09-28 against SQL Server 2025 — 1 for both 'sa' and
        // SELF under sa).
        string? executeAsLogin = null;
        int? executeAsPrincipalId = null;
        if (serverScope && options.ExecuteAs is { } serverExecuteAs && !serverExecuteAs.Equals("CALLER", StringComparison.OrdinalIgnoreCase))
        {
            executeAsLogin = serverExecuteAs.Equals("SELF", StringComparison.OrdinalIgnoreCase)
                ? context.Connection.Security.Effective.LoginName
                : serverExecuteAs;
            if (!LoginExists(simulation, executeAsLogin) || !simulation.TryResolveServerPrincipalId(executeAsLogin, out var loginPrincipalId))
                throw SimulatedSqlException.CannotExecuteAsLogin(serverExecuteAs);
            executeAsPrincipalId = loginPrincipalId;
        }

        var objectId = existed ? existing!.ObjectId : homeDatabase.AllocateObjectId();
        var trigger = new DdlTrigger(
            triggerName.Leaf,
            objectId,
            triggerSchema.SchemaId,
            eventTypes,
            bodyText,
            createDate: existed ? existing!.CreateDate : context.Batch.CurrentStatement.UtcNow,
            bodyLineOffset: bodyLineOffset,
            isServerScoped: serverScope)
        {
            DefinitionText = options.Encryption || clrEntry is not null ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            ClrEntry = clrEntry,
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            ExecuteAsLoginName = executeAsLogin,
            ExecuteAsPrincipalId = executeAsPrincipalId,
        };
        if (existed)
            trigger.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        if (serverScope)
        {
            var registry = simulation.ServerTriggers;
            var previous = registry.Set(trigger);
            RecordDdlUndo(context, () =>
            {
                if (previous is null)
                    _ = registry.Remove(trigger.Name, out _);
                else
                    _ = registry.Set(previous);
            });
            return true;
        }
        context.CurrentDatabase.DdlTriggers[triggerName.Leaf] = trigger;
        RecordSlotUndo(context, context.CurrentDatabase.DdlTriggers, triggerName.Leaf, existing);
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
    /// <summary>
    /// Whether an FK on <paramref name="table"/> cascades on a verb
    /// <paramref name="actions"/> also covers — the only pairing real refuses.
    /// </summary>
    internal static bool HasConflictingCascade(HeapTable table, TriggerActions actions)
    {
        foreach (var fk in table.OutgoingForeignKeys)
        {
            if ((actions & TriggerActions.Delete) != 0 && fk.DeleteAction == ReferentialAction.Cascade)
                return true;
            if ((actions & TriggerActions.Update) != 0 && fk.UpdateAction == ReferentialAction.Cascade)
                return true;
        }

        return false;
    }


    /// <summary>
    /// Reads a trigger's body after <c>AS</c> to the end of the batch: the
    /// T-SQL text, or for a CLR trigger the <c>EXTERNAL NAME
    /// assembly.class.method</c> triple and no text. Cursor on entry: the first
    /// token after <c>AS</c>.
    /// </summary>
    private static (int BodyStart, string BodyText, MultiPartName? ExternalName) ReadTriggerBody(ParserContext context, string commandText)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.External })
        {
            var externalName = ParseExternalName(context, 3);
            if (context.Token is not null)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            return (commandText.Length, "", externalName);
        }

        var bodyStart = context.Token?.StartIndex ?? commandText.Length;
        var bodyEnd = commandText.Length;
        while (context.Token is not null)
        {
            bodyEnd = context.Token.EndIndex;
            context.MoveNextOptional();
        }
        return (bodyStart, commandText[bodyStart..bodyEnd], null);
    }

    /// <summary>
    /// Binds a CLR trigger's method: the assembly, class and method lookups
    /// (Msg 6528 / 6505 / 6506), then a method that returns a value (Msg 6500)
    /// or takes parameters (Msg 6531) — probed 2026-09-28 against SQL Server
    /// 2025.
    /// </summary>
    private static ClrEntryPoint BindClrTrigger(ParserContext context, MultiPartName externalName)
    {
        var (assembly, type) = ResolveClrClass(context, externalName[0], externalName[1]);
        var method = ResolveClrMethod(type, externalName[2], externalName[1], assembly.Name);
        if (method.ReturnType != typeof(void))
            throw SimulatedSqlException.ClrTriggerReturnType(externalName[2], externalName[1], assembly.Name, ClrTypeMarshaller.DisplayName(method.ReturnType));
        if (method.GetParameters().Length != 0)
            throw SimulatedSqlException.ClrTriggerTakesParameters(externalName[2], externalName[1], assembly.Name);
        return new ClrEntryPoint(assembly, externalName[1], externalName[2], type, method);
    }

    /// <summary>
    /// <c>ALTER</c> can't turn a T-SQL trigger into a CLR one (Msg 6530) or the
    /// reverse (Msg 2010).
    /// </summary>
    private static void RejectClrTriggerKindChange(SchemaObject? existing, ClrEntryPoint? replacement, MultiPartName triggerName)
    {
        var (wasTrigger, existingEntry) = existing switch
        {
            Trigger dml => (true, dml.ClrEntry),
            DdlTrigger ddl => (true, ddl.ClrEntry),
            _ => (false, null),
        };
        if (!wasTrigger || (existingEntry is null) == (replacement is null))
            return;
        throw replacement is not null
            ? SimulatedSqlException.ClrAlterIncompatible(triggerName.Leaf)
            : SimulatedSqlException.CannotAlterIncompatibleObjectType(triggerName);
    }
}
