using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes a <c>SELECT … INTO target [FROM …]</c> statement: creates
    /// the destination heap table from the parse-time-derived schema (see
    /// <see cref="Selection.DestColumnSchema"/>), then runs the projection
    /// and inserts each row into the new table. Returns a
    /// <see cref="SimulatedNonQuery"/> whose record count is the row count
    /// written (real SQL Server's SELECT INTO doesn't yield a result set
    /// envelope; ExecuteNonQuery returns the row count).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed against SQL Server 2025 (2026-05-11):
    /// </para>
    /// <list type="bullet">
    /// <item>Target name routes by <c>#</c>-prefix to the connection's
    /// <see cref="SimulatedDbConnection.TempTables"/> or the named schema's
    /// (or default <c>dbo</c>'s) heap-table dict via
    /// <see cref="Database.Schemas"/>. Same routing rule as CREATE TABLE.</item>
    /// <item>Target-already-exists raises Msg 2714 (same error as
    /// <c>CREATE TABLE</c> with a duplicate name).</item>
    /// <item>Identity column on the destination tracks the source's values
    /// through <see cref="IdentityState.ObserveExplicit"/> as each row is
    /// copied — so a follow-up plain insert generates the next sequential
    /// value past the highest one copied.</item>
    /// <item>Temp-table targets participate in transactional CREATE undo:
    /// a SELECT INTO #foo inside <c>BEGIN TRAN</c> + <c>ROLLBACK</c>
    /// removes the dest table entirely (same machinery as <c>CREATE TABLE
    /// #foo</c>).</item>
    /// </list>
    /// </remarks>
    private static SimulatedNonQuery ExecuteSelectInto(Selection selection, BatchContext batch)
    {
        var targetName = selection.IntoTarget!.Value;
        var destColumns = selection.DestColumnSchema!;
        var leaf = targetName.Leaf;
        batch.NoteTempTableCreation(leaf);

        // In a skipped IF branch the destination shouldn't be created at all
        // — the existence check (Msg 2714) and the SELECT execution both
        // need to be skipped so a `IF NOT EXISTS (…) SELECT … INTO foo` over
        // an already-existing `foo` doesn't false-positive.
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var isLocalTemp = BatchContext.IsLocalTempName(leaf);
        var isGlobalTemp = !isLocalTemp && BatchContext.IsGlobalTempName(leaf);
        Schema? schema = null;
        var destination = isLocalTemp
            ? batch.Connection.TempTables
            : isGlobalTemp
                ? batch.Connection.Simulation.GlobalTempTables
                : batch.TryResolveSchema(targetName, out schema) ? schema.HeapTables
                    : throw SimulatedSqlException.InvalidObjectName(targetName);
        // A three-part target lands in the named database, so both the object
        // id and the owning-database stamp come from the resolved schema.
        var owningDatabase = schema?.Database;
        // It takes what CREATE TABLE takes — inside a procedure too, which
        // ownership chaining doesn't reach (probed 2026-10-04 against SQL
        // Server 2025: Msg 262).
        if (schema is not null)
        {
            if (!PermissionEnforcement.HasDatabasePermission(batch, schema.Database, "CREATE TABLE"))
                throw SimulatedSqlException.DatabasePermissionDenied("CREATE TABLE", schema.Database.Name);
            if (!PermissionEnforcement.HasSchemaAlter(batch, schema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
        }
        // SELECT INTO creates the destination, so a read-only database refuses
        // it whatever the source produces — including no rows at all
        // (probe-confirmed). A #temp destination resolves no schema and stays
        // legal.
        owningDatabase?.RejectWriteWhenReadOnly();
        // tempdb carries no user CLR type, so a temporary destination can't
        // take a column of one (probed 2026-09-28 against SQL Server 2025).
        if (isLocalTemp || isGlobalTemp)
        {
            foreach (var column in destColumns)
            {
                if (column.Type is ClrUdtSqlType clrType)
                    throw SimulatedSqlException.SelectIntoClrTypeMissingInTarget(clrType.Udt.Name);
            }
        }
        var destinationDatabase = owningDatabase ?? batch.Connection.Simulation.Databases[TempdbDatabaseName];
        var destTable = new HeapTable(leaf, destColumns, destinationDatabase.AllocateObjectId())
        {
            OwningDatabase = owningDatabase,
            UsesAnsiNulls = batch.Connection.AnsiNulls,
            // The rows land on the default filegroup, as a CREATE TABLE's
            // without an ON clause do.
            FilegroupId = destinationDatabase.DefaultFilegroupId,
            LobFilegroupId = destinationDatabase.DefaultFilegroupId,
        };
        if (isGlobalTemp)
            destTable.OwnerSession = batch.Connection.Session;
        if (isLocalTemp)
            destTable.TempScopeId = batch.TempTableScopeId();
        // SELECT INTO creates a table, so it collides with every name in the
        // shared object namespace — a synonym, view or procedure of that name
        // raises Msg 2714 just as another table would (probe-confirmed).
        if (schema is not null && schema.HasNameInSharedNamespace(leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);
        VersionStore.NoteDefinitionChange(batch, destTable);
        if (!(isLocalTemp ? batch.Connection.TryAddTempTable(destTable) : destination.TryAdd(leaf, destTable)))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);
        // The new table joins the catalog views without passing the DDL arm.
        batch.Connection.Simulation.CatalogRows.Invalidate();
        // A local temp created via SELECT INTO inside a module body is dropped
        // when that module exits (probe-confirmed, same as CREATE TABLE #t).
        if (isLocalTemp)
            batch.RegisterScopedTempTable(destTable);

        // Temp-table SELECT INTO participates in transactional CREATE undo —
        // probe-confirmed that ROLLBACK undoes both local and global temp-table
        // CREATEs on real SQL Server, matching the asymmetry already documented
        // for regular CREATE TABLE which isn't logged.
        if ((isLocalTemp || isGlobalTemp) && batch.Connection.CurrentTransaction is { } tx)
        {
            if (isLocalTemp)
                tx.UndoLog.RecordLocalTempTableCreation(batch.Connection, destTable);
            else
                tx.UndoLog.RecordTempTableCreation(destination, leaf);
        }

        // Execute the SELECT and stream each row into the destination. Rows
        // arrive as SqlValue[] and are encoded through the destination's own
        // HeapColumn[] (same types by construction, but the encoder needs the
        // schema with nullability and LOB-store routing). Reading the values
        // rather than the bytes is what keeps a projecting SELECT from encoding
        // a page image only for this loop to decode it straight back — the
        // round trip landed on exactly the values the projection had computed.
        // Identity columns track source values via ObserveExplicit so the
        // high-water mark survives the copy. The one NULL an identity column
        // is handed is IDENTITY()'s placeholder, which takes the next value in
        // the rows' order — an inherited identity reads a NOT NULL column.
        var resultSet = selection.Execute(batch).WithRowCountLimit(batch.Connection.RowCountLimit);
        // A principal without UNMASK copies what it would read: the new table
        // holds the masked values, and no mask of its own (probed 2026-09-27).
        var masking = DataMasking.Applying(batch, selection.ColumnMasks);
        var rowCount = 0;
        var undoLog = batch.Connection.CurrentTransaction?.UndoLog;
        // One encoded-row buffer for the whole copy — Insert copies into the page.
        byte[]? encoded = null;
        foreach (var row in resultSet.RowValues)
        {
            var sourceValues = masking is null ? row : DataMasking.MaskRowForStorage(row, masking, resultSet.Schema);
            for (var i = 0; i < destColumns.Length; i++)
            {
                if (destColumns[i].Identity is not { } identity)
                    continue;
                if (sourceValues[i].IsNull)
                {
                    // The row may be one the plan baked, so the value goes in a copy.
                    sourceValues = [.. sourceValues];
                    sourceValues[i] = CoerceForIdentity(GenerateIdentity(destColumns[i]), destColumns[i]);
                }
                else
                {
                    identity.ObserveExplicit(IdentityState.FromSqlValue(sourceValues[i]));
                }
            }
            var length = RowEncoder.EncodeRowInto(destTable.StoredColumns, sourceValues, destTable.Heap, ref encoded);
            // Use the active undo log so a containing tx's ROLLBACK unwinds
            // the row writes alongside the table creation entry.
            _ = InsertRow(batch, destTable, encoded.AsSpan(0, length), undoLog, captureVersion: false);
            rowCount++;
        }

        // SELECT … INTO creates a table, so real raises CREATE_TABLE for it
        // (probe-confirmed) — but not for a temp destination.
        if (!isLocalTemp && !isGlobalTemp)
            RecordDdlEvent(batch.Parser, "CREATE_TABLE", schema?.Name ?? Database.DefaultSchemaName, leaf, "TABLE");
        return new SimulatedNonQuery(rowCount);
    }
}
