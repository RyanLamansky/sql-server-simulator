using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage.Backup;

// Restore: what RESTORE DATABASE does with an image. The new database is built
// while registered under its name in the RESTORING state, which keeps every
// other session out of it.
partial class DatabaseImage
{
    /// <summary>
    /// A new database named <paramref name="name"/> holding the image's files
    /// (<paramref name="physicalNames"/> giving each file's path, by logical
    /// name), filegroups, schemas, principals, permissions and id counters —
    /// everything registration doesn't overwrite and replay depends on.
    /// </summary>
    public Database CreateShell(string name, Dictionary<string, string> physicalNames)
    {
        var collation = Collation.TryGet(this.CollationName)
            ?? throw new InvalidDataException($"The backup set's collation '{this.CollationName}' isn't one the simulator recognizes.");
        var database = new Database(name, collation);
        this.ApplyStorageAndSecurity(database, physicalNames);
        // Ahead of the replay, so an id its DDL hands out beyond the ones the
        // steps carry can't collide with one a later step restores.
        database.RestoreCounters(this.Counters);
        return database;
    }

    /// <summary>
    /// Replaces <paramref name="database"/>'s files and filegroups, schema
    /// owners, principals, permissions and role memberships with the image's,
    /// adding the schemas it lacks.
    /// </summary>
    private void ApplyStorageAndSecurity(Database database, Dictionary<string, string> physicalNames)
    {
        lock (database.Files)
        {
            database.Files.Clear();
            foreach (var file in this.Files)
            {
                database.Files.Add(new DatabaseFile(file.FileId, file.IsLog, file.Name, physicalNames.GetValueOrDefault(file.Name, file.PhysicalName),
                    file.DataSpaceId, file.SizePages, file.MaxSizePages, file.Growth, file.IsPercentGrowth, file.IsContainer));
            }
        }
        lock (database.Filegroups)
        {
            database.Filegroups.Clear();
            foreach (var (filegroup, id) in this.Filegroups)
                database.Filegroups[filegroup] = id;
            database.ReadOnlyFilegroups.Clear();
            database.ReadOnlyFilegroups.UnionWith(this.ReadOnlyFilegroups);
            database.AutogrowAllFilesFilegroups.Clear();
            database.AutogrowAllFilesFilegroups.UnionWith(this.AutogrowAllFilesFilegroups);
        }
        database.FixedRoleSchemaOwners.Clear();
        foreach (var (schemaId, ownerId) in this.FixedRoleSchemaOwners)
            database.FixedRoleSchemaOwners[schemaId] = ownerId;
        foreach (var (schemaName, schemaId, principalId) in this.Schemas)
        {
            if (database.Schemas.TryGetValue(schemaName, out var existing))
                existing.PrincipalId = principalId;
            else
                database.Schemas[schemaName] = new Schema(database, schemaName, schemaId) { PrincipalId = principalId };
        }
        database.Principals.Clear();
        foreach (var principal in this.Principals)
        {
            database.Principals[principal.Name] = new DatabasePrincipal(principal.PrincipalId, principal.Name, principal.TypeCode, principal.TypeDescription,
                principal.IsFixedRole, principal.CreateDate, principal.LoginName, principal.SecurityIdentifierString)
            {
                LoginPrincipalId = principal.LoginPrincipalId,
                DefaultSchemaName = principal.DefaultSchemaName,
                PasswordHash = principal.PasswordHash,
                OwningPrincipalId = principal.OwningPrincipalId,
            };
        }
        lock (database.Permissions)
        {
            database.Permissions.Clear();
            database.Permissions.AddRange([.. this.Permissions]);
        }
        lock (database.RoleMembers)
        {
            database.RoleMembers.Clear();
            database.RoleMembers.AddRange(this.RoleMembers);
        }
    }

    /// <summary>
    /// Restores <paramref name="database"/> in place when every definition it
    /// holds is still exactly the image's — the common case of one database
    /// restored again and again from one backup — replacing each table's rows
    /// and every value the image carries without rebuilding an object: the
    /// result <see cref="Populate"/> would build, in a fraction of the time.
    /// False, having changed nothing, when anything but the standalone
    /// statistics queries create differs; the caller rebuilds instead.
    /// </summary>
    /// <remarks>
    /// The caller has made sure no other session uses the database and holds
    /// it in the RESTORING state.
    /// </remarks>
    public bool TryRestoreInPlace(Database database, Dictionary<string, string> physicalNames)
    {
        if (!database.CollationName.Equals(this.CollationName, StringComparison.Ordinal))
            return false;
        DatabaseImage live;
        try
        {
            live = Capture(database, rows: false);
        }
        catch (NotSupportedException)
        {
            return false;
        }
        if (!SameDefinitions(live, this))
            return false;
        var tables = new List<(TableImage Image, HeapTable Table, List<UserStatistic> Statistics)>(this.Tables.Count);
        foreach (var image in this.Tables)
        {
            var table = FindTable(database, image);
            if (TryRebuildStatistics(table, image) is not { } statistics)
                return false;
            tables.Add((image, table, statistics));
        }

        this.ApplySettings(database);
        // The restore replaces the database, which ends any session's single-user slot.
        Volatile.Write(ref database.SingleUserHolder, null);
        this.ApplyStorageAndSecurity(database, physicalNames);
        database.RestoreCounters(this.Counters);
        _ = Parallel.ForEach(tables, static entry =>
        {
            ClearRows(entry.Table);
            LoadRows(entry.Table, entry.Image);
        });
        foreach (var (image, table, statistics) in tables)
        {
            ApplyTableState(database, table, image, statistics);
            table.MarkStatisticsFresh(null, database.Collation);
        }
        this.ApplyObjectState(database);
        database.ExtendedProperties.Clear();
        foreach (var (key, value) in this.ExtendedProperties)
            database.ExtendedProperties[key] = value;
        lock (database.QueryStoreData.Gate)
            database.QueryStoreData.Clear();
        database.IsReadOnly = this.IsReadOnly;
        return true;
    }

    /// <summary>Whether two images recreate the same objects under the same ids, statistics aside.</summary>
    private static bool SameDefinitions(DatabaseImage live, DatabaseImage image)
    {
        var liveSteps = live.Steps.Where(static step => step.Phase != ReplayPhase.Statistics).ToList();
        var imageSteps = image.Steps.Where(static step => step.Phase != ReplayPhase.Statistics).ToList();
        if (liveSteps.Count != imageSteps.Count || live.Tables.Count != image.Tables.Count)
            return false;
        for (var i = 0; i < liveSteps.Count; i++)
        {
            var (a, b) = (liveSteps[i], imageSteps[i]);
            if (a.Sql != b.Sql || a.Phase != b.Phase || a.SortKey != b.SortKey || a.QuotedIdentifier != b.QuotedIdentifier || a.AnsiNulls != b.AnsiNulls
                || a.CreateDate != b.CreateDate || a.DefaultSchema != b.DefaultSchema || !a.ObjectIds.AsSpan().SequenceEqual(b.ObjectIds)
                || !a.UserTypeIds.AsSpan().SequenceEqual(b.UserTypeIds) || !a.XmlCollectionIds.AsSpan().SequenceEqual(b.XmlCollectionIds))
            {
                return false;
            }
        }
        for (var i = 0; i < live.Tables.Count; i++)
        {
            var (a, b) = (live.Tables[i], image.Tables[i]);
            if (a.SchemaName != b.SchemaName || a.Name != b.Name || a.ObjectId != b.ObjectId)
                return false;
        }
        if (live.Schemas.Count != image.Schemas.Count)
            return false;
        for (var i = 0; i < live.Schemas.Count; i++)
        {
            if (live.Schemas[i].Name != image.Schemas[i].Name || live.Schemas[i].SchemaId != image.Schemas[i].SchemaId)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The table's standalone statistics as the image has them: each the
    /// table already holds is rebuilt under its backed-up id, a missing one
    /// built from its columns; null when a missing one is filtered, which only
    /// a statement can parse.
    /// </summary>
    private static List<UserStatistic>? TryRebuildStatistics(HeapTable table, TableImage image)
    {
        var statistics = new List<UserStatistic>(image.Statistics.Count);
        foreach (var state in image.Statistics)
        {
            var held = table.UserStatistics.Find(candidate => candidate.Name == state.Name
                && candidate.ColumnFullOrdinals.AsSpan().SequenceEqual(state.Columns) && candidate.FilterDefinition == state.FilterDefinition);
            if (held is null && state.FilterDefinition is not null)
                return null;
            var statistic = held is null
                ? new UserStatistic(state.Name, state.StatsId, state.Columns, state.NoRecompute, state.CreateDate, autoCreated: state.AutoCreated)
                : new UserStatistic(held.Name, state.StatsId, held.ColumnFullOrdinals, state.NoRecompute, state.CreateDate,
                    held.Filter, held.FilterDefinition, held.FilterColumnFullOrdinals, state.AutoCreated);
            statistic.Statistics.AutoDrop = state.AutoDrop;
            statistic.Statistics.HasPersistedSample = state.HasPersistedSample;
            statistics.Add(statistic);
        }
        return statistics;
    }

    /// <summary>
    /// Empties a table's heap as <c>TRUNCATE TABLE</c> does, with its version
    /// chains and the locks interned over its old rows, none of which any
    /// session holds.
    /// </summary>
    private static void ClearRows(HeapTable table)
    {
        var heap = table.Heap;
        using (heap.EnterLatch())
        {
            heap.Pages.Clear();
            _ = heap.RecomputeRowCount();
            heap.LobPages.Clear();
            heap.RestoreForwardTargets([]);
            heap.ClearFreeLobPages();
            heap.ClearReclaimablePages();
            heap.RootedNullLobCells = null;
            heap.InvalidateSeekJournal();
        }
        lock (table.RowVersionsGate)
            heap.RowVersions.Clear();
        table.RowLocks.Clear();
        table.PageLocks.Clear();
        table.SupersededKeyImages.Clear();
        table.KeyLockGroups.Clear();
        foreach (var key in table.KeyConstraints)
            key.Statistics.Snapshot = null;
        foreach (var index in table.Indexes)
            index.Statistics.Snapshot = null;
        _ = Interlocked.Increment(ref table.DefinitionVersion);
    }

    /// <summary>
    /// Applies the database-level settings to <paramref name="database"/> — after
    /// registration, which seeds its scoped configuration from <c>model</c>.
    /// <see cref="IsReadOnly"/> waits for <see cref="Populate"/>'s end, since
    /// the replay writes.
    /// </summary>
    public void ApplySettings(Database database)
    {
        database.OwnerLoginName = this.OwnerLoginName;
        database.DatabaseGuid = this.DatabaseGuid;
        database.RecoveryForkGuid = this.RecoveryForkGuid;
        database.CompatibilityLevel = (CompatibilityLevel)this.CompatibilityLevel;
        database.AllowSnapshotIsolation = this.AllowSnapshotIsolation;
        database.ReadCommittedSnapshot = this.ReadCommittedSnapshot;
        database.RecursiveTriggers = this.RecursiveTriggers;
        database.Trustworthy = this.Trustworthy;
        database.CrossDatabaseChaining = this.CrossDatabaseChaining;
        database.BrokerEnabled = this.BrokerEnabled;
        database.FullTextDisabled = this.FullTextDisabled;
        database.RecoveryModel = (RecoveryModel)this.RecoveryModel;
        database.PageVerify = this.PageVerify;
        database.DelayedDurability = this.DelayedDurability;
        database.FileStreamNonTransactedAccess = this.FileStreamNonTransactedAccess;
        database.FileStreamDirectoryName = this.FileStreamDirectoryName;
        database.UserAccess = this.UserAccess;
        database.Switches = (DatabaseSwitches)this.Switches;
        database.TargetRecoveryTimeSeconds = this.TargetRecoveryTimeSeconds;
        database.DefaultFilegroupId = this.DefaultFilegroupId;
        database.MemoryOptimizedFilegroupId = this.MemoryOptimizedFilegroupId;
        database.QueryStore = this.QueryStore.Copy();
        var scoped = Math.Min(this.ScopedPrimary.Length, DatabaseScopedConfiguration.Options.Length);
        for (var i = 0; i < scoped; i++)
        {
            database.ScopedConfiguration.Set(i, forSecondary: false, this.ScopedPrimary[i]);
            database.ScopedConfiguration.Set(i, forSecondary: true, this.ScopedSecondary[i]);
        }
    }

    /// <summary>
    /// Recreates every schema object in <paramref name="database"/>, loads the
    /// rows, then stamps the state the DDL doesn't set.
    /// </summary>
    public void Populate(Simulation simulation, Database database)
    {
        using (var connection = simulation.CreateDbConnection())
        {
            connection.OpenSession();
            connection.CurrentDatabase = database;
            // Real's restore runs no DDL trigger, the database's own or the server's.
            connection.SuppressDdlTriggers = true;
            database.RestoringConnection = connection;
            try
            {
                var replay = new Replay(connection, database);
                replay.RunPhase(this.Steps.Where(static step => step.Phase == ReplayPhase.Definitions), retryMissingDependencies: true);
                replay.RunPhase(this.Steps.Where(static step => step.Phase == ReplayPhase.ForeignKeys), retryMissingDependencies: false);
                replay.RunPhase(this.Steps.Where(static step => step.Phase == ReplayPhase.Statistics), retryMissingDependencies: false);
            }
            finally
            {
                connection.PinnedUtcNow = null;
                connection.ReplayDefaultSchema = null;
                database.RestoringConnection = null;
            }
        }

        var tables = this.Tables.Select(image => (Image: image, Table: FindTable(database, image))).ToList();
        _ = Parallel.ForEach(tables, static pair => LoadRows(pair.Table, pair.Image));
        foreach (var (image, table) in tables)
            ApplyTableState(database, table, image);
        this.ApplyObjectState(database);
        foreach (var (key, value) in this.ExtendedProperties)
            database.ExtendedProperties[key] = value;
        database.IsReadOnly = this.IsReadOnly;
        database.RestoreCounters(this.Counters, raiseOnly: true);
    }

    private static HeapTable FindTable(Database database, TableImage image) =>
        database.Schemas.TryGetValue(image.SchemaName, out var schema) && schema.HeapTables.TryGetValue(image.Name, out var table)
            ? table
            : throw new InvalidOperationException($"RESTORE didn't recreate the table [{image.SchemaName}].[{image.Name}].");

    /// <summary>Inserts the image's rows straight into the table's heap, as the bacpac loader does: the data is already consistent.</summary>
    internal static void LoadRows(HeapTable table, TableImage image)
    {
        var columns = table.StoredColumns;
        var values = new SqlValue[columns.Length];
        byte[]? buffer = null;
        var offset = 0;
        for (var r = 0; r < image.RowCount; r++)
        {
            BackupRowCodec.ReadRow(columns, image.Rows, ref offset, values);
            var length = RowEncoder.EncodeRowInto(columns, values, table.Heap, ref buffer);
            _ = table.Heap.Insert(buffer.AsSpan(0, length));
        }
    }

    /// <summary>Stamps what <see cref="TableImage"/> carries onto the recreated <paramref name="table"/>.</summary>
    internal static void ApplyTableState(Database database, HeapTable table, TableImage image, List<UserStatistic>? statistics = null)
    {
        var collation = database.Collation;
        for (var c = 0; c < table.Columns.Length && c < image.Columns.Count; c++)
        {
            var column = table.Columns[c];
            (column.ColumnId, column.IsAnsiPaddingOff, column.IsUntypedNull) = image.Columns[c];
            column.Identity?.RestoreReseed((image.IdentityHighWaterMark, image.IdentityReseededStart));
        }
        table.MaxColumnIdUsed = image.MaxColumnIdUsed;
        table.OwnerPrincipalId = image.OwnerPrincipalId;
        table.UsesAnsiNulls = image.UsesAnsiNulls;
        table.LockEscalation = image.LockEscalation;
        table.FilegroupId = image.FilegroupId;
        table.LobFilegroupId = image.LobFilegroupId;
        table.HeapDataCompression = image.HeapDataCompression;
        table.HeapXmlCompression = image.HeapXmlCompression;
        table.PageCompressed = image.PageCompressed;
        table.TextInRowLimit = image.TextInRowLimit;
        table.LargeValueTypesOutOfRow = image.LargeValueTypesOutOfRow;
        table.LockOnBulkLoad = image.LockOnBulkLoad;
        table.HeapAllowRowLocks = image.HeapAllowRowLocks;
        table.HeapAllowPageLocks = image.HeapAllowPageLocks;
        table.KeysMayExceedLimit = image.KeysMayExceedLimit;

        foreach (var state in image.Keys)
        {
            var key = table.KeyConstraints.Find(candidate => collation.Equals(candidate.Name, state.Name))
                ?? throw new InvalidOperationException($"RESTORE didn't recreate the key '{state.Name}'.");
            key.IndexId = state.IndexId;
            key.FillFactor = state.FillFactor;
            key.IsPadded = state.IsPadded;
            key.AllowRowLocks = state.AllowRowLocks;
            key.AllowPageLocks = state.AllowPageLocks;
            key.OptimizeForSequentialKey = state.OptimizeForSequentialKey;
            key.DataCompression = state.DataCompression;
            key.XmlCompression = state.XmlCompression;
            key.StatisticsNoRecompute = state.StatisticsNoRecompute;
            key.IsDisabled = state.IsDisabled;
            key.FilegroupId = state.FilegroupId;
            key.KeyMayExceedLimit = state.KeyMayExceedLimit;
            key.Statistics.AutoDrop = state.AutoDrop;
            key.Statistics.HasPersistedSample = state.HasPersistedSample;
            key.ModifyDate = state.ModifyDate;
        }
        foreach (var state in image.Indexes)
        {
            var index = table.Indexes.Find(candidate => collation.Equals(candidate.Name, state.Name))
                ?? throw new InvalidOperationException($"RESTORE didn't recreate the index '{state.Name}'.");
            index.IndexId = state.IndexId;
            index.FillFactor = state.FillFactor;
            index.IsPadded = state.IsPadded;
            index.AllowRowLocks = state.AllowRowLocks;
            index.AllowPageLocks = state.AllowPageLocks;
            index.OptimizeForSequentialKey = state.OptimizeForSequentialKey;
            index.DataCompression = state.DataCompression;
            index.XmlCompression = state.XmlCompression;
            index.StatisticsNoRecompute = state.StatisticsNoRecompute;
            index.IsDisabled = state.IsDisabled;
            index.FilegroupId = state.FilegroupId;
            index.KeyMayExceedLimit = state.KeyMayExceedLimit;
            index.IgnoreDupKey = state.IgnoreDupKey;
            index.Statistics.AutoDrop = state.AutoDrop;
            index.Statistics.HasPersistedSample = state.HasPersistedSample;
        }
        foreach (var state in image.Checks)
        {
            var check = table.CheckConstraints.Find(candidate => collation.Equals(candidate.Name, state.Name))
                ?? throw new InvalidOperationException($"RESTORE didn't recreate the CHECK constraint '{state.Name}'.");
            check.IsSystemNamed = state.IsSystemNamed;
            check.IsNotTrusted = state.IsNotTrusted;
            check.NotForReplication = state.NotForReplication;
            check.IsDisabled = state.IsDisabled;
            check.Definition = state.Definition;
            check.ModifyDate = state.ModifyDate;
        }
        foreach (var state in image.Defaults)
        {
            var column = Array.Find(table.Columns, candidate => collation.Equals(candidate.Name, state.Name));
            if (column?.DefaultConstraint is not { } columnDefault)
                throw new InvalidOperationException($"RESTORE didn't recreate the default of column '{state.Name}'.");
            columnDefault.IsSystemNamed = state.IsSystemNamed;
            columnDefault.ModifyDate = state.ModifyDate;
        }
        foreach (var state in image.ForeignKeys)
        {
            var foreignKey = table.OutgoingForeignKeys.Find(candidate => collation.Equals(candidate.Name, state.Name))
                ?? throw new InvalidOperationException($"RESTORE didn't recreate the foreign key '{state.Name}'.");
            foreignKey.IsSystemNamed = state.IsSystemNamed;
            foreignKey.IsNotTrusted = state.IsNotTrusted;
            foreignKey.NotForReplication = state.NotForReplication;
            foreignKey.IsDisabled = state.IsDisabled;
            foreignKey.ModifyDate = state.ModifyDate;
        }
        if (statistics is not null)
        {
            table.UserStatistics = statistics;
        }
        else if (image.Statistics.Count != 0)
        {
            statistics = new List<UserStatistic>(table.UserStatistics.Count);
            foreach (var state in image.Statistics)
            {
                var created = table.UserStatistics.Find(candidate => collation.Equals(candidate.Name, state.Name))
                    ?? throw new InvalidOperationException($"RESTORE didn't recreate the statistic '{state.Name}'.");
                var statistic = new UserStatistic(created.Name, state.StatsId, created.ColumnFullOrdinals, state.NoRecompute, state.CreateDate,
                    created.Filter, created.FilterDefinition, created.FilterColumnFullOrdinals, state.AutoCreated);
                statistic.Statistics.AutoDrop = state.AutoDrop;
                statistic.Statistics.HasPersistedSample = state.HasPersistedSample;
                statistics.Add(statistic);
            }
            table.UserStatistics = statistics;
        }
        table.ModifyDate = image.ModifyDate;
    }

    /// <summary>Stamps owners, dates and switches onto every other recreated object, and positions the sequences.</summary>
    private void ApplyObjectState(Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, alias) in schema.AliasTypes)
            {
                if (this.Types.Find(image => !image.IsXmlSchemaCollection && image.Id == alias.UserTypeId) is { } aliasImage)
                    alias.OwnerPrincipalId = aliasImage.OwnerPrincipalId;
            }
            foreach (var (_, collection) in schema.XmlSchemaCollections)
            {
                if (this.Types.Find(image => image.IsXmlSchemaCollection && image.Id == collection.Id) is { } collectionImage)
                {
                    collection.PrincipalId = collectionImage.OwnerPrincipalId;
                    collection.ModifyDate = collectionImage.ModifyDate;
                }
            }
        }

        foreach (var image in this.Objects)
        {
            var obj = FindObject(database, image.SchemaName, image.Name)
                ?? throw new InvalidOperationException($"RESTORE didn't recreate the object '{image.Name}'.");
            obj.ModifyDate = image.ModifyDate;
            obj.OwnerPrincipalId = image.OwnerPrincipalId;
            obj.ExecuteAsPrincipalId = image.ExecuteAsPrincipalId;
            switch (obj)
            {
                case Trigger trigger:
                    trigger.IsDisabled = image.IsDisabled;
                    trigger.NotForReplication = image.NotForReplication;
                    trigger.FirstForActions = (TriggerActions)image.FirstForActions;
                    trigger.LastForActions = (TriggerActions)image.LastForActions;
                    break;
                case DdlTrigger ddlTrigger:
                    ddlTrigger.IsDisabled = image.IsDisabled;
                    break;
            }
        }
        foreach (var image in this.Sequences)
        {
            if (FindObject(database, image.SchemaName, image.Name) is not Sequence sequence)
                throw new InvalidOperationException($"RESTORE didn't recreate the sequence '{image.Name}'.");
            sequence.StartValue = image.StartValue;
            sequence.Increment = image.Increment;
            sequence.MinValue = image.MinValue;
            sequence.MaxValue = image.MaxValue;
            sequence.Cycle = image.Cycle;
            sequence.CacheSize = image.CacheSize;
            sequence.CurrentValue = image.CurrentValue;
            sequence.IsExhausted = image.IsExhausted;
            sequence.LastUsedValue = image.LastUsedValue;
        }
    }

    /// <summary>The schema object, table type or (with no schema) database DDL trigger named <paramref name="name"/>.</summary>
    private static SchemaObject? FindObject(Database database, string? schemaName, string name)
    {
        if (schemaName is null)
            return database.DdlTriggers.GetValueOrDefault(name);
        if (!database.Schemas.TryGetValue(schemaName, out var schema))
            return null;
        return schema.TryFindInSharedNamespace(name, out var found) ? found : schema.TableTypes.GetValueOrDefault(name);
    }

    /// <summary>Runs replay steps on the restoring session.</summary>
    private sealed class Replay(SimulatedDbConnection connection, Database database)
    {
        private bool quotedIdentifier = true;
        private bool ansiNulls = true;

        public void RunPhase(IEnumerable<ReplayStep> steps, bool retryMissingDependencies)
        {
            var pending = steps.OrderBy(static step => step.SortKey).ToList();
            while (pending.Count != 0)
            {
                var failed = new List<ReplayStep>();
                Exception? firstFailure = null;
                foreach (var step in pending)
                {
                    try
                    {
                        this.Run(step);
                    }
                    catch (Exception ex) when (retryMissingDependencies && ex is SimulatedSqlException or NotSupportedException)
                    {
                        failed.Add(step);
                        firstFailure ??= ex;
                    }
                }
                if (failed.Count == pending.Count)
                    throw new InvalidOperationException($"RESTORE couldn't recreate an object of the backup set: {firstFailure!.Message} (replaying: {failed[0].Sql})", firstFailure);
                pending = failed;
            }
        }

        private void Run(ReplayStep step)
        {
            if (step.QuotedIdentifier != this.quotedIdentifier)
            {
                this.Execute(step.QuotedIdentifier ? "SET QUOTED_IDENTIFIER ON" : "SET QUOTED_IDENTIFIER OFF");
                this.quotedIdentifier = step.QuotedIdentifier;
            }
            if (step.AnsiNulls != this.ansiNulls)
            {
                this.Execute(step.AnsiNulls ? "SET ANSI_NULLS ON" : "SET ANSI_NULLS OFF");
                this.ansiNulls = step.AnsiNulls;
            }
            connection.PinnedUtcNow = step.CreateDate;
            connection.ReplayDefaultSchema = step.DefaultSchema;
            var objectIds = step.ObjectIds;
            while (true)
            {
                if (step.MayDrawMoreIds)
                    this.Execute("BEGIN TRANSACTION");
                database.RestoredObjectIds = new Queue<int>(objectIds);
                database.RestoredUserTypeIds = new Queue<int>(step.UserTypeIds);
                database.RestoredXmlCollectionIds = new Queue<int>(step.XmlCollectionIds);
                database.RestoredAllocations = 0;
                try
                {
                    this.Execute(step.Sql);
                }
                catch
                {
                    if (step.MayDrawMoreIds)
                        this.Execute("IF @@TRANCOUNT > 0 ROLLBACK");
                    throw;
                }
                finally
                {
                    database.RestoredObjectIds = null;
                    database.RestoredUserTypeIds = null;
                    database.RestoredXmlCollectionIds = null;
                }
                var drawn = database.RestoredAllocations;
                if (!step.MayDrawMoreIds)
                    return;
                if (drawn <= objectIds.Length || objectIds.Length == 0 || objectIds.Length != step.ObjectIds.Length)
                {
                    this.Execute("COMMIT");
                    return;
                }
                // The extra draws came ahead of the object's own: run it again
                // with them served first, by ids its own objects then replace.
                this.Execute("ROLLBACK");
                objectIds = [.. Enumerable.Repeat(objectIds[0], drawn - objectIds.Length), .. objectIds];
            }
        }

        private void Execute(string sql)
        {
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // the backup set's own DDL, captured from a database by the simulator, never user input
            command.CommandText = sql;
#pragma warning restore CA2100
            _ = command.ExecuteNonQuery();
        }
    }
}
