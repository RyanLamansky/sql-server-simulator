using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    // The sev-10 "Caution" info message every successful sp_rename emits
    // (Msg 15477, probe-confirmed against SQL Server 2025, 2026-07-23). Delivered
    // through the InfoMessage / PRINT path, never thrown.
    private const string RenameCautionMessage =
        "Caution: Changing any part of an object name could break scripts and stored procedures.";

    // Real raises it from line 801 of sp_rename's own text, so it names that
    // line and the procedure whatever was renamed (probed 2026-09-26).
    private static void QueueRenameCaution(BatchContext batch)
    {
        var caution = batch.InfoMessage(@class: 10, state: 1, number: 15477, message: RenameCautionMessage);
        caution.LineNumber = 801;
        caution.Procedure = "sp_rename";
        batch.Connection.PendingMessages.Enqueue(caution);
    }

    /// <summary>
    /// Handles <c>EXEC sp_rename @objname, @newname [, @objtype]</c> — the
    /// object / column / index rename schema-migration tools (Alembic's
    /// <c>rename_table</c> / <c>alter_column</c>, SSMS) emit. Reachable through
    /// both <c>EXEC sp_rename …</c> and the RPC-by-name path (a name-form system
    /// proc is re-synthesized as an <c>EXEC</c>), so a single dispatch arm serves
    /// both. Supports positional and named (<c>@objname=</c> / <c>@newname=</c> /
    /// <c>@objtype=</c>) arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed behavior (SQL Server 2025, 2026-07-23):
    /// </para>
    /// <list type="bullet">
    /// <item>Success mutates catalog state and buffers Msg 15477 (severity 10)
    /// as an info message; the proc returns 0.</item>
    /// <item><c>@objtype</c> NULL / omitted renames a table (or object); the
    /// resolved leaf moves within its schema. A table.leaf name no object
    /// answers renames that table's column or index instead. A missing object
    /// → Msg 15225 with <c>@itemtype</c> rendered as <c>(null)</c>.</item>
    /// <item><c>@objtype</c> = COLUMN / INDEX (case-insensitive) renames a column
    /// / index of <c>[schema.]table.leaf</c>. A missing parent table or leaf →
    /// Msg 15248 ("ambiguous or the claimed @objtype is wrong").</item>
    /// <item>A colliding <c>@newname</c> → Msg 15335, naming the kind as
    /// <c>@objtype</c> spelled it or, without one, as real inferred it.</item>
    /// <item><c>@newname</c> is used verbatim as the new leaf — real does not
    /// parse it as a multi-part name.</item>
    /// </list>
    /// <para>
    /// Other <c>@objtype</c> values (USERDATATYPE / STATISTICS / DATABASE / …)
    /// raise <see cref="NotSupportedException"/> naming the unmodeled type — a
    /// divergence from real, which distinguishes Msg 15248 (recognized but not
    /// found) from Msg 15249 (unrecognized) for those. #temp tables aren't
    /// special-cased: their DB-scoped resolution miss surfaces Msg 15225, which
    /// matches real (real resolves <c>@objname</c> in the current database, so a
    /// bare <c>#t</c> isn't found either).
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpRename(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        // Ahead of every resolution: real reports Msg 3930 for an sp_rename of
        // a missing object too, where the read-only gate in each Rename* helper
        // below yields to the not-found error (both probe-confirmed).
        RejectWriteInDoomedTransaction(batch.Connection);

        var (objName, newName, objType) = ParseSpRenameArgs(arguments);

        // @objname / @newname are mandatory. Real raises Msg 201 on a missing
        // one; the simulator surfaces the generic invalid-parameters error —
        // schema-migration callers always pass both.
        if (string.IsNullOrEmpty(objName) || string.IsNullOrEmpty(newName))
            throw SimulatedSqlException.InvalidProcedureParameters("sp_rename");

        try
        {
            // Without @objtype a table.leaf name no object answers renames the
            // column, else the index, it names, and the collision message
            // names the kind in lower case where a passed @objtype is echoed
            // as written (probed 2026-09-30 against SQL Server 2025).
            var inferred = objType is null ? InferredSubobjectKind(batch, objName) : null;
            if (inferred == "column")
            {
                RenameColumn(batch, objName, newName, inferred);
                RecordRenameEvent(batch, objName, "COLUMN");
            }
            else if (inferred == "index")
            {
                RenameIndex(batch, objName, newName, inferred);
                RecordRenameEvent(batch, objName, "INDEX");
            }
            else if (objType is null || BuiltInToken.Equals(objType, "OBJECT"))
            {
                var renamedKind = RenameObject(batch, objName, newName, objType);
                RecordRenameEvent(batch, objName, renamedKind);
            }
            else if (BuiltInToken.Equals(objType, "COLUMN"))
            {
                RenameColumn(batch, objName, newName, objType);
                RecordRenameEvent(batch, objName, "COLUMN");
            }
            else if (BuiltInToken.Equals(objType, "INDEX"))
            {
                RenameIndex(batch, objName, newName, objType);
                RecordRenameEvent(batch, objName, "INDEX");
            }
            else
            {
                throw new NotSupportedException(
                    $"sp_rename with @objtype '{objType}' is not modeled; supported @objtype values are COLUMN, INDEX, and a table / object rename (NULL @objtype).");
            }
        }
        catch (SimulatedSqlException refused) when (refused.Number is 3906 or 4928 or 5074)
        {
            // Real cautions before it finds the database read-only, a filter
            // reading the column or the column computed, so the caution
            // precedes the Msg 3906, 5074 or 4928 (probed 2026-09-25 and
            // 2026-09-30).
            QueueRenameCaution(batch);
            throw;
        }

        QueueRenameCaution(batch);
        yield break;
    }

    /// <summary>
    /// Raises the <c>RENAME</c> DDL event for a completed <c>sp_rename</c>.
    /// Real reports the <em>old</em> name as <c>ObjectName</c> (probe-confirmed)
    /// alongside a <c>NewObjectName</c> element the simulator doesn't emit.
    /// </summary>
    /// <remarks>
    /// A column or index names its table as <c>TargetObjectName</c> /
    /// <c>TargetObjectType</c> under the table's schema, and an object's
    /// rename carries the pair empty (probed 2026-10-04 against SQL Server
    /// 2025).
    /// </remarks>
    private static void RecordRenameEvent(BatchContext batch, string objName, string objectType)
    {
        var parts = objName.Split('.');
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim('[', ']');
        var leaf = parts[^1];
        if (objectType is "COLUMN" or "INDEX" && parts.Length >= 2)
        {
            var columnSchema = parts.Length >= 3 ? parts[^3] : Database.DefaultSchemaName;
            RecordDdlEvent(batch.Parser, "RENAME", columnSchema, leaf, objectType, parts[^2], "TABLE");
            return;
        }
        var schemaName = parts.Length >= 2 ? parts[^2] : Database.DefaultSchemaName;
        RecordDdlEvent(batch.Parser, "RENAME", schemaName, leaf, objectType, targetObjectName: "", targetObjectType: "");
    }

    private static (string? ObjName, string? NewName, string? ObjType) ParseSpRenameArgs(List<ProcArgument> arguments)
    {
        string? objName = null, newName = null, objType = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: objName = CatalogStringArg(arg); break;
                    case 1: newName = CatalogStringArg(arg); break;
                    case 2: objType = CatalogStringArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_rename");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "objname"): objName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "newname"): newName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "objtype"): objType = CatalogStringArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_rename");
            }
        }

        return (objName, newName, objType);
    }

    /// <summary>
    /// Renames any object in a schema's shared namespace — a table, view,
    /// procedure, function, sequence, synonym or DML trigger — or a constraint
    /// on one of its tables, the NULL / <c>OBJECT</c> <c>@objtype</c> forms
    /// (probed 2026-09-25 against SQL Server 2025). The object's
    /// <c>modify_date</c> advances; a module's stored definition keeps the
    /// name it was created with, as real's does. Returns
    /// the RENAME event's object type. Nothing by the name is Msg 15225 with
    /// the NULL type and Msg 15248 naming <c>OBJECT</c>.
    /// </summary>
    private string RenameObject(BatchContext batch, string objName, string newName, string? objType)
    {
        var database = batch.CurrentDatabase;
        SimulatedSqlException NotFound() => objType is null
            ? SimulatedSqlException.RenameItemNotFound(objName, database.Name, "(null)")
            : SimulatedSqlException.RenameAmbiguousOrWrongType("OBJECT");
        if (!ObjectId.TryParseObjectName(objName, out var name) || !batch.TryResolveSchema(name, out var schema))
            throw NotFound();

        if (schema.TryFindInSharedNamespace(name.Leaf, out var found))
        {
            // sp_rename is gated on ALTER of the object (schema ALTER / object
            // CONTROL cover it) and reports the same not-found record a missing
            // object earns — probe-confirmed, so nothing about the object's
            // existence leaks.
            // An object the caller can't see reads as missing; one it sees
            // without ALTER is Msg 297 (probed 2026-10-04 against SQL Server
            // 2025).
            if (PermissionEnforcement.ObjectVisibility(batch, database) is { } visible && !visible(found))
                throw NotFound();
            if (!PermissionEnforcement.HasObjectAlter(batch, database, found.ObjectId, found.SchemaId))
                throw SimulatedSqlException.RenameNotPermitted();
            // Collision is against the whole shared object namespace
            // (probe-confirmed: renaming a table onto a view name also raises
            // Msg 15335 "as a object").
            // A new name differing from the old in case alone is the object
            // itself, which real renames (probed 2026-09-30 against SQL
            // Server 2025: 'r' to 'R').
            if (schema.TryFindInSharedNamespace(newName, out var clash) && !ReferenceEquals(clash, found))
                throw SimulatedSqlException.RenameDuplicateName(newName, objType ?? "object");
            // A schema-bound module's reference is by name, so real refuses to
            // rename out from under one — Msg 15336, echoing @objname as passed.
            if (SchemaBinding.FindReferencingModule(database, found) is not null)
                throw SimulatedSqlException.RenameParticipatesInEnforcedDependencies(objName, column: false);

            // Real refuses a read-only database only once the rename has
            // otherwise been accepted: an unresolvable @objname still reports
            // its own Msg 15225 / 15248 (probe-confirmed).
            database.RejectWriteWhenReadOnly();
            var kind = found switch
            {
                HeapTable table => Move(schema.HeapTables, table, "TABLE"),
                View view => Move(schema.Views, view, "VIEW"),
                Procedure procedure => Move(schema.Procedures, procedure, "PROCEDURE"),
                UserDefinedFunction function => Move(schema.Functions, function, "FUNCTION"),
                Sequence sequence => Move(schema.Sequences, sequence, "SEQUENCE"),
                Synonym synonym => Move(schema.Synonyms, synonym, "SYNONYM"),
                Trigger trigger => Move(schema.Triggers, trigger, "TRIGGER"),
                DefaultObject bindableDefault => Move(schema.Defaults, bindableDefault, "DEFAULT"),
                RuleObject rule => Move(schema.Rules, rule, "RULE"),
                _ => throw NotFound(),
            };
            BumpSchemaVersion();
            return kind;

            string Move<T>(System.Collections.Concurrent.ConcurrentDictionary<string, T> objects, T renamed, string eventType)
                where T : SchemaObject
            {
                if (renamed is HeapTable table)
                    batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
                var (oldName, oldModifyDate) = (renamed.Name, renamed.ModifyDate);
                _ = objects.TryRemove(renamed.Name, out _);
                renamed.Name = newName;
                renamed.ModifyDate = batch.CurrentStatement.UtcNow;
                objects[newName] = renamed;
                // A procedure group's numbered members carry its name.
                if (renamed is Procedure { Numbered: { } numbered })
                {
                    foreach (var member in numbered.Values)
                        member.Name = newName;
                }
                RecordDdlUndo(batch, () =>
                {
                    _ = objects.TryRemove(newName, out _);
                    (renamed.Name, renamed.ModifyDate) = (oldName, oldModifyDate);
                    objects[oldName] = renamed;
                    if (renamed is Procedure { Numbered: { } renamedMembers })
                    {
                        foreach (var member in renamedMembers.Values)
                            member.Name = oldName;
                    }
                });
                return eventType;
            }
        }

        // A constraint shares the namespace but lives on its table.
        var collation = database.Collation;
        foreach (var (_, table) in schema.HeapTables)
        {
            var eventType = RenameConstraintOn(table);
            if (eventType is null)
                continue;
            BumpSchemaVersion();
            return eventType;
        }
        throw NotFound();

        string? RenameConstraintOn(HeapTable table)
        {
            foreach (var key in table.KeyConstraints)
            {
                if (collation.Equals(key.Name, name.Leaf))
                    return RenameConstraint(table, now => (key.Name, key.ModifyDate) = (newName, now), key.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY CONSTRAINT" : "UNIQUE KEY CONSTRAINT");
            }
            foreach (var check in table.CheckConstraints)
            {
                if (collation.Equals(check.Name, name.Leaf))
                    return RenameConstraint(table, now => (check.Name, check.ModifyDate) = (newName, now), "CHECK CONSTRAINT");
            }
            foreach (var foreignKey in table.OutgoingForeignKeys)
            {
                if (collation.Equals(foreignKey.Name, name.Leaf))
                    return RenameConstraint(table, now => (foreignKey.Name, foreignKey.ModifyDate) = (newName, now), "FOREIGN KEY CONSTRAINT");
            }
            foreach (var column in table.Columns)
            {
                if (column.DefaultConstraint is { } defaultConstraint && collation.Equals(defaultConstraint.Name, name.Leaf))
                    return RenameConstraint(table, now => (defaultConstraint.Name, defaultConstraint.ModifyDate) = (newName, now), "DEFAULT");
            }
            return null;
        }

        string RenameConstraint(HeapTable table, Action<DateTime> rename, string eventType)
        {
            if (!PermissionEnforcement.HasObjectAlter(batch, database, table.ObjectId, table.SchemaId))
                throw NotFound();
            if (schema.HasNameInSharedNamespace(newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, objType ?? "object");
            database.RejectWriteWhenReadOnly();
            batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
            RecordTableDdlUndo(batch, table);
            rename(batch.CurrentStatement.UtcNow);
            return eventType;
        }
    }

    private void RenameColumn(BatchContext batch, string objName, string newName, string kind)
    {
        if (!TrySplitTableAndLeaf(objName, out var tableName, out var columnName)
            || !batch.TryResolveTable(tableName, out var table))
        {
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("COLUMN");
        }

        var collation = batch.CurrentDatabase.Collation;
        var ordinal = -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
            {
                ordinal = i;
                break;
            }
        }
        if (ordinal < 0)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("COLUMN");

        // The column itself is no clash, so a change of case alone renames
        // (probed 2026-09-30 against SQL Server 2025).
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (i != ordinal && collation.Equals(table.Columns[i].Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind);
        }

        // Column-granular binding by name: renaming a column no schema-bound
        // module, computed column or CHECK constraint reads is allowed, and
        // renaming one that any of them reads is Msg 15336 — a column-level
        // CHECK and a persisted computed column included, while a DEFAULT, an
        // index key and either end of a foreign key rename freely (probed
        // 2026-09-30 against SQL Server 2025).
        var renamed = table.Columns[ordinal];
        if (SchemaBinding.ColumnReferencingModules(batch.CurrentDatabase, table, columnName).Count > 0
            || Array.Exists(table.Columns, column => column.Computed is { } computed && ComputedReferencesColumn(collation, computed, renamed.Name))
            || table.CheckConstraints.Exists(check => (check.InlineColumn is { } inline && collation.Equals(inline, renamed.Name)) || CheckPredicateReferencesColumn(collation, check.Predicate, renamed.Name)))
        {
            throw SimulatedSqlException.RenameParticipatesInEnforcedDependencies(objName, column: true);
        }

        // A computed column itself can't be renamed: Msg 4928 from line 905
        // (probed 2026-09-30 against SQL Server 2025).
        if (renamed.Computed is not null)
            throw SimulatedSqlException.CannotAlterColumnOfKind(renamed.Name, "COMPUTED");

        // A filter names its columns, so a column a filtered index or
        // statistic's predicate reads can't be renamed: Msg 5074 for each,
        // then Msg 4922, from line 905 of sp_rename (probed 2026-09-30
        // against SQL Server 2025).
        if (FilterDependents(batch.CurrentDatabase, table, ordinal) is { Count: > 0 } dependents)
        {
            var refusal = SimulatedSqlException.ColumnHasDependencies("RENAME COLUMN", table.Columns[ordinal].Name, dependents);
            refusal.PreserveDiagnostics(905, "sp_rename");
            throw refusal;
        }

        // Storage is by ordinal, so the name change needs no row re-encode — but
        // the schema-version bump invalidates any cached plan that resolved the
        // old name.
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        RecordTableDdlUndo(batch, table);
        // A system-versioned table's history column takes the new name too
        // (probed 2026-10-04 against SQL Server 2025).
        if (table.SystemVersioning is { } history
            && Array.FindIndex(history.Columns, column => collation.Equals(column.Name, renamed.Name)) is >= 0 and var historyOrdinal)
        {
            RecordTableDdlUndo(batch, history);
            history.Columns[historyOrdinal].Name = newName;
        }
        table.Columns[ordinal].Name = newName;
        BumpSchemaVersion();
    }

    private void RenameIndex(BatchContext batch, string objName, string newName, string kind)
    {
        if (!TrySplitTableAndLeaf(objName, out var tableName, out var indexName)
            || !batch.TryResolveTable(tableName, out var table))
        {
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX");
        }

        var collation = batch.CurrentDatabase.Collation;
        Storage.Index? target = null;
        foreach (var index in table.Indexes)
        {
            // The index itself is no clash: a change of case alone renames
            // (probed 2026-09-30 against SQL Server 2025).
            if (collation.Equals(index.Name, newName) && !collation.Equals(index.Name, indexName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind);
            if (collation.Equals(index.Name, indexName))
                target = index;
        }
        // A JSON index renames the same way (probed 2026-09-27 against SQL
        // Server 2025).
        Schemas.JsonIndex? jsonTarget = null;
        foreach (var jsonIndex in table.JsonIndexes)
        {
            if (collation.Equals(jsonIndex.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind);
            if (collation.Equals(jsonIndex.Name, indexName))
                jsonTarget = jsonIndex;
        }
        Schemas.VectorIndex? vectorTarget = null;
        foreach (var vectorIndex in table.VectorIndexes)
        {
            if (collation.Equals(vectorIndex.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind);
            if (collation.Equals(vectorIndex.Name, indexName))
                vectorTarget = vectorIndex;
        }
        if (target is null && jsonTarget is null && vectorTarget is null)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX");
        // A vector index keeps its name (probed 2026-09-29 against SQL Server
        // 2025).
        if (vectorTarget is not null)
        {
            QueueRenameCaution(batch);
            throw SimulatedSqlException.VectorIndexRename();
        }

        table.OwningDatabase?.RejectWriteWhenReadOnly();
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        RecordTableDdlUndo(batch, table);
        if (target is not null)
            target.Name = newName;
        else
            jsonTarget!.Name = newName;
        BumpSchemaVersion();
    }

    /// <summary>
    /// What a NULL-<c>@objtype</c> <c>sp_rename</c> of <paramref name="objName"/>
    /// renames when no object in the shared namespace answers the name:
    /// <c>"column"</c> or <c>"index"</c> for a table.leaf whose table has one
    /// by that leaf, the column first, else null for the object path.
    /// </summary>
    private static string? InferredSubobjectKind(BatchContext batch, string objName)
    {
        if (ObjectId.TryParseObjectName(objName, out var name) && batch.TryResolveSchema(name, out var schema) && schema.TryFindInSharedNamespace(name.Leaf, out _))
            return null;
        if (!TrySplitTableAndLeaf(objName, out var tableName, out var leaf) || !batch.TryResolveTable(tableName, out var table))
            return null;
        var collation = batch.CurrentDatabase.Collation;
        if (Array.Exists(table.Columns, column => collation.Equals(column.Name, leaf)))
            return "column";
        return table.Indexes.Exists(index => collation.Equals(index.Name, leaf))
            || table.JsonIndexes.Exists(index => collation.Equals(index.Name, leaf))
            || table.VectorIndexes.Exists(index => collation.Equals(index.Name, leaf))
            ? "index"
            : null;
    }

    /// <summary>
    /// Splits a <c>[db.][schema.]table.leaf</c> object name into the parent
    /// table's <see cref="MultiPartName"/> and the trailing column / index leaf.
    /// Returns false for a bare 1-part name (no parent table to resolve against).
    /// </summary>
    private static bool TrySplitTableAndLeaf(string objName, out MultiPartName tableName, out string leaf)
    {
        tableName = default;
        leaf = "";
        if (!ObjectId.TryParseObjectName(objName, out var full) || full.Count < 2)
            return false;

        leaf = full.Leaf;
        tableName = new MultiPartName(full[0]);
        for (var i = 1; i < full.Count - 1; i++)
            tableName = tableName.WithAddedPart(full[i]);
        return true;
    }
}
