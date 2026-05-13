using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

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
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Compatibility_Level })
            return false;

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
            TransferTableType(sourceSchema, destSchema, sourceName.Leaf);
        else
            TransferObject(sourceSchema, destSchema, sourceName.Leaf);
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
    private static void TransferTableType(Schema sourceSchema, Schema destSchema, string leafName)
    {
        if (!sourceSchema.TableTypes.TryGetValue(leafName, out var tableType))
            throw SimulatedSqlException.CannotFindType(leafName);
        if (ReferenceEquals(sourceSchema, destSchema))
            return;
        if (destSchema.TableTypes.ContainsKey(leafName))
            throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
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
    private static void TransferObject(Schema sourceSchema, Schema destSchema, string leafName)
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
            _ = sourceSchema.Sequences.TryRemove(leafName, out _);
            destSchema.Sequences[leafName] = seq;
            seq.Schema = destSchema;
            seq.SchemaId = destSchema.SchemaId;
            return;
        }

        throw SimulatedSqlException.CannotFindObject(leafName);
    }

    /// <summary>
    /// Parses <c>ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = OFF)</c>.
    /// The only <c>ALTER TABLE</c> shape modeled — every other shape (ADD /
    /// DROP COLUMN, ADD / DROP CONSTRAINT, SET other options, ENABLE /
    /// DISABLE, REBUILD, etc.) raises <see cref="NotSupportedException"/> at
    /// the post-name dispatch point. Entered with <see cref="ParserContext.Token"/>
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
    /// <item>Target exists but isn't system-versioned (plain regular table
    /// or the history sibling itself) → <strong>Msg 13591</strong>.</item>
    /// <item>Grammar variants other than <c>SET (SYSTEM_VERSIONING = OFF)</c>
    /// → <see cref="NotSupportedException"/>. Real SQL Server reaches a
    /// range of error codes (Msg 102 syntax for typos, Msg 13510 for the
    /// <c>= ON</c> path without a period definition); the simulator collapses
    /// these to one not-supported signal since the broader ALTER TABLE
    /// grammar isn't modeled.</item>
    /// </list>
    /// <para>
    /// Successful flip clears <see cref="HeapTable.SystemVersioning"/> on the
    /// parent (the parent's link to its history sibling) and
    /// <see cref="HeapTable.IsHistoryTable"/> on the sibling — both tables
    /// revert to plain regular status. Period and GENERATED ALWAYS column
    /// metadata stays intact (probe-confirmed: <c>sys.columns.generated_always_type_desc</c>
    /// and <c>is_hidden</c> remain after SET OFF). DROP TABLE on either now
    /// succeeds.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterTable(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        // Cursor is on the last name segment; advance to the post-name token.
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            throw new NotSupportedException("Only ALTER TABLE … SET (SYSTEM_VERSIONING = OFF) is supported.");

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

        // Flip both tables to regular status. Period and GENERATED ALWAYS
        // column metadata stays — only the parent → history link and the
        // sibling's history-role flag clear.
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
