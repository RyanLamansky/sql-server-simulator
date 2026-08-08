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
    /// resolved leaf moves within its schema. A missing object → Msg 15225 with
    /// <c>@itemtype</c> rendered as <c>(null)</c>.</item>
    /// <item><c>@objtype</c> = COLUMN / INDEX (case-insensitive) renames a column
    /// / index of <c>[schema.]table.leaf</c>. A missing parent table or leaf →
    /// Msg 15248 ("ambiguous or the claimed @objtype is wrong").</item>
    /// <item>A colliding <c>@newname</c> → Msg 15335; the substituted kind is
    /// COLUMN / INDEX for those paths and the ungrammatical <c>object</c> for the
    /// table path (matched verbatim).</item>
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

        if (objType is null)
        {
            RenameTable(batch, objName, newName);
            RecordRenameEvent(batch, objName, "TABLE");
        }
        else if (BuiltInToken.Equals(objType, "COLUMN"))
        {
            RenameColumn(batch, objName, newName);
            RecordRenameEvent(batch, objName, "COLUMN");
        }
        else if (BuiltInToken.Equals(objType, "INDEX"))
        {
            RenameIndex(batch, objName, newName);
            RecordRenameEvent(batch, objName, "INDEX");
        }
        else
        {
            throw new NotSupportedException(
                $"sp_rename with @objtype '{objType}' is not modeled; supported @objtype values are COLUMN, INDEX, and a table / object rename (NULL @objtype).");
        }

        batch.AppendInfoError(@class: 10, state: 1, number: 15477, message: RenameCautionMessage);
        yield break;
    }

    /// <summary>
    /// Raises the <c>RENAME</c> DDL event for a completed <c>sp_rename</c>.
    /// Real reports the <em>old</em> name as <c>ObjectName</c> (probe-confirmed)
    /// alongside a <c>NewObjectName</c> element the simulator doesn't emit.
    /// </summary>
    private static void RecordRenameEvent(BatchContext batch, string objName, string objectType)
    {
        var dot = objName.LastIndexOf('.');
        var schemaName = dot < 0 ? Database.DefaultSchemaName : objName[..dot].Trim('[', ']');
        var leaf = objName[(dot + 1)..].Trim('[', ']');
        RecordDdlEvent(batch.Parser, "RENAME", schemaName, leaf, objectType);
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

    private void RenameTable(BatchContext batch, string objName, string newName)
    {
        var database = batch.CurrentDatabase;
        if (!ObjectId.TryParseObjectName(objName, out var name)
            || !batch.TryResolveSchema(name, out var schema)
            || !schema.HeapTables.TryGetValue(name.Leaf, out var table))
        {
            throw SimulatedSqlException.RenameItemNotFound(objName, database.Name, "(null)");
        }

        // sp_rename is gated on ALTER of the object (schema ALTER / object
        // CONTROL cover it) and reports the same Msg 15225 not-found record a
        // missing object earns — probe-confirmed, so nothing about the object's
        // existence leaks.
        if (!PermissionEnforcement.HasObjectAlter(batch, database, table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.RenameItemNotFound(objName, database.Name, "(null)");

        // Collision is against the whole shared object namespace (probe-confirmed:
        // renaming a table onto a view name also raises Msg 15335 "as a object").
        if (schema.HasNameInSharedNamespace(newName))
            throw SimulatedSqlException.RenameDuplicateName(newName, "object");

        // A schema-bound module's reference is by name, so real refuses to
        // rename out from under one — Msg 15336, echoing @objname as passed.
        if (SchemaBinding.FindReferencingModule(database, table) is not null)
            throw SimulatedSqlException.RenameParticipatesInEnforcedDependencies(objName);

        // Real refuses a read-only database only once the rename has otherwise
        // been accepted: an unresolvable @objname still reports its own Msg
        // 15225 / 15248 (probe-confirmed).
        database.RejectWriteWhenReadOnly();
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        _ = schema.HeapTables.TryRemove(table.Name, out _);
        table.Name = newName;
        schema.HeapTables[newName] = table;
        BumpSchemaVersion();
    }

    private void RenameColumn(BatchContext batch, string objName, string newName)
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

        foreach (var column in table.Columns)
        {
            if (collation.Equals(column.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, "COLUMN");
        }

        // Column-granular schema binding: renaming a column no schema-bound
        // module reads is allowed, renaming one that is read is Msg 15336
        // (both probe-confirmed).
        if (SchemaBinding.ColumnReferencingModuleNames(batch.CurrentDatabase, table, columnName).Count > 0)
            throw SimulatedSqlException.RenameParticipatesInEnforcedDependencies(objName);

        // Storage is by ordinal, so the name change needs no row re-encode — but
        // the schema-version bump invalidates any cached plan that resolved the
        // old name.
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        table.Columns[ordinal].Name = newName;
        BumpSchemaVersion();
    }

    private void RenameIndex(BatchContext batch, string objName, string newName)
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
            if (collation.Equals(index.Name, newName))
                throw SimulatedSqlException.RenameDuplicateName(newName, "INDEX");
            if (collation.Equals(index.Name, indexName))
                target = index;
        }
        if (target is null)
            throw SimulatedSqlException.RenameAmbiguousOrWrongType("INDEX");

        table.OwningDatabase?.RejectWriteWhenReadOnly();
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        target.Name = newName;
        BumpSchemaVersion();
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
