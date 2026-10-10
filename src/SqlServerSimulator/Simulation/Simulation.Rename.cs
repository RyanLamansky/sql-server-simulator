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
    // line and the procedure as the call named it, whatever was renamed
    // (probed 2026-09-26 and 2026-10-04).
    private static void QueueRenameCaution(BatchContext batch, string procedureName)
    {
        var caution = batch.InfoMessage(@class: 10, state: 1, number: 15477, message: RenameCautionMessage);
        caution.LineNumber = 801;
        caution.Procedure = procedureName;
        batch.Connection.PendingMessages.Enqueue(caution);
    }

    // sp_rename's parameters, as sys.all_parameters declares them: @objname
    // nvarchar(1035), @newname sysname, @objtype varchar(13). A longer value is
    // cut to the parameter's width as any procedure argument is.
    private static readonly string[] RenameParameterNames = ["objname", "newname", "objtype"];

    /// <summary>
    /// Handles <c>EXEC sp_rename @objname, @newname [, @objtype]</c> — the
    /// object / column / index / statistics / type / database rename
    /// schema-migration tools (Alembic's <c>rename_table</c> /
    /// <c>alter_column</c>, SSMS, EF Core) emit. Reachable through both
    /// <c>EXEC sp_rename …</c> and the RPC-by-name path (a name-form system proc
    /// is re-synthesized as an <c>EXEC</c>), so a single dispatch arm serves
    /// both.
    /// </summary>
    /// <remarks>
    /// The checks run in real's order (probed 2026-10-04 against SQL Server
    /// 2025): the argument binding, an <c>@objtype</c> outside COLUMN /
    /// DATABASE / INDEX / OBJECT / STATISTICS / USERDATATYPE (Msg 15249), a
    /// NULL <c>@newname</c> then a NULL <c>@objname</c> (Msg 15223), an empty
    /// <c>@newname</c> (sp_validname's Msg 15004, then Msg 15224), and an
    /// <c>@objname</c> that doesn't parse as a name of at most four parts
    /// (Msg 15253) — each at its own line of real's source. Success sends the
    /// Msg 15477 caution and leaves <c>@@ROWCOUNT</c> at 1, as real's last
    /// statement does; a rename inside a transaction rolls back with it.
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpRename(BatchContext batch, string procedureName)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        RequireSystemProcedureShape("sp_rename", arguments, RenameParameterNames, required: 2);

        // Ahead of every resolution: real reports Msg 3930 for an sp_rename of
        // a missing object too, where the read-only gate in each Rename* helper
        // below yields to the not-found error (both probe-confirmed).
        RejectWriteInDoomedTransaction(batch.Connection);

        var (objName, newName, objType) = ParseSpRenameArgs(arguments);
        var kind = objType?.TrimEnd(' ');
        if (kind is not null && !BuiltInToken.EqualsAny(kind, "COLUMN", "DATABASE", "INDEX", "OBJECT", "STATISTICS", "USERDATATYPE"))
            throw SimulatedSqlException.RenameObjectTypeUnrecognized(objType!).AtSystemProcedureLine(90);
        if (newName is null)
            throw SimulatedSqlException.RenameParameterIsNull("NewName", state: 11).AtSystemProcedureLine(96);
        if (objName is null)
            throw SimulatedSqlException.RenameParameterIsNull("OldName", state: 1).AtSystemProcedureLine(101);
        if (newName.Length == 0)
        {
            // sp_rename asks sp_validname, whose own refusal arrives first.
            throw SimulatedSqlException.RenameNewNameInvalid(newName, procedureName);
        }

        if (kind is not null && BuiltInToken.Equals(kind, "DATABASE"))
        {
            RenameDatabase(batch, objName, newName);
            batch.Connection.LastStatementRowCount = 1;
            yield break;
        }

        if (!ObjectId.TryParseObjectName(objName, out _))
            throw SimulatedSqlException.RenameIdentifierSyntax(objName).AtSystemProcedureLine(120);

        try
        {
            // Without @objtype a table.leaf name no object answers renames the
            // column, else the index, it names, and a name no object or
            // subobject answers renames the user type it names; the collision
            // message names the kind in lower case where a passed @objtype is
            // echoed as written (probed 2026-09-30 against SQL Server 2025).
            var inferred = kind is null ? InferredSubobjectKind(batch, objName) : null;
            if (inferred == "column" || (kind is not null && BuiltInToken.Equals(kind, "COLUMN")))
            {
                RenameColumn(batch, objName, newName, inferred ?? objType!);
                RecordRenameEvent(batch, objName, "COLUMN");
            }
            else if (inferred == "index" || (kind is not null && BuiltInToken.Equals(kind, "INDEX")))
            {
                RenameIndex(batch, objName, newName, inferred ?? objType!, procedureName);
                RecordRenameEvent(batch, objName, "INDEX");
            }
            else if (inferred == "userdatatype" || (kind is not null && BuiltInToken.Equals(kind, "USERDATATYPE")))
            {
                this.RenameUserType(batch, objName, newName, procedureName);
            }
            else if (kind is not null && BuiltInToken.Equals(kind, "STATISTICS"))
            {
                RenameStatistics(batch, objName, newName, procedureName);
                RecordRenameEvent(batch, objName, "STATISTICS");
            }
            else
            {
                var renamedKind = this.RenameObject(batch, objName, newName, objType);
                RecordRenameEvent(batch, objName, renamedKind);
            }
        }
        catch (SimulatedSqlException refused) when (refused.Number is 3906 or 4928 or 5074)
        {
            // Real cautions before it finds the database read-only, a filter
            // reading the column or the column computed, so the caution
            // precedes the Msg 3906, 5074 or 4928 (probed 2026-09-25 and
            // 2026-09-30).
            QueueRenameCaution(batch, procedureName);
            throw;
        }

        QueueRenameCaution(batch, procedureName);
        batch.Connection.LastStatementRowCount = 1;
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
            var slot = arg.Name is { } name
                ? Array.FindIndex(RenameParameterNames, parameter => BuiltInToken.Equals(name, parameter))
                : positional++;
            var value = CatalogStringArg(arg);
            switch (slot)
            {
                case 0: objName = Truncated(value, 1035); break;
                case 1: newName = Truncated(value, 128); break;
                default: objType = Truncated(value, 13); break;
            }
        }

        return (objName, newName, objType);

        static string? Truncated(string? value, int width) => value is { } text && text.Length > width ? text[..width] : value;
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
            : SimulatedSqlException.RenameAmbiguousOrWrongType("OBJECT").AtSystemProcedureLine(620);
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
                SecurityPolicy policy => Move(schema.SecurityPolicies, policy, "SECURITY POLICY"),
                _ => throw NotFound(),
            };
            BumpSchemaVersion();
            return kind;

            string Move<T>(System.Collections.Concurrent.ConcurrentDictionary<string, T> objects, T renamed, string eventType)
                where T : SchemaObject
            {
                // The rename holds both names and the object to the
                // transaction's end (probed 2026-10-10 against SQL Server 2025:
                // a reference to the old name waits on the name, one to the
                // new name on the object).
                batch.LockDefinitionName(schema, renamed.Name, abortsTransaction: false);
                batch.LockDefinitionName(schema, newName, DefinitionNameUse.Creates, abortsTransaction: false);
                if (renamed is HeapTable table)
                {
                    batch.AcquireTableRedefinitionLock(table);
                    batch.NoteDefinedObject(schema, table.Name, table.ObjectId, prior: true);
                }
                else
                {
                    batch.LockDefinition(schema, renamed, abortsTransaction: false);
                }
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
            || !TryResolveRenameParent(batch, tableName, out var table))
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

    private void RenameIndex(BatchContext batch, string objName, string newName, string kind, string procedureName)
    {
        if (!TrySplitTableAndLeaf(objName, out var tableName, out var indexName))
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX").AtSystemProcedureLine(450);
        // An indexed view's index renames as a table's does.
        if (TryResolveRenameView(batch, tableName, out var view))
        {
            this.RenameViewIndex(batch, view, indexName, newName, kind);
            return;
        }
        if (!TryResolveRenameParent(batch, tableName, out var table))
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX").AtSystemProcedureLine(450);

        var collation = batch.CurrentDatabase.Collation;
        // A PRIMARY KEY or UNIQUE constraint's index is the constraint: the
        // rename renames both (probed 2026-10-04 against SQL Server 2025).
        KeyConstraint? keyTarget = null;
        foreach (var key in table.KeyConstraints)
        {
            if (collation.Equals(key.Name, newName) && !collation.Equals(key.Name, indexName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind).AtSystemProcedureLine(738);
            if (collation.Equals(key.Name, indexName))
                keyTarget = key;
        }
        // A statistic's name is taken too (probed 2026-10-04).
        foreach (var statistic in table.UserStatistics)
        {
            if (collation.Equals(statistic.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind).AtSystemProcedureLine(738);
        }
        Storage.Index? target = null;
        foreach (var index in table.Indexes)
        {
            // The index itself is no clash: a change of case alone renames
            // (probed 2026-09-30 against SQL Server 2025).
            if (collation.Equals(index.Name, newName) && !collation.Equals(index.Name, indexName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind).AtSystemProcedureLine(738);
            if (collation.Equals(index.Name, indexName))
                target = index;
        }
        if (keyTarget is not null)
        {
            table.OwningDatabase?.RejectWriteWhenReadOnly();
            batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
            RecordTableDdlUndo(batch, table);
            (keyTarget.Name, keyTarget.ModifyDate) = (newName, batch.CurrentStatement.UtcNow);
            _ = target?.Name = newName;
            this.BumpSchemaVersion();
            return;
        }
        // A JSON index renames the same way (probed 2026-09-27 against SQL
        // Server 2025).
        Schemas.JsonIndex? jsonTarget = null;
        foreach (var jsonIndex in table.JsonIndexes)
        {
            if (collation.Equals(jsonIndex.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind).AtSystemProcedureLine(738);
            if (collation.Equals(jsonIndex.Name, indexName))
                jsonTarget = jsonIndex;
        }
        Schemas.VectorIndex? vectorTarget = null;
        foreach (var vectorIndex in table.VectorIndexes)
        {
            if (collation.Equals(vectorIndex.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind).AtSystemProcedureLine(738);
            if (collation.Equals(vectorIndex.Name, indexName))
                vectorTarget = vectorIndex;
        }
        if (target is null && jsonTarget is null && vectorTarget is null)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX").AtSystemProcedureLine(450);
        // A vector index keeps its name (probed 2026-09-29 against SQL Server
        // 2025).
        if (vectorTarget is not null)
        {
            QueueRenameCaution(batch, procedureName);
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
    /// Resolves the table a column, index or statistics rename names, as
    /// real's sp_rename sees it: a <c>#temp</c> table only from tempdb, and a
    /// table the caller can't see as missing, while one it sees without
    /// <c>ALTER</c> is Msg 297 from line 242 (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    private static bool TryResolveRenameParent(BatchContext batch, MultiPartName tableName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapTable? table)
    {
        table = null;
        var database = batch.CurrentDatabase;
        if (BatchContext.IsLocalTempName(tableName.Leaf) && !database.Collation.Equals(database.Name, TempdbDatabaseName))
            return false;
        if (!batch.TryResolveTable(tableName, out var resolved))
            return false;
        if (resolved.OwningDatabase is { } owner && owner == database && !resolved.IsTableVariable && !BatchContext.IsLocalTempName(resolved.Name))
        {
            if (PermissionEnforcement.ObjectVisibility(batch, database) is { } visible && !visible(resolved))
                return false;
            if (!PermissionEnforcement.HasObjectAlter(batch, database, resolved.ObjectId, resolved.SchemaId))
                throw SimulatedSqlException.RenameNotPermitted().AtSystemProcedureLine(242);
        }
        table = resolved;
        return true;
    }

    /// <summary>The view an index rename's parent name resolves to, when it is one.</summary>
    private static bool TryResolveRenameView(BatchContext batch, MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        if (!batch.TryResolveSchema(name, out var schema) || !schema.Views.TryGetValue(name.Leaf, out var found))
            return false;
        var database = batch.CurrentDatabase;
        if (PermissionEnforcement.ObjectVisibility(batch, database) is { } visible && !visible(found))
            return false;
        if (!PermissionEnforcement.HasObjectAlter(batch, database, found.ObjectId, found.SchemaId))
            throw SimulatedSqlException.RenameNotPermitted().AtSystemProcedureLine(242);
        view = found;
        return true;
    }

    private void RenameViewIndex(BatchContext batch, View view, string indexName, string newName, string kind)
    {
        var collation = batch.CurrentDatabase.Collation;
        Storage.Index? target = null;
        foreach (var index in view.Indexes)
        {
            if (collation.Equals(index.Name, newName) && !collation.Equals(index.Name, indexName))
                throw SimulatedSqlException.RenameDuplicateName(newName, kind).AtSystemProcedureLine(738);
            if (collation.Equals(index.Name, indexName))
                target = index;
        }
        if (target is null)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX").AtSystemProcedureLine(450);
        batch.CurrentDatabase.RejectWriteWhenReadOnly();
        var oldName = target.Name;
        target.Name = newName;
        RecordDdlUndo(batch, () => target.Name = oldName);
        this.BumpSchemaVersion();
    }

    /// <summary>
    /// The <c>STATISTICS</c> form: renames a statistic of
    /// <c>[schema.]table.name</c> — a <c>CREATE STATISTICS</c> one, or an
    /// index's, which renames the index — with Msg 15248 from line 481 when
    /// nothing answers and, after the caution, Msg 15335 from line 882 for a
    /// name another statistic or index holds (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    private void RenameStatistics(BatchContext batch, string objName, string newName, string procedureName)
    {
        if (!TrySplitTableAndLeaf(objName, out var tableName, out var statisticName)
            || !TryResolveRenameParent(batch, tableName, out var table))
        {
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("STATISTICS").AtSystemProcedureLine(481);
        }
        var collation = batch.CurrentDatabase.Collation;
        var statistic = table.UserStatistics.Find(candidate => collation.Equals(candidate.Name, statisticName));
        var index = table.Indexes.Find(candidate => collation.Equals(candidate.Name, statisticName));
        var key = table.KeyConstraints.Find(candidate => collation.Equals(candidate.Name, statisticName));
        if (statistic is null && index is null && key is null)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("STATISTICS").AtSystemProcedureLine(481);
        var taken = table.UserStatistics.Exists(candidate => candidate != statistic && collation.Equals(candidate.Name, newName))
            || table.Indexes.Exists(candidate => candidate != index && collation.Equals(candidate.Name, newName))
            || table.KeyConstraints.Exists(candidate => candidate != key && collation.Equals(candidate.Name, newName));
        if (taken)
        {
            QueueRenameCaution(batch, procedureName);
            throw SimulatedSqlException.RenameDuplicateName(newName, "STATISTICS").AtSystemProcedureLine(882);
        }
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        RecordTableDdlUndo(batch, table);
        _ = statistic?.Name = newName;
        _ = index?.Name = newName;
        if (key is not null)
            (key.Name, key.ModifyDate) = (newName, batch.CurrentStatement.UtcNow);
        this.BumpSchemaVersion();
    }

    /// <summary>
    /// The <c>USERDATATYPE</c> form, and a NULL-<c>@objtype</c> name only a
    /// type answers: renames an alias or table type, which every column,
    /// parameter and variable declared with it then reports under the new
    /// name. A system type is Msg 4185 from line 205, nothing by the name Msg
    /// 15248 from line 215, and, after the caution, a name another type holds
    /// Msg 15335 from line 824 (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private void RenameUserType(BatchContext batch, string objName, string newName, string procedureName)
    {
        if (!ObjectId.TryParseObjectName(objName, out var name) || name.Count > 2)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("USERDATATYPE").AtSystemProcedureLine(215);
        if (name.Count == 1 && SqlType.IsSystemTypeName(name.Leaf))
            throw SimulatedSqlException.ActionNotAllowedOnSystemType().AtSystemProcedureLine(205);
        if (!batch.TryResolveSchema(name, out var schema))
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("USERDATATYPE").AtSystemProcedureLine(215);
        var database = batch.CurrentDatabase;
        if (schema.AliasTypes.TryGetValue(name.Leaf, out var alias))
        {
            if (!PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Alter) && !batch.Connection.Security.EffectiveIsDbo)
                throw SimulatedSqlException.RenameNotPermitted();
            RequireFreeTypeName(alias.Name);
            database.RejectWriteWhenReadOnly();
            var oldName = alias.Name;
            _ = schema.AliasTypes.TryRemove(oldName, out _);
            alias.Name = newName;
            schema.AliasTypes[newName] = alias;
            RecordDdlUndo(batch, () =>
            {
                _ = schema.AliasTypes.TryRemove(newName, out _);
                alias.Name = oldName;
                schema.AliasTypes[oldName] = alias;
            });
        }
        else if (schema.TableTypes.TryGetValue(name.Leaf, out var tableType))
        {
            if (!PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Alter) && !batch.Connection.Security.EffectiveIsDbo)
                throw SimulatedSqlException.RenameNotPermitted();
            RequireFreeTypeName(tableType.Name);
            database.RejectWriteWhenReadOnly();
            var oldName = tableType.Name;
            _ = schema.TableTypes.TryRemove(oldName, out _);
            tableType.Name = newName;
            schema.TableTypes[newName] = tableType;
            RecordDdlUndo(batch, () =>
            {
                _ = schema.TableTypes.TryRemove(newName, out _);
                tableType.Name = oldName;
                schema.TableTypes[oldName] = tableType;
            });
        }
        else
        {
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("USERDATATYPE").AtSystemProcedureLine(215);
        }
        this.BumpSchemaVersion();

        void RequireFreeTypeName(string current)
        {
            if (database.Collation.Equals(current, newName))
                return;
            if (schema.AliasTypes.ContainsKey(newName) || schema.TableTypes.ContainsKey(newName) || schema.XmlSchemaCollections.ContainsKey(newName))
            {
                QueueRenameCaution(batch, procedureName);
                throw SimulatedSqlException.RenameDuplicateName(newName, "USERDATATYPE").AtSystemProcedureLine(824);
            }
        }
    }

    /// <summary>
    /// The <c>DATABASE</c> form, which real hands to <c>sys.sp_renamedb</c>:
    /// the rename <c>ALTER DATABASE … MODIFY NAME</c> makes, with its Msg 5021
    /// notice and no caution, a missing database Msg 15010 from line 29 and a
    /// name another database holds Msg 15032 from line 36, both naming
    /// <c>sys.sp_renamedb</c> (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static void RenameDatabase(BatchContext batch, string databaseName, string newName)
    {
        var databases = batch.Connection.Simulation.Databases;
        if (!databases.TryGetValue(databaseName, out var target))
            throw AtProcedureLine(SimulatedSqlException.HelpDatabaseDoesNotExist(databaseName), "sys.sp_renamedb", 29);
        if (databases.TryGetValue(newName, out var holder) && holder != target)
            throw AtProcedureLine(SimulatedSqlException.DatabaseNameAlreadyExistsForRename(newName), "sys.sp_renamedb", 36);
        if (!PermissionEnforcement.HasDatabasePermission(batch, target, Permission.Alter))
            throw SimulatedSqlException.AlterDatabasePermissionDenied(target.Name);
        RenameDatabaseTo(batch, target, newName);
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
        {
            // A name only a type answers renames the type (probed 2026-10-04
            // against SQL Server 2025).
            return ObjectId.TryParseObjectName(objName, out var typeName) && typeName.Count <= 2 && batch.TryResolveCallerTypeSchema(typeName, out var typeSchema)
                && (typeSchema.AliasTypes.ContainsKey(typeName.Leaf) || typeSchema.TableTypes.ContainsKey(typeName.Leaf))
                ? "userdatatype"
                : null;
        }
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
