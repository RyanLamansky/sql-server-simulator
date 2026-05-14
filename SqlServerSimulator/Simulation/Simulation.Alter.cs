using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the two ALTER DATABASE forms the simulator currently models:
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c> (per-database
    /// compat) and
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// The simulator has a single database, so any database name (including
    /// <c>CURRENT</c>) is accepted and ignored.
    /// </summary>
    private static bool TryParseAlter(ParserContext context)
    {
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc }:
                // ALTER PROCEDURE is identical in shape to CREATE PROCEDURE —
                // same parameter grammar, same options, same body capture —
                // differing only in the existence-check direction (must exist
                // vs must not). Reuse the CREATE PROCEDURE parser with the
                // isAlter flag set.
                return TryParseCreateProcedure(context, isAlter: true, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Trigger }:
                // Same shape-sharing pattern as ALTER PROCEDURE — body /
                // actions replace in place, ObjectId is preserved.
                return TryParseCreateTrigger(context, isAlter: true, createOrAlter: false);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Sequence }:
                return TryParseAlterSequence(context);
            case ReservedKeyword { Keyword: Keyword.Schema }:
                return TryParseAlterSchemaTransfer(context);
            case ReservedKeyword { Keyword: Keyword.Table }:
                return TryParseAlterTable(context);
            case ReservedKeyword { Keyword: Keyword.Database }:
                break;
            default:
                return false;
        }

        // Cursor is on DATABASE; advance to the token after it (a db name, the
        // CURRENT keyword, or the SCOPED contextual keyword routing to the
        // database-scoped-configuration path).
        var afterDatabase = context.GetNextRequired();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Scoped })
            return TryParseAlterDatabaseScopedConfiguration(context);

        // Otherwise a database name (or CURRENT). The simulator has one
        // database; accept anything that looks like an identifier.
        return afterDatabase is Name or ReservedKeyword { Keyword: Keyword.Current }
            && TryParseAlterDatabaseSet(context);
    }

    private static bool TryParseAlterDatabaseSet(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        return context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Compatibility_Level } => TryParseAlterDatabaseSetCompatibilityLevel(context),
            UnquotedString { ContextualKeyword: ContextualKeyword.Allow_Snapshot_Isolation } => TryParseAlterDatabaseSetSnapshotFlag(context, isRcsi: false),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Committed_Snapshot } => TryParseAlterDatabaseSetSnapshotFlag(context, isRcsi: true),
            _ => false,
        };
    }

    private static bool TryParseAlterDatabaseSetCompatibilityLevel(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        var requested = numericValue.AsInt32;
        if (context.Batch.IsSkipping)
            return true;
        if (!Enum.IsDefined((CompatibilityLevel)requested))
            throw SimulatedSqlException.InvalidCompatibilityLevel();

        context.CurrentDatabase.CompatibilityLevel = (CompatibilityLevel)requested;
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER DATABASE name SET (ALLOW_SNAPSHOT_ISOLATION | READ_COMMITTED_SNAPSHOT) { ON | OFF }</c>.
    /// The probed real-server gates ALLOW_SNAPSHOT_ISOLATION ON behind a
    /// brief stabilization wait and READ_COMMITTED_SNAPSHOT ON behind a
    /// single-connection requirement; the simulator skips both — the flip
    /// takes effect immediately. <c>WITH (NO_WAIT | ROLLBACK IMMEDIATE | ROLLBACK AFTER n)</c>
    /// termination options are rejected by real SQL Server on versioning
    /// state changes (Msg 5083); the simulator falls through and raises
    /// <see cref="NotSupportedException"/> on the unexpected trailer.
    /// </summary>
    private static bool TryParseAlterDatabaseSetSnapshotFlag(ParserContext context, bool isRcsi)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var on } || on is not (Keyword.On or Keyword.Off))
            return false;
        if (context.Batch.IsSkipping)
            return true;
        var value = on == Keyword.On;
        if (isRcsi)
            context.CurrentDatabase.ReadCommittedSnapshot = value;
        else
            context.CurrentDatabase.AllowSnapshotIsolation = value;
        return true;
    }

    private static bool TryParseAlterDatabaseScopedConfiguration(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Configuration })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Verbose_Truncation_Warnings })
            return false;

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var on } || on is not (Keyword.On or Keyword.Off))
            return false;

        if (!context.Batch.IsSkipping)
            context.CurrentDatabase.VerboseTruncationWarnings = on == Keyword.On;
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SEQUENCE [schema.]name [RESTART [WITH n]] [INCREMENT BY n]
    /// [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE]
    /// [CACHE n | NO CACHE]</c>. Entered with <see cref="ParserContext.Token"/>
    /// on the <c>SEQUENCE</c> contextual keyword. <c>RESTART</c> resets
    /// <see cref="Sequence.CurrentValue"/> to the explicit value or to
    /// <see cref="Sequence.StartValue"/>, and clears
    /// <see cref="Sequence.IsExhausted"/>. Other options replace the
    /// matching field. Probe-confirmed: ALTER SEQUENCE accepts the same
    /// option subset as CREATE SEQUENCE.
    /// </summary>
    private static bool TryParseAlterSequence(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var sequenceName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
        {
            // Walk past any option tokens so the dispatch loop's lookahead
            // doesn't trip on them.
            while (context.MoveNext() && context.Token is not (Operator { Character: ';' } or ReservedKeyword))
            {
                // no-op
            }
            return true;
        }

        if (!context.Batch.TryResolveSequence(sequenceName, out var sequence))
            throw SimulatedSqlException.InvalidObjectName(sequenceName);
        // TryResolveSequence took Sch-S; upgrade to Sch-M before mutating
        // the sequence's option fields. Other connections reading the
        // sequence (NEXT VALUE FOR) will wait on the Sch-M acquire.
        context.Batch.AcquireStatementLock(sequence.SchemaLock, LockMode.SchemaModification);

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Restart }:
                    {
                        // RESTART [WITH n]: peek WITH; if present, read the
                        // value; otherwise reset to the original start value.
                        var afterRestart = context.SaveCheckpoint();
                        if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.With })
                        {
                            sequence.CurrentValue = ReadSignedIntegerLiteral(context);
                        }
                        else
                        {
                            context.RestoreCheckpoint(afterRestart);
                            sequence.CurrentValue = sequence.StartValue;
                        }
                        sequence.IsExhausted = false;
                        continue;
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Increment }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                        return false;
                    sequence.Increment = ReadSignedIntegerLiteral(context);
                    if (sequence.Increment == 0)
                        throw SimulatedSqlException.SequenceIncrementCannotBeZero(sequence.FullName);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                    sequence.MinValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                    sequence.MaxValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                    sequence.Cycle = true;
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.No }:
                    {
                        var afterNo = context.GetNextRequired();
                        switch (afterNo)
                        {
                            case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                                sequence.Cycle = false;
                                continue;
                            case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue or ContextualKeyword.MaxValue or ContextualKeyword.Cache }:
                                continue;
                            default:
                                return false;
                        }
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                    {
                        var afterCache = context.SaveCheckpoint();
                        if (!context.MoveNext() || context.Token is not (Numeric or Operator { Character: '-' or '+' }))
                        {
                            context.RestoreCheckpoint(afterCache);
                        }
                        else
                        {
                            context.RestoreCheckpoint(afterCache);
                            _ = ReadSignedIntegerLiteral(context);
                        }
                        continue;
                    }
                default:
                    return true;
            }
        }
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SCHEMA dest TRANSFER [ (OBJECT|TYPE)::] source.obj</c>.
    /// Entered with <see cref="ParserContext.Token"/> on the <c>SCHEMA</c>
    /// keyword. Routes the named object between schemas:
    /// <list type="bullet">
    /// <item><c>OBJECT</c> class (default if no prefix given): targets the
    /// shared-namespace dicts on <see cref="Schema"/> —
    /// <see cref="Schema.HeapTables"/>, <see cref="Schema.Views"/>,
    /// <see cref="Schema.Functions"/>, <see cref="Schema.Procedures"/>,
    /// <see cref="Schema.Sequences"/>. Triggers are not directly
    /// transferable — they move along with their parent table or view
    /// automatically (Msg 15347 if named directly).</item>
    /// <item><c>TYPE</c> class: targets <see cref="Schema.TableTypes"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed error paths (SQL Server 2025, 2026-05-13):
    /// </para>
    /// <list type="bullet">
    /// <item>Destination schema doesn't exist → <strong>Msg 15151</strong>
    /// alter-schema variant.</item>
    /// <item>Source object/type doesn't exist → <strong>Msg 15151</strong>
    /// find-object / find-type variant (leaf name only — qualifier not
    /// echoed).</item>
    /// <item>Source = destination schema and the object exists in source →
    /// silent no-op (probe-confirmed).</item>
    /// <item>Object with same leaf already exists in destination →
    /// <strong>Msg 15530</strong>.</item>
    /// <item>Source is a trigger → <strong>Msg 15347</strong> (triggers
    /// follow their parent's schema; can't be transferred directly).</item>
    /// </list>
    /// <para>
    /// When the transferred object is a heap table or view, any attached
    /// triggers automatically reseat into the destination schema's
    /// <see cref="Schema.Triggers"/> dict and their <see cref="Trigger.Schema"/>
    /// reference + <see cref="SchemaObject.SchemaId"/> update — mirrors
    /// real SQL Server's "triggers belong to their parent's schema" rule.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterSchemaTransfer(ParserContext context)
    {
        if (context.GetNextRequired() is not Name destSchemaToken)
            return false;
        var destSchemaName = destSchemaToken.Value;

        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Transfer })
            return false;

        // Optional class prefix: OBJECT:: or TYPE::. Both Object and Type are
        // contextual keywords, and the :: separator tokenizes as two adjacent
        // single-character ':' operators.
        var classIsType = false;
        var afterTransfer = context.SaveCheckpoint();
        if (context.MoveNext() && context.Token is UnquotedString { ContextualKeyword: var ck }
            && ck is ContextualKeyword.Object or ContextualKeyword.Type)
        {
            var first = context.GetNextRequired();
            var second = context.GetNextRequired();
            if (first is not Operator { Character: ':' } || second is not Operator { Character: ':' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            classIsType = ck == ContextualKeyword.Type;
            context.MoveNextRequired();
        }
        else
        {
            context.RestoreCheckpoint(afterTransfer);
            context.MoveNextRequired();
        }

        var sourceName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.CurrentDatabase.Schemas.TryGetValue(destSchemaName, out var destSchema))
            throw SimulatedSqlException.CannotAlterSchemaDoesNotExist(destSchemaName);

        if (!context.Batch.TryResolveSchema(sourceName, out var sourceSchema))
        {
            throw classIsType
                ? SimulatedSqlException.CannotFindType(sourceName.Leaf)
                : SimulatedSqlException.CannotFindObject(sourceName.Leaf);
        }

        if (classIsType)
            TransferTableType(sourceSchema, destSchema, sourceName.Leaf, context.Batch);
        else
            TransferObject(sourceSchema, destSchema, sourceName.Leaf, context.Batch);
        return true;
    }

    /// <summary>
    /// Moves a user-defined table type between schemas. Lookup miss →
    /// Msg 15151 find-type; collision in destination → Msg 15530. Same-schema
    /// transfer is a no-op (matches probe). Tests for fidelity: real SQL
    /// Server also moves the type's underlying type-table id via
    /// <see cref="SchemaObject.SchemaId"/>; <see cref="TableType.Schema"/>
    /// reference updates in lockstep.
    /// </summary>
    private static void TransferTableType(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        if (!sourceSchema.TableTypes.TryGetValue(leafName, out var tableType))
            throw SimulatedSqlException.CannotFindType(leafName);
        if (ReferenceEquals(sourceSchema, destSchema))
            return;
        if (destSchema.TableTypes.ContainsKey(leafName))
            throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
        batch.AcquireStatementLock(tableType.SchemaLock, LockMode.SchemaModification);
        _ = sourceSchema.TableTypes.TryRemove(leafName, out _);
        destSchema.TableTypes[leafName] = tableType;
        tableType.Schema = destSchema;
        tableType.SchemaId = destSchema.SchemaId;
    }

    /// <summary>
    /// Moves an object between schemas. Walks the source schema's shared-
    /// namespace dicts (heap tables / views / functions / procedures /
    /// sequences) — first hit by leaf name wins. Triggers explicitly raise
    /// Msg 15347 since they're owned by their parent (the trigger's schema
    /// follows its parent's schema automatically). After the move,
    /// HeapTable / View transfers reseat any attached triggers — they belong
    /// to the destination schema after the transfer.
    /// </summary>
    private static void TransferObject(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        // Triggers can't be transferred directly — Msg 15347 owns this case.
        if (sourceSchema.Triggers.TryGetValue(leafName, out _))
            throw SimulatedSqlException.CannotTransferObjectOwnedByParent();

        var sameSchema = ReferenceEquals(sourceSchema, destSchema);

        if (sourceSchema.HeapTables.TryGetValue(leafName, out var heap))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(heap.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.HeapTables.TryRemove(leafName, out _);
            destSchema.HeapTables[leafName] = heap;
            heap.SchemaId = destSchema.SchemaId;
            ReseatAttachedTriggers(sourceSchema, destSchema, heap);
            return;
        }
        if (sourceSchema.Views.TryGetValue(leafName, out var view))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(view.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Views.TryRemove(leafName, out _);
            destSchema.Views[leafName] = view;
            view.Schema = destSchema;
            view.SchemaId = destSchema.SchemaId;
            ReseatAttachedTriggers(sourceSchema, destSchema, view);
            return;
        }
        if (sourceSchema.Functions.TryGetValue(leafName, out var fn))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(fn.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Functions.TryRemove(leafName, out _);
            destSchema.Functions[leafName] = fn;
            fn.Schema = destSchema;
            fn.SchemaId = destSchema.SchemaId;
            return;
        }
        if (sourceSchema.Procedures.TryGetValue(leafName, out var proc))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(proc.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Procedures.TryRemove(leafName, out _);
            destSchema.Procedures[leafName] = proc;
            proc.Schema = destSchema;
            proc.SchemaId = destSchema.SchemaId;
            return;
        }
        if (sourceSchema.Sequences.TryGetValue(leafName, out var seq))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(seq.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Sequences.TryRemove(leafName, out _);
            destSchema.Sequences[leafName] = seq;
            seq.Schema = destSchema;
            seq.SchemaId = destSchema.SchemaId;
            return;
        }

        throw SimulatedSqlException.CannotFindObject(leafName);
    }

    /// <summary>
    /// Parses the modeled <c>ALTER TABLE</c> shapes: <c>SET (SYSTEM_VERSIONING
    /// = OFF)</c>, <c>[WITH CHECK | WITH NOCHECK] ADD [CONSTRAINT name]
    /// (PRIMARY KEY | UNIQUE | FOREIGN KEY | CHECK | DEFAULT) …</c>, and
    /// <c>DROP CONSTRAINT [IF EXISTS] name [, …]</c>. Every other shape (ADD /
    /// DROP COLUMN, ALTER COLUMN, REBUILD, SET other options, ENABLE /
    /// DISABLE, etc.) raises <see cref="NotSupportedException"/> at the
    /// post-name dispatch point. Entered with <see cref="ParserContext.Token"/>
    /// on the <c>TABLE</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed error paths (SQL Server 2025, 2026-05-13):
    /// </para>
    /// <list type="bullet">
    /// <item>Target name doesn't resolve → <strong>Msg 4902</strong>
    /// (alter-table-specific table-not-found variant; distinct from Msg 208's
    /// generic name-resolution wording).</item>
    /// <item>SET (SYSTEM_VERSIONING = OFF) on a plain regular table or
    /// history sibling → <strong>Msg 13591</strong>.</item>
    /// <item>Unmodeled ALTER TABLE shapes → <see cref="NotSupportedException"/>.</item>
    /// </list>
    /// <para>
    /// ADD / DROP CONSTRAINT paths are documented on
    /// <see cref="TryParseAlterTableAddConstraint"/> and
    /// <see cref="TryParseAlterTableDropConstraint"/>.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterTable(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        // Sch-M for the ALTER's lifetime — acquired here at the dispatcher
        // entry so every sub-parser (ADD / DROP / ALTER COLUMN / ADD CONSTRAINT
        // / DROP CONSTRAINT / CHECK / NOCHECK / SET SYSTEM_VERSIONING) runs
        // under exclusive schema modification. Sub-parsers still call
        // TryResolveTable themselves to surface their own context-specific
        // missing-table error (Msg 4902 / 4904 / etc.); the additional Sch-S
        // those acquires take is harmless under same-owner Sch-M reentrance.
        // Skip the early acquire when the table doesn't exist — the sub-
        // parser's TryResolveTable then raises the right error code without
        // having acquired anything.
        if (!context.Batch.IsSkipping && context.Batch.TryResolveTable(tableName, out var alterTarget))
            context.Batch.AcquireStatementLock(alterTarget.SchemaLock, LockMode.SchemaModification);

        // Cursor is on the last name segment; advance to the post-name token.
        context.MoveNextRequired();

        // Optional WITH CHECK | WITH NOCHECK preceding ADD or CHECK / NOCHECK
        // CONSTRAINT. Default differs by action: ADD defaults to validate
        // (= WITH CHECK), CHECK CONSTRAINT defaults to skip-validate (= WITH
        // NOCHECK). Track tri-state so each branch can apply its own default.
        bool? withCheckExplicit = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            withCheckExplicit = context.GetNextRequired() switch
            {
                ReservedKeyword { Keyword: Keyword.Check } => true,
                ReservedKeyword { Keyword: Keyword.NoCheck } => false,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextRequired();
        }

        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Set }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableSetSystemVersioning(context, tableName);
            case ReservedKeyword { Keyword: Keyword.Add }:
                // ADD defaults to validate; only explicit WITH NOCHECK skips.
                return TryParseAlterTableAddConstraint(context, tableName, withNoCheck: withCheckExplicit == false);
            case ReservedKeyword { Keyword: Keyword.Drop }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableDropConstraint(context, tableName);
            case ReservedKeyword { Keyword: Keyword.Check }:
                // CHECK CONSTRAINT — re-enable enforcement on existing
                // constraint(s). Default skip-validate; explicit WITH CHECK
                // revalidates and clears IsNotTrusted on success.
                return TryParseAlterTableTrustToggle(context, tableName, disable: false, revalidate: withCheckExplicit == true);
            case ReservedKeyword { Keyword: Keyword.NoCheck }:
                // NOCHECK CONSTRAINT — disable enforcement. WITH-prefix is
                // semantically irrelevant (NOCHECK always implies "don't
                // validate"); probe shows real SQL Server accepts but ignores
                // the prefix here.
                return TryParseAlterTableTrustToggle(context, tableName, disable: true, revalidate: false);
            case ReservedKeyword { Keyword: Keyword.Alter }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableAlterColumn(context, tableName);
            default:
                throw new NotSupportedException("ALTER TABLE supports only SET (SYSTEM_VERSIONING = OFF), ADD / DROP / ALTER COLUMN, ADD / DROP CONSTRAINT, and CHECK / NOCHECK CONSTRAINT shapes.");
        }
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … SET (SYSTEM_VERSIONING = OFF)</c>. Cursor is
    /// on the <c>SET</c> keyword on entry. Probe-confirmed flow: target table
    /// must resolve (Msg 4902 otherwise), must be system-versioned (Msg 13591
    /// otherwise); the parent's link to its history sibling clears and the
    /// sibling's history-role flag flips. Period / GENERATED-ALWAYS column
    /// metadata is preserved.
    /// </summary>
    private static bool TryParseAlterTableSetSystemVersioning(ParserContext context, MultiPartName tableName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Versioning })
            throw new NotSupportedException("Only ALTER TABLE … SET (SYSTEM_VERSIONING = OFF) is supported.");

        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Off })
            throw new NotSupportedException("Only ALTER TABLE … SET (SYSTEM_VERSIONING = OFF) is supported (the = ON form requires the parent column-list grammar which isn't modeled).");

        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        if (table.SystemVersioning is null)
            throw SimulatedSqlException.SystemVersioningNotOn(QualifyTableName(table, context.CurrentDatabase));

        var historyTable = table.SystemVersioning;
        table.SystemVersioning = null;
        historyTable.IsHistoryTable = false;
        return true;
    }

    /// <summary>
    /// Moves every trigger whose <see cref="Trigger.Parent"/> matches
    /// <paramref name="movedParent"/> from <paramref name="sourceSchema"/>'s
    /// <see cref="Schema.Triggers"/> dict into <paramref name="destSchema"/>'s
    /// — mirrors SQL Server's "trigger schema follows parent" rule.
    /// Pre-existing destination-schema triggers with the same leaf are
    /// impossible in practice (a trigger's name shares the shared namespace
    /// via <see cref="Schema.HasNameInSharedNamespace"/>, which the upstream
    /// collision check has already rejected via Msg 15530 before this point).
    /// </summary>
    private static void ReseatAttachedTriggers(Schema sourceSchema, Schema destSchema, SchemaObject movedParent)
    {
        if (ReferenceEquals(sourceSchema, destSchema))
            return;
        string[]? names = null;
        foreach (var kv in sourceSchema.Triggers)
        {
            if (ReferenceEquals(kv.Value.Parent, movedParent))
            {
                names ??= [];
                Array.Resize(ref names, names.Length + 1);
                names[^1] = kv.Key;
            }
        }
        if (names is null) return;
        foreach (var n in names)
        {
            if (!sourceSchema.Triggers.TryRemove(n, out var trigger))
                continue;
            destSchema.Triggers[n] = trigger;
            trigger.Schema = destSchema;
            trigger.SchemaId = destSchema.SchemaId;
        }
    }
}
