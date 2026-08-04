using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
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
            case ReservedKeyword { Keyword: Keyword.Database }:
                return TryParseDropDatabase(context);
            case ReservedKeyword { Keyword: Keyword.User }:
                return TryParseDropUser(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Role }:
                return TryParseDropRole(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Login }:
                return TryParseDropLogin(context);
            case Name serverWord when serverWord.Value.Equals("SERVER", StringComparison.OrdinalIgnoreCase):
                return TryParseDropServerRole(context);
            case Name appWord when appWord.Value.Equals("APPLICATION", StringComparison.OrdinalIgnoreCase):
                return TryParseDropApplicationRole(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.FullText }:
                return Simulation.TryParseDropFullText(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Xml }:
                return Simulation.TryParseDropXml(context);
            case Name synonymWord when synonymWord.Value.Equals("SYNONYM", StringComparison.OrdinalIgnoreCase):
                return TryParseDropSynonym(context);
            case Name assemblyWord when assemblyWord.Value.Equals("ASSEMBLY", StringComparison.OrdinalIgnoreCase):
                return TryParseDropAssembly(context);
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
    /// Parses <c>DROP DATABASE [IF EXISTS] name[, name...]</c>. Each name is a
    /// single identifier (bare or bracketed / quoted). Cursor on entry is the
    /// <c>DATABASE</c> keyword. Per-name outcomes (probe-confirmed against SQL
    /// Server 2025): a system database (<c>master</c> / <c>tempdb</c> /
    /// <c>model</c> / <c>msdb</c>) → Msg 3708; a database in use by any session
    /// (its <c>CurrentDatabase</c>) → Msg 3702; a missing database → Msg 3701
    /// unless <c>IF EXISTS</c> is present (then a silent no-op).
    /// </summary>
    private static bool TryParseDropDatabase(ParserContext context)
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

        var simulation = context.Batch.Connection.Simulation;
        while (true)
        {
            if (context.Token is not Name nameToken)
                return false;
            DropOneDatabase(context, simulation, nameToken.Value, ifExists);

            // The name is a single token; peek the next for the comma separator.
            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return true;
    }

    /// <summary>
    /// Applies the <see cref="TryParseDropDatabase"/> rejection ladder to one
    /// database name and removes it from <see cref="Simulation.Databases"/>
    /// (freeing its <c>database_id</c> for the next <c>CREATE DATABASE</c> /
    /// import to reuse). No-ops in skip mode.
    /// </summary>
    private static void DropOneDatabase(ParserContext context, Simulation simulation, string name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        if (Simulation.SystemDatabaseNames.Contains(name))
            throw SimulatedSqlException.CannotDropSystemDatabase(name);
        // DROP DATABASE answers at server scope like its CREATE sibling: ALTER
        // ANY DATABASE, or dbcreator membership. The denial is Msg 3701 at
        // severity 11 state 2 — a different shape from every object drop's
        // severity 14 state 20 (probe-confirmed).
        if (!PermissionEnforcement.HasDatabaseDdlAuthority(context.Batch, Permission.AlterAnyDatabase))
            throw SimulatedSqlException.DropDatabasePermissionDenied(name);
        // Msg 3702 for a self-drop: the executing session sitting in the target
        // database (USE foo; DROP DATABASE foo). Real also blocks *other* active
        // sessions, but the teardown idiom every app runs first —
        // SET SINGLE_USER WITH ROLLBACK IMMEDIATE — evicts those; the simulator
        // treats that ALTER as parse-and-discard (no eviction model), so it
        // mirrors the idiom's intent by treating other sessions as already
        // evicted and blocking only the executing one.
        if (BuiltInToken.Comparer.Equals(context.CurrentDatabase.Name, name))
            throw SimulatedSqlException.CannotDropDatabaseInUse(name);
        lock (simulation.Databases)
        {
            if (!simulation.Databases.Remove(name) && !ifExists)
                throw SimulatedSqlException.CannotDropDatabaseNotFound(name);
        }
    }

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
        if (IsReservedSchemaName(context.CurrentDatabase.Collation, schemaName))
            throw SimulatedSqlException.CannotDropProtectedSchema(schemaName);
        if (!context.CurrentDatabase.Schemas.TryGetValue(schemaName, out var schema))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropSchemaDoesNotExist(schemaName);
        }
        // DROP SCHEMA needs CONTROL on the schema or the database-scope ALTER
        // ANY SCHEMA — probe-confirmed that schema ALTER alone is NOT enough,
        // unlike every object drop. The denial is the same 15151 a missing
        // schema earns.
        if (!PermissionEnforcement.HasSchemaControl(context.Batch, schema)
            && !PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnySchema))
        {
            throw SimulatedSqlException.CannotDropSchemaDoesNotExist(schemaName);
        }
        var blocker = FirstSchemaResident(schema);
        if (blocker is not null)
            throw SimulatedSqlException.CannotDropSchemaBecauseNotEmpty(schemaName, blocker);
        _ = context.CurrentDatabase.Schemas.TryRemove(schemaName, out _);
        RecordDdlEvent(context, "DROP_SCHEMA", schemaName, schemaName, "SCHEMA");
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
    /// <summary>
    /// The <c>DROP TYPE</c> gate — schema ALTER (or the CONTROL that covers it).
    /// Real also accepts <c>CONTROL</c> on the type itself, a securable class the
    /// simulator's GRANT surface doesn't carry. Denial is Msg 218, the same
    /// record a missing type earns, naming the type as written.
    /// </summary>
    private static void RejectUnauthorizedTypeDrop(ParserContext context, Schema schema, MultiPartName name)
    {
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, schema))
            throw SimulatedSqlException.TypeDoesNotExist(name.ToString());
    }

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
            if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnyDatabaseDdlTrigger))
                throw SimulatedSqlException.DropObjectPermissionDenied("trigger", name.Leaf);
            context.Batch.AcquireStatementLock(existingDdl.SchemaLock, LockMode.SchemaModification);
            if (!context.CurrentDatabase.DdlTriggers.TryRemove(name.Leaf, out _) && !ifExists)
                throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
            RecordDdlEvent(context, "DROP_TRIGGER", EventSchemaName(name), name.Leaf, "TRIGGER");
            return;
        }

        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Triggers.TryGetValue(name.Leaf, out var existing))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
        }

        // A read-only database refuses the drop, but only once the object
        // is known to exist — real reports the ordinary not-found error first.
        schema.Database.RejectWriteWhenReadOnly();

        // A DML trigger isn't a grantable securable of its own: real gates its
        // DROP on ALTER of the parent table / view (probe-confirmed).
        if (!PermissionEnforcement.HasObjectAlter(
                context.Batch, schema.Database, existing.Parent.ObjectId, existing.Parent.SchemaId))
        {
            throw SimulatedSqlException.DropObjectPermissionDenied("trigger", name.Leaf);
        }
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Triggers.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropTriggerDoesNotExist(name.ToString());
        RecordDdlEvent(
            context, "DROP_TRIGGER", schema.Name, name.Leaf, "TRIGGER",
            existing.Parent.Name,
            existing.Parent is View ? "VIEW" : "TABLE");
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

        // A read-only database refuses the drop, but only once the object
        // is known to exist — real reports the ordinary not-found error first.
        schema.Database.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDropAuthority(context.Batch, schema, existing.ObjectId))
            throw SimulatedSqlException.DropObjectPermissionDenied("sequence", name.Leaf);
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Sequences.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropSequenceDoesNotExist(name.ToString());
        RecordDdlEvent(context, "DROP_SEQUENCE", schema.Name, name.Leaf, "SEQUENCE");
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
            schema.Database.RejectWriteWhenReadOnly();
            RejectUnauthorizedTypeDrop(context, schema, name);
            _ = schema.AliasTypes.TryRemove(name.Leaf, out _);
            RecordDdlEvent(context, "DROP_TYPE", schema.Name, name.Leaf, "TYPE");
            return;
        }
        if (schema is null || !schema.TableTypes.TryGetValue(name.Leaf, out var tableType))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.TypeDoesNotExist(name.ToString());
        }

        // A read-only database refuses the drop, but only once the object
        // is known to exist — real reports the ordinary not-found error first.
        schema.Database.RejectWriteWhenReadOnly();

        RejectUnauthorizedTypeDrop(context, schema, name);
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
        RecordDdlEvent(context, "DROP_TYPE", schema.Name, name.Leaf, "TYPE");
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

        // A read-only database refuses the drop, but only once the object
        // is known to exist — real reports the ordinary not-found error first.
        schema.Database.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDropAuthority(context.Batch, schema, existing.ObjectId))
            throw SimulatedSqlException.DropObjectPermissionDenied("procedure", name.Leaf);
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        if (!schema.Procedures.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropProcedureDoesNotExist(name.ToString());
        RecordDdlEvent(context, "DROP_PROCEDURE", schema.Name, name.Leaf, "PROCEDURE");
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

        // A read-only database refuses the drop, but only once the object
        // is known to exist — real reports the ordinary not-found error first.
        schema.Database.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDropAuthority(context.Batch, schema, droppedView.ObjectId))
            throw SimulatedSqlException.DropObjectPermissionDenied("view", name.Leaf);
        context.Batch.AcquireStatementLock(droppedView.SchemaLock, LockMode.SchemaModification);
        RejectDropOfSchemaBoundReferent(context.CurrentDatabase, droppedView, "DROP VIEW", name);
        if (!schema.Views.TryRemove(name.Leaf, out _))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropViewDoesNotExist(name.ToString());
        }
        DetachIndexedViewDependencies(droppedView);
        CascadeDropTriggers(context.CurrentDatabase, droppedView);
        RecordDdlEvent(context, "DROP_VIEW", schema.Name, name.Leaf, "VIEW");
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

        // A read-only database refuses the drop, but only once the object
        // is known to exist — real reports the ordinary not-found error first.
        schema.Database.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDropAuthority(context.Batch, schema, existing.ObjectId))
            throw SimulatedSqlException.DropObjectPermissionDenied("function", name.Leaf);
        context.Batch.AcquireStatementLock(existing.SchemaLock, LockMode.SchemaModification);
        RejectDropOfSchemaBoundReferent(context.CurrentDatabase, existing, "DROP FUNCTION", name);
        if (!schema.Functions.TryRemove(name.Leaf, out _) && !ifExists)
            throw SimulatedSqlException.CannotDropFunctionDoesNotExist(name.ToString());
        RecordDdlEvent(context, "DROP_FUNCTION", schema.Name, name.Leaf, "FUNCTION");
    }

    private static void DropOneTable(ParserContext context, MultiPartName name, bool ifExists)
    {
        // In a skipped IF branch, gate both the existence check (Msg 3701)
        // and the dict removal: `IF OBJECT_ID('foo','U') IS NOT NULL DROP
        // TABLE foo` when foo doesn't exist should silently skip the un-taken
        // branch rather than raise.
        if (context.Batch.IsSkipping)
            return;
        var isLocalTempTable = BatchContext.IsLocalTempName(name.Leaf);
        var isGlobalTempTable = BatchContext.IsGlobalTempName(name.Leaf);
        var isTempTable = isLocalTempTable || isGlobalTempTable;
        // For temp tables a qualifier is cosmetic (real SQL Server ignores it
        // — `tempdb..#foo` and `tempdb.dbo.#foo` both resolve the same way,
        // and `##foo` accepts the same qualifier shapes). For regular tables
        // the schema is looked up through CurrentDatabase.Schemas; a missing
        // schema or db-mismatched 3-part name surfaces the standard Msg 3701
        // below.
        Schema? schema = null;
        var destination = isLocalTempTable
            ? context.Batch.Connection.TempTables
            : isGlobalTempTable
                ? context.Batch.Connection.Simulation.GlobalTempTables
                : context.Batch.TryResolveSchema(name, out schema) ? schema.HeapTables : null;
        if (destination is null || !destination.TryGetValue(name.Leaf, out var removedTable))
        {
            // A name belonging to another object kind — a synonym above all,
            // since `DROP TABLE syn` reads as dropping what the synonym points
            // at — raises Msg 3705 naming that kind, and IF EXISTS doesn't
            // suppress it (the object does exist).
            if (schema is not null)
                RejectDropOfOtherKind(schema, name, "TABLE");
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTableDoesNotExist(name.ToString());
        }
        // A read-only database refuses the drop, once the table is known to
        // exist. Temp tables live in tempdb and are exempt however the session's
        // own database is set.
        removedTable.OwningDatabase?.RejectWriteWhenReadOnly();

        // DROP TABLE needs ALTER on the schema or CONTROL on the table itself
        // (a plain object-scope ALTER is insufficient — probe M5b); a
        // non-privileged principal gets Msg 3701 sev 14 state 20. Temp tables
        // are session-owned and exempt.
        if (!isTempTable && schema is not null && !PermissionEnforcement.HasDropAuthority(context.Batch, schema, removedTable.ObjectId))
            throw SimulatedSqlException.DropObjectPermissionDenied("table", name.Leaf);
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
        // Schema-binding protection, which real applies after the FK gate
        // (probe-confirmed: a table that is both an FK parent and a
        // schema-bound view's base reports Msg 3726). Temp tables are exempt —
        // a schema-bound body can't name one.
        if (!isTempTable && schema is not null)
            RejectDropOfSchemaBoundReferent(context.CurrentDatabase, removedTable, "DROP TABLE", name);
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
            tx.UndoLog.RecordTempTableRemoval(destination, name.Leaf, removedTable);
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
            RecordDdlEvent(context, "DROP_TABLE", schema?.Name ?? Database.DefaultSchemaName, name.Leaf, "TABLE");
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
            var firstName = BatchContext.ParseObjectName(context);
            context.MoveNextOptional();
            string indexName;
            MultiPartName tableName;
            if (context.Token is ReservedKeyword { Keyword: Keyword.On })
            {
                // Standard `index_name ON table` form.
                indexName = firstName.Leaf;
                context.MoveNextRequired();
                tableName = BatchContext.ParseObjectName(context);
                context.MoveNextOptional();
            }
            else
            {
                // Deprecated `table.index` (also `schema.table.index`) form,
                // accepted by real SQL Server: the rightmost segment names the
                // index, the remaining left segments name the table. A missing
                // index still raises Msg 3701 through DropOneIndex.
                if (firstName.Count < 2)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                indexName = firstName.Leaf;
                tableName = WithoutLeaf(firstName);
            }
            DropOneIndex(context, indexName, tableName, ifExists);

            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return true;
    }

    /// <summary>
    /// Builds the table-name portion of a deprecated <c>DROP INDEX
    /// table.index</c> reference by dropping the rightmost (index-name)
    /// segment.
    /// </summary>
    private static MultiPartName WithoutLeaf(MultiPartName name)
    {
        var result = new MultiPartName(name[0]);
        for (var i = 1; i < name.Count - 1; i++)
            result = result.WithAddedPart(name[i]);
        return result;
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

        // DROP INDEX is gated on ALTER of the parent table; real reports the
        // written table name plus the index leaf, at Msg 1088 state 9.
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForAlterIndex($"{tableName}.{indexName}");

        qualifiedTableName = FormatQualifiedTableName(tableName, table);
        foreach (var kc in table.KeyConstraints)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(kc.Name, indexName))
                throw SimulatedSqlException.ExplicitDropIndexNotAllowed(qualifiedTableName, indexName, kc.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE");
        }

        for (var i = 0; i < table.Indexes.Count; i++)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(table.Indexes[i].Name, indexName))
            {
                if (table.Indexes[i].IsClustered && RetentionCleanupDependsOn(context, table))
                    throw SimulatedSqlException.CannotDropRetentionCleanupIndex(qualifiedTableName, indexName);
                table.Indexes.RemoveAt(i);
                RecordDdlEvent(context, "DROP_INDEX", EventSchemaName(tableName), indexName, "INDEX", table.Name, "TABLE");
                return;
            }
        }

        if (ifExists)
            return;
        throw SimulatedSqlException.CannotDropIndexDoesNotExist(qualifiedTableName, indexName, state: 7);
    }

    /// <summary>
    /// Whether <paramref name="table"/> is a history sibling whose base is on a
    /// finite <c>HISTORY_RETENTION_PERIOD</c> — the state that pins the
    /// history table's clustered index in place (Msg 13766). Real releases the
    /// index the moment the base returns to INFINITE retention or versioning is
    /// turned off, both probe-confirmed, so this asks the live link rather than
    /// recording a flag on the index.
    /// </summary>
    private static bool RetentionCleanupDependsOn(ParserContext context, HeapTable table)
    {
        if (!table.IsHistoryTable)
            return false;
        foreach (var schema in context.Batch.DatabaseFor(table).Schemas.Values)
        {
            foreach (var candidate in schema.HeapTables.Values)
            {
                if (ReferenceEquals(candidate.SystemVersioning, table))
                    return candidate.HistoryRetentionUnit != HistoryRetentionUnit.Infinite;
            }
        }
        return false;
    }

    /// <summary>
    /// Raises <strong>Msg 3729</strong> when a <c>WITH SCHEMABINDING</c>
    /// module references the object being dropped. <paramref name="statement"/>
    /// is the verb pair real echoes; the target is echoed <b>as the statement
    /// spelled it</b> — `DROP TABLE t` reports <c>'t'</c> and
    /// `DROP TABLE dbo.t` reports <c>'dbo.t'</c> (probe-confirmed) — and the
    /// blocker surfaces as its bare leaf.
    /// </summary>
    private static void RejectDropOfSchemaBoundReferent(
        Database database, SchemaObject target, string statement, MultiPartName writtenName)
    {
        if (SchemaBinding.FindReferencingModule(database, target) is { } referencing)
        {
            throw SimulatedSqlException.CannotDropReferencedBySchemaBoundObject(
                statement, writtenName.ToString(), referencing.Name);
        }
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
