using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DROP TABLE [IF EXISTS] name[, name...]</c>. Routes <c>#foo</c>
    /// names to the connection's <see cref="SimulatedDbConnection.TempTables"/>
    /// dict; everything else to the named schema's heap-table dict (or the
    /// default <c>dbo</c> schema for an unqualified reference). Missing
    /// table without <c>IF EXISTS</c> raises Msg 3701 St 5 (probe-confirmed
    /// against SQL Server 2025 for both regular and temp targets); with
    /// <c>IF EXISTS</c> the miss is silent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comma-list form (<c>DROP TABLE a, b, c</c>) is real SQL Server
    /// syntax — modeled here because it composes naturally with the single-
    /// table case and the parser's lookahead doesn't have to backtrack.
    /// </para>
    /// <para>
    /// Three-part qualifiers on a <c>#</c>-prefixed leaf are accepted and
    /// ignored (probe-confirmed: <c>tempdb..#foo</c>, <c>tempdb.dbo.#foo</c>,
    /// and even <c>claude..#foo</c> all resolve to the same session-local
    /// table). Qualified non-<c>#</c> names are rejected today — the
    /// simulator has a single database and no <c>USE</c>, so a database
    /// qualifier other than the current one would have no resolution target.
    /// </para>
    /// </remarks>
    private static bool TryParseDrop(ParserContext context)
    {
        // DROP INDEX has a distinct grammar — `name ON table [, name ON table, …]`
        // (not just `name`). Route it through its own parser before the
        // shared comma-list path below.
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Index })
            return TryParseDropIndex(context);

        // DROP USER / DROP ROLE / DROP FULLTEXT go through dedicated parsers
        // — they have their own [IF EXISTS] / comma-list-free / sub-keyword
        // grammars and live in per-database dicts rather than per-schema.
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.User }:
                return TryParseDropUser(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Role }:
                return TryParseDropRole(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.FullText }:
                return Simulation.TryParseDropFullText(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Xml }:
                return Simulation.TryParseDropXml(context);
        }

        var targetKind = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Table } => DropTargetKind.Table,
            ReservedKeyword { Keyword: Keyword.Function } => DropTargetKind.Function,
            ReservedKeyword { Keyword: Keyword.View } => DropTargetKind.View,
            ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc } => DropTargetKind.Procedure,
            ReservedKeyword { Keyword: Keyword.Trigger } => DropTargetKind.Trigger,
            ReservedKeyword { Keyword: Keyword.Schema } => DropTargetKind.Schema,
            UnquotedString { ContextualKeyword: ContextualKeyword.Type } => DropTargetKind.Type,
            UnquotedString { ContextualKeyword: ContextualKeyword.Sequence } => DropTargetKind.Sequence,
            _ => DropTargetKind.None,
        };
        if (targetKind == DropTargetKind.None)
            return false;

        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                return false;
            ifExists = true;
            context.MoveNextRequired();
        }

        while (true)
        {
            var name = BatchContext.ParseObjectName(context);
            switch (targetKind)
            {
                case DropTargetKind.Function:
                    DropOneFunction(context, name, ifExists);
                    break;
                case DropTargetKind.View:
                    DropOneView(context, name, ifExists);
                    break;
                case DropTargetKind.Procedure:
                    DropOneProcedure(context, name, ifExists);
                    break;
                case DropTargetKind.Type:
                    DropOneType(context, name, ifExists);
                    break;
                case DropTargetKind.Sequence:
                    DropOneSequence(context, name, ifExists);
                    break;
                case DropTargetKind.Trigger:
                    DropOneTrigger(context, name, ifExists);
                    break;
                case DropTargetKind.Schema:
                    DropOneSchema(context, name, ifExists);
                    break;
                default:
                    DropOneTable(context, name, ifExists);
                    break;
            }

            // ParseObjectName leaves the cursor on the last name segment;
            // peek for the comma list separator without permanently advancing
            // past statement-boundary tokens.
            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return true;
    }

    private enum DropTargetKind { None, Table, Function, View, Procedure, Type, Sequence, Trigger, Schema }

    /// <summary>
    /// Removes one entry from <see cref="Database.Schemas"/>. Missing schema
    /// without <c>IF EXISTS</c> → Msg 15151; reserved schema names (<c>dbo</c>,
    /// <c>sys</c>, <c>INFORMATION_SCHEMA</c>) → Msg 15150; non-empty schema
    /// (any heap table / view / function / procedure / sequence / trigger /
    /// table type) → Msg 3729 naming the first object encountered. Probe-
    /// confirmed verbatim wording for all three rejection paths against SQL
    /// Server 2025. The schema name is read from the 1-part leaf — real
    /// SQL Server's grammar rejects qualified <c>db.schema</c> at parse;
    /// the simulator's <see cref="BatchContext.ParseObjectName"/> accepts
    /// up to 4 parts but the qualifier is ignored here (no <c>USE</c>).
    /// </summary>
    private static void DropOneSchema(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schemaName = name.Leaf;
        if (IsReservedSchemaName(schemaName))
            throw SimulatedSqlException.CannotDropProtectedSchema(schemaName);
        if (!context.CurrentDatabase.Schemas.TryGetValue(schemaName, out var schema))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropSchemaDoesNotExist(schemaName);
        }
        var blocker = FirstSchemaResident(schema);
        if (blocker is not null)
            throw SimulatedSqlException.CannotDropSchemaBecauseNotEmpty(schemaName, blocker);
        _ = context.CurrentDatabase.Schemas.TryRemove(schemaName, out _);
    }

    /// <summary>
    /// Returns the name of any object currently residing in
    /// <paramref name="schema"/> — used by the non-empty-schema rejection
    /// path. Walks the shared-namespace objects via
    /// <see cref="Schema.SchemaObjects"/> first (so DML-targetable objects
    /// surface preferentially); falls through to <see cref="Schema.TableTypes"/>
    /// which occupies the parallel type namespace. Returns <c>null</c> when
    /// the schema is completely empty.
    /// </summary>
    private static string? FirstSchemaResident(Schema schema)
    {
        foreach (var obj in schema.SchemaObjects())
            return obj.Name;
        foreach (var tt in schema.TableTypes.Values)
            return tt.Name;
        return null;
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Triggers"/>
    /// dict (DML trigger), or from <see cref="Database.DdlTriggers"/> if the
    /// caller follows the trigger name with <c>ON DATABASE</c>. Missing
    /// trigger → Msg 3701 (trigger variant) unless <paramref name="ifExists"/>
    /// is set. Probe-confirmed wording.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>DROP TRIGGER</c> grammar requires <c>ON DATABASE</c>
    /// for database-scope DDL triggers and forbids it for DML triggers
    /// (probe-confirmed). The simulator peeks the next token to discriminate;
    /// if it's <c>ON</c> followed by <c>DATABASE</c>, the DDL-trigger
    /// dictionary is consulted, otherwise the per-schema DML-trigger dict.
    /// </remarks>
    private static void DropOneTrigger(ParserContext context, MultiPartName name, bool ifExists)
    {
        // Peek for the ON DATABASE trailer. The caller's loop normally
        // advances after this returns; we capture the next token here to
        // route correctly, and if not "ON DATABASE", leave the cursor on
        // the name's last segment for the caller's advance step.
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        var isDdl = false;
        if (next is ReservedKeyword { Keyword: Keyword.On })
        {
            var afterOn = context.GetNextOptional();
            if (afterOn is ReservedKeyword { Keyword: Keyword.Database })
            {
                isDdl = true;
            }
            else
            {
                context.RestoreCheckpoint(checkpoint);
            }
        }
        else
        {
            context.RestoreCheckpoint(checkpoint);
        }

        if (context.Batch.IsSkipping)
            return;

        if (isDdl)
        {
            if (!context.CurrentDatabase.DdlTriggers.TryGetValue(name.Leaf, out var existingDdl))
            {
                if (ifExists)
                    return;
                throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
            }
            context.Batch.AcquireStatementLock(existingDdl.SchemaLock, LockMode.SchemaModification);
            if (!context.CurrentDatabase.DdlTriggers.TryRemove(name.Leaf, out _) && !ifExists)
                throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
            return;
        }

        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Triggers.TryGetValue(name.Leaf, out var existing))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
        }
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Triggers.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Sequences"/>
    /// dict. Missing sequence → Msg 3701 (sequence variant) unless
    /// <paramref name="ifExists"/> is set.
    /// </summary>
    private static void DropOneSequence(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Sequences.TryGetValue(name.Leaf, out var existing))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropSequenceDoesNotExist(name.ToString());
        }
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Sequences.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropSequenceDoesNotExist(name.ToString());
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.TableTypes"/>
    /// or <see cref="Schema.AliasTypes"/> dict (the two share the type-name
    /// namespace). Probe-confirmed wording on the two failure modes against
    /// SQL Server 2025: missing without IF EXISTS → Msg 218; table-type
    /// referenced by at least one procedure → Msg 3732 (the simulator scans
    /// every procedure in the database and names the first one found — real
    /// SQL Server does the same, naming a single referencing object even
    /// when more than one exists). Types don't participate in the undo log
    /// (same convention as CREATE / DROP regular tables — only temp-table
    /// DDL is transactional).
    /// </summary>
    /// <remarks>
    /// Alias-type drops do NOT scan tables for column references in this
    /// bundle — the simulator's <c>HeapColumn</c> doesn't retain a back-
    /// pointer to its declaring alias, so a fidelity-faithful Msg 3732 path
    /// for alias types would require threading the alias pointer through
    /// every column creation site. Deferred as a known fidelity gap; the
    /// bacpac-loader use case never drops alias types during import.
    /// </remarks>
    private static void DropOneType(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is not null && schema.AliasTypes.TryGetValue(name.Leaf, out _))
        {
            _ = schema.AliasTypes.TryRemove(name.Leaf, out _);
            return;
        }
        if (schema is null || !schema.TableTypes.TryGetValue(name.Leaf, out var tableType))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.TypeDoesNotExist(name.ToString());
        }
        context.Batch.AcquireStatementLock(tableType.SchemaLock, LockMode.SchemaModification);
        // Scan every procedure in every schema of the current database for
        // a parameter that references this table type. Procedures are the
        // only object kind that can take a TVP today; views / functions
        // grow this surface when those features land.
        foreach (var s in context.CurrentDatabase.Schemas.Values)
        {
            foreach (var proc in s.Procedures.Values)
            {
                foreach (var param in proc.Parameters)
                {
                    if (ReferenceEquals(param.TableType, tableType))
                        throw SimulatedSqlException.CannotDropTypeBecauseReferenced($"{schema.Name}.{tableType.Name}", proc.Name);
                }
            }
        }
        _ = schema.TableTypes.TryRemove(name.Leaf, out _);
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Procedures"/>
    /// dict. Missing procedure → Msg 3701 (procedure variant) unless
    /// <paramref name="ifExists"/> is set. Same shape as
    /// <see cref="DropOneFunction"/> / <see cref="DropOneView"/> —
    /// procedures don't participate in the undo log either.
    /// </summary>
    private static void DropOneProcedure(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Procedures.TryGetValue(name.Leaf, out var existing))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropProcedureDoesNotExist(name.ToString());
        }
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Procedures.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropProcedureDoesNotExist(name.ToString());
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Views"/>
    /// dict. Missing view → Msg 3701 (view variant) unless
    /// <paramref name="ifExists"/> is set.
    /// </summary>
    private static void DropOneView(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Views.TryGetValue(name.Leaf, out var droppedView))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropViewDoesNotExist(name.ToString());
        }
        context.Batch.AcquireStatementLock(droppedView.SchemaLock, LockMode.SchemaModification);
        if (!schema.Views.TryRemove(name.Leaf, out _))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropViewDoesNotExist(name.ToString());
        }
        CascadeDropTriggers(context.CurrentDatabase, droppedView);
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Functions"/>
    /// dict. Routing mirrors <see cref="DropOneTable"/>'s regular-table branch:
    /// resolve the schema, lookup the leaf, raise Msg 3701 (function variant)
    /// on miss unless <paramref name="ifExists"/> is set. Functions don't
    /// participate in the undo log — same asymmetry as regular CREATE TABLE /
    /// DROP TABLE (only temp-table DDL is transactional).
    /// </summary>
    private static void DropOneFunction(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Functions.TryGetValue(name.Leaf, out var existing))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropFunctionDoesNotExist(name.ToString());
        }
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Functions.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropFunctionDoesNotExist(name.ToString());
    }

    private static void DropOneTable(ParserContext context, MultiPartName name, bool ifExists)
    {
        // In a skipped IF branch, gate both the existence check (Msg 3701)
        // and the dict removal: `IF OBJECT_ID('foo','U') IS NOT NULL DROP
        // TABLE foo` when foo doesn't exist should silently skip the un-taken
        // branch rather than raise.
        if (context.Batch.IsSkipping)
            return;
        var isTempTable = BatchContext.IsLocalTempName(name.Leaf);
        // For temp tables a qualifier is cosmetic (real SQL Server ignores it
        // — `tempdb..#foo` and `tempdb.dbo.#foo` both resolve to the same
        // session-local table). For regular tables the schema is looked up
        // through CurrentDatabase.Schemas; a missing schema or db-mismatched
        // 3-part name surfaces the standard Msg 3701 below.
        var destination = isTempTable
            ? context.Batch.Connection.TempTables
            : context.Batch.TryResolveSchema(name, out var schema) ? schema.HeapTables : null;
        if (destination is null || !destination.TryGetValue(name.Leaf, out var removedTable))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTableDoesNotExist(name.ToString());
        }
        // Sch-M on the target table for the duration of the statement.
        // Waits for any concurrent Sch-S holders (readers / writers) to drain
        // before we proceed; honors the connection's @@LOCK_TIMEOUT so a
        // stuck reader on another connection surfaces Msg 1222 instead of
        // hanging this DROP indefinitely. Temp tables are session-local and
        // not concurrency-reachable, but acquiring uniformly keeps the path
        // simple and is effectively free for the single-owner case.
        context.Batch.AcquireStatementLock(removedTable.SchemaLock, LockMode.SchemaModification);
        // DROP TABLE on a system-versioned temporal parent or its history
        // sibling is rejected — caller must ALTER TABLE … SET (SYSTEM_VERSIONING
        // = OFF) first (probe-confirmed Msg 13552 wording against SQL Server
        // 2025). Applies to permanent tables only; temp tables can't be
        // temporal so the gate doesn't fire there.
        if (!isTempTable && (removedTable.IsHistoryTable || removedTable.SystemVersioning is not null))
            throw SimulatedSqlException.CannotDropTemporalTable(QualifyTableName(removedTable, context.CurrentDatabase));
        // FK protection: refuse to drop a table that is the parent (referenced
        // table) of any FK constraint. Real SQL Server's wording targets the
        // bare table name, not the qualified one (probe-confirmed).
        if (removedTable.IncomingForeignKeys.Count > 0)
            throw SimulatedSqlException.CannotDropTableReferencedByForeignKey(removedTable.Name);
        if (!destination.TryRemove(name.Leaf, out _))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTableDoesNotExist(name.ToString());
        }
        // Temp-table DDL participates in transaction rollback (matching real
        // SQL Server). Regular DROP TABLE isn't logged — same asymmetry
        // documented for CREATE TABLE.
        if (isTempTable && context.Connection.CurrentTransaction is { } tx)
        {
            tx.UndoLog.RecordTempTableRemoval(context.Batch.Connection.TempTables, name.Leaf, removedTable);
        }
        else
        {
            // Detach the dropped child's outgoing FKs from each parent's
            // IncomingForeignKeys list so future DROP TABLE on the parent
            // doesn't see a stale reference. The FK protection check above
            // guarantees this table had no incoming FKs.
            foreach (var fk in removedTable.OutgoingForeignKeys)
                _ = fk.ReferencedTable.IncomingForeignKeys.RemoveAll(other => ReferenceEquals(other, fk));
            CascadeDropTriggers(context.CurrentDatabase, removedTable);
        }
    }

    /// <summary>
    /// Parses <c>DROP INDEX [IF EXISTS] name ON table [, name ON table, …]</c>.
    /// Cursor on entry: <c>INDEX</c> keyword (already consumed by
    /// <see cref="TryParseDrop"/>). Each entry resolves independently:
    /// missing parent table raises Msg 3701 St 6; missing index on a found
    /// table raises Msg 3701 St 7; dropping a system index that backs a
    /// PRIMARY KEY or UNIQUE constraint raises Msg 3723. <c>IF EXISTS</c>
    /// suppresses the missing-index branches; the dup-on-PK rejection
    /// fires regardless (real SQL Server's behavior — IF EXISTS only
    /// gates the does-not-exist path).
    /// </summary>
    private static bool TryParseDropIndex(ParserContext context)
    {
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                return false;
            ifExists = true;
            context.MoveNextRequired();
        }

        while (true)
        {
            if (context.Token is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var indexName = ((Name)context.Token).Value;
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var tableName = BatchContext.ParseObjectName(context);
            DropOneIndex(context, indexName, tableName, ifExists);

            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return true;
    }

    /// <summary>
    /// Resolves the target table, then removes the matching entry from
    /// <see cref="HeapTable.Indexes"/>. Surfaces:
    /// <list type="bullet">
    /// <item>Msg 3701 St 6 when the parent table itself doesn't exist
    /// (unless <c>IF EXISTS</c>).</item>
    /// <item>Msg 3701 St 7 when the table exists but has no such index
    /// (unless <c>IF EXISTS</c>).</item>
    /// <item>Msg 3723 when the named index is a PK/UQ-backing constraint
    /// (fires regardless of <c>IF EXISTS</c> — probed against SQL Server
    /// 2025).</item>
    /// </list>
    /// </summary>
    private static void DropOneIndex(ParserContext context, string indexName, MultiPartName tableName, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;

        string qualifiedTableName;
        if (!context.Batch.TryResolveTable(tableName, out var table))
        {
            if (ifExists)
                return;
            qualifiedTableName = tableName.Count >= 2
                ? $"{tableName.ImmediateQualifier}.{tableName.Leaf}"
                : $"{Database.DefaultSchemaName}.{tableName.Leaf}";
            throw SimulatedSqlException.CannotDropIndexDoesNotExist(qualifiedTableName, indexName, state: 6);
        }

        qualifiedTableName = FormatQualifiedTableName(tableName, table);
        foreach (var kc in table.KeyConstraints)
        {
            if (Collation.Default.Equals(kc.Name, indexName))
                throw SimulatedSqlException.ExplicitDropIndexNotAllowed(qualifiedTableName, indexName, kc.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE");
        }

        for (var i = 0; i < table.Indexes.Count; i++)
        {
            if (Collation.Default.Equals(table.Indexes[i].Name, indexName))
            {
                table.Indexes.RemoveAt(i);
                return;
            }
        }

        if (ifExists)
            return;
        throw SimulatedSqlException.CannotDropIndexDoesNotExist(qualifiedTableName, indexName, state: 7);
    }

    /// <summary>
    /// Removes every trigger across the database whose <see cref="Trigger.Parent"/>
    /// matches the dropped object. Cascading from DROP TABLE / DROP VIEW
    /// matches real SQL Server's "you can't have a trigger without its parent"
    /// invariant. Triggers don't participate in the undo log; this fires
    /// unconditionally on DROP outside transactional temp-table scope.
    /// </summary>
    private static void CascadeDropTriggers(Database database, SchemaObject droppedParent)
    {
        foreach (var schema in database.Schemas.Values)
        {
            string[]? names = null;
            foreach (var kv in schema.Triggers)
            {
                if (ReferenceEquals(kv.Value.Parent, droppedParent))
                {
                    names ??= [];
                    Array.Resize(ref names, names.Length + 1);
                    names[^1] = kv.Key;
                }
            }
            if (names is null) continue;
            foreach (var n in names)
                _ = schema.Triggers.TryRemove(n, out _);
        }
    }
}
