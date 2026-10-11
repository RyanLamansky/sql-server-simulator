using System.Globalization;
using System.Text;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage.Backup;

// Capture: what BACKUP DATABASE writes. The caller holds a shared lock on every
// table, so the rows read here are committed and no definition changes under it.
partial class DatabaseImage
{
    /// <summary>
    /// Captures <paramref name="database"/>. A feature the image can't carry
    /// yet raises <see cref="NotSupportedException"/> naming it, so a backup
    /// never silently loses part of a database.
    /// </summary>
    public static DatabaseImage Capture(Database database) => Capture(database, rows: true);

    /// <summary><see cref="Capture(Database)"/>, without the rows when <paramref name="rows"/> is false.</summary>
    internal static DatabaseImage Capture(Database database, bool rows)
    {
        RejectUnsupported(database);
        var image = new DatabaseImage
        {
            DatabaseName = database.Name,
            CollationName = database.CollationName,
            OwnerLoginName = database.OwnerLoginName,
            DatabaseGuid = database.DatabaseGuid,
            RecoveryForkGuid = database.RecoveryForkGuid,
            CompatibilityLevel = (int)database.CompatibilityLevel,
            AllowSnapshotIsolation = database.AllowSnapshotIsolation,
            ReadCommittedSnapshot = database.ReadCommittedSnapshot,
            RecursiveTriggers = database.RecursiveTriggers,
            Trustworthy = database.Trustworthy,
            CrossDatabaseChaining = database.CrossDatabaseChaining,
            IsReadOnly = database.IsReadOnly,
            BrokerEnabled = database.BrokerEnabled,
            FullTextDisabled = database.FullTextDisabled,
            RecoveryModel = (byte)database.RecoveryModel,
            PageVerify = database.PageVerify,
            DelayedDurability = database.DelayedDurability,
            FileStreamNonTransactedAccess = database.FileStreamNonTransactedAccess,
            UserAccess = database.UserAccess,
            Switches = (int)database.Switches,
            TargetRecoveryTimeSeconds = database.TargetRecoveryTimeSeconds,
            DefaultFilegroupId = database.DefaultFilegroupId,
            MemoryOptimizedFilegroupId = database.MemoryOptimizedFilegroupId,
            FileStreamDirectoryName = database.FileStreamDirectoryName,
            Counters = database.CaptureCounters(),
            QueryStore = database.QueryStore.Copy(),
        };
        var scoped = DatabaseScopedConfiguration.Options.Length;
        image.ScopedPrimary = new string[scoped];
        image.ScopedSecondary = new string?[scoped];
        for (var i = 0; i < scoped; i++)
        {
            image.ScopedPrimary[i] = database.ScopedConfiguration.Primary(i);
            image.ScopedSecondary[i] = database.ScopedConfiguration.Secondary(i);
        }
        foreach (var file in database.FilesInOrder())
        {
            image.Files.Add(new DatabaseFile(file.FileId, file.IsLog, file.Name, file.PhysicalName, file.DataSpaceId,
                file.SizePages, file.MaxSizePages, file.Growth, file.IsPercentGrowth, file.IsContainer));
        }
        lock (database.Filegroups)
        {
            foreach (var (name, id) in database.Filegroups)
                image.Filegroups.Add((name, id));
            image.ReadOnlyFilegroups = [.. database.ReadOnlyFilegroups];
            image.AutogrowAllFilesFilegroups = [.. database.AutogrowAllFilesFilegroups];
        }
        foreach (var (schemaId, ownerId) in database.FixedRoleSchemaOwners)
            image.FixedRoleSchemaOwners.Add((schemaId, ownerId));
        foreach (var (_, principal) in database.Principals)
        {
            image.Principals.Add(new DatabasePrincipal(principal.PrincipalId, principal.Name, principal.TypeCode, principal.TypeDescription,
                principal.IsFixedRole, principal.CreateDate, principal.LoginName, principal.SecurityIdentifierString)
            {
                LoginPrincipalId = principal.LoginPrincipalId,
                DefaultSchemaName = principal.DefaultSchemaName,
                PasswordHash = principal.PasswordHash,
                OwningPrincipalId = principal.OwningPrincipalId,
            });
        }
        lock (database.Permissions)
        {
            foreach (var permission in database.Permissions)
                image.Permissions.Add(permission);
        }
        lock (database.RoleMembers)
            image.RoleMembers.AddRange(database.RoleMembers);
        foreach (var (key, value) in database.ExtendedProperties)
            image.ExtendedProperties.Add((key, value));

        foreach (var schema in database.Schemas.Select(static pair => pair.Value).OrderBy(static schema => schema.SchemaId))
        {
            image.Schemas.Add((schema.Name, schema.SchemaId, schema.PrincipalId));
            image.CaptureSchema(database, schema, rows);
        }
        foreach (var (_, trigger) in database.DdlTriggers)
        {
            image.AddModuleStep(trigger, null);
            image.AddObject(trigger, null);
        }
        return image;
    }

    /// <summary>Refuses the features <see cref="Populate"/> can't rebuild yet.</summary>
    private static void RejectUnsupported(Database database)
    {
        static NotSupportedException Unsupported(string feature) =>
            new($"BACKUP DATABASE of a database holding {feature} isn't modeled by the simulator.");

        if (!database.Assemblies.IsEmptyLockFree())
            throw Unsupported("CLR assemblies");
        if (!database.PartitionFunctions.IsEmptyLockFree() || !database.PartitionSchemes.IsEmptyLockFree())
            throw Unsupported("partition functions or schemes");
        if (!database.FullTextCatalogs.IsEmptyLockFree())
            throw Unsupported("full-text catalogs");
        if (database.ChangeTracking is not null)
            throw Unsupported("change tracking");
        foreach (var (_, trigger) in database.DdlTriggers)
        {
            if (trigger.ClrEntry is not null || trigger.DefinitionText is null || trigger.FirstForEvents is not null || trigger.LastForEvents is not null)
                throw Unsupported($"the DDL trigger '{trigger.Name}' (CLR, encrypted, or with a firing order)");
        }
        foreach (var (_, schema) in database.Schemas)
        {
            if (!schema.SecurityPolicies.IsEmptyLockFree())
                throw Unsupported("security policies");
            if (!schema.Defaults.IsEmptyLockFree() || !schema.Rules.IsEmptyLockFree())
                throw Unsupported("CREATE DEFAULT / CREATE RULE objects");
            foreach (var (_, alias) in schema.AliasTypes)
            {
                if (alias.BoundDefault is not null || alias.BoundRule is not null)
                    throw Unsupported("bound defaults or rules");
            }
            foreach (var (_, type) in schema.TableTypes)
            {
                if (type.IsMemoryOptimized)
                    throw Unsupported("memory-optimized table types");
            }
            foreach (var (_, table) in schema.HeapTables)
                RejectUnsupportedTable(table, Unsupported);
            foreach (var obj in schema.SchemaObjects())
            {
                switch (obj)
                {
                    case Procedure { ClrEntry: not null } or Trigger { ClrEntry: not null } or ClrFunction:
                        throw Unsupported($"the CLR module '{obj.Name}'");
                    case Procedure { Numbered: not null }:
                        throw Unsupported($"the numbered procedure '{obj.Name}'");
                    case Procedure or Trigger or View or UserDefinedFunction when obj.DefinitionText is null:
                        throw Unsupported($"the module '{obj.Name}' created WITH ENCRYPTION");
                    case Procedure or Trigger or View or UserDefinedFunction when obj.IsNativelyCompiled:
                        throw Unsupported($"the natively compiled module '{obj.Name}'");
                }
            }
        }
    }

    private static void RejectUnsupportedTable(HeapTable table, Func<string, NotSupportedException> unsupported)
    {
        var name = table.Name;
        if (table.IsMemoryOptimized)
            throw unsupported($"the memory-optimized table '{name}'");
        if (table.PeriodColumns is not null || table.SystemVersioning is not null || table.IsHistoryTable)
            throw unsupported($"the temporal table '{name}'");
        if (table.GraphKind != GraphTableKind.None)
            throw unsupported($"the graph table '{name}'");
        if (table.Partitioning is not null || table.HeapPartitionDataCompression is not null || table.HeapPartitionXmlCompression is not null)
            throw unsupported($"the partitioned table '{name}'");
        if (table.FullTextIndex is not null)
            throw unsupported($"the full-text index on '{name}'");
        if (table.ChangeTracking is not null)
            throw unsupported($"change tracking on '{name}'");
        if (table.XmlIndexes.Count != 0 || table.SpatialIndexes.Count != 0 || table.JsonIndexes.Count != 0 || table.VectorIndexes.Count != 0)
            throw unsupported($"an XML, spatial, JSON or vector index on '{name}'");
        if (table.HypotheticalIndexes.Count != 0)
            throw unsupported($"a hypothetical index on '{name}'");
        if (table.EdgeConstraints.Count != 0)
            throw unsupported($"an edge constraint on '{name}'");
        foreach (var column in table.Columns)
        {
            if (column.MaskingFunction is not null)
                throw unsupported($"the masked column '{name}.{column.Name}'");
            if (column.GeneratedAs != GeneratedAlwaysAsRow.None || column.IsHidden)
                throw unsupported($"the GENERATED ALWAYS column '{name}.{column.Name}'");
            if (column.BoundDefault is not null || column.BoundRule is not null)
                throw unsupported("bound defaults or rules");
            if (column.Type is ClrUdtSqlType)
                throw unsupported($"the CLR-typed column '{name}.{column.Name}'");
        }
        foreach (var key in table.KeyConstraints)
        {
            if (key.IsHash || key.Partitioning is not null || key.PartitionDataCompression is not null || key.PartitionXmlCompression is not null)
                throw unsupported($"the hash or partitioned key '{key.Name}'");
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsColumnstore || index.IsHash || index.Partitioning is not null || index.PartitionDataCompression is not null || index.PartitionXmlCompression is not null)
                throw unsupported($"the columnstore, hash or partitioned index '{index.Name}'");
        }
    }

    private void CaptureSchema(Database database, Schema schema, bool rows)
    {
        foreach (var (_, alias) in schema.AliasTypes)
        {
            this.Steps.Add(new ReplayStep
            {
                Sql = $"CREATE TYPE {Qualified(schema, alias.Name)} FROM {TypeSql(alias.UnderlyingType, alias.SpelledNumeric)} {(alias.IsNullable ? "NULL" : "NOT NULL")}",
                UserTypeIds = [alias.UserTypeId],
                CreateDate = alias.CreateDate,
            });
            this.Types.Add(new TypeImage { Id = alias.UserTypeId, OwnerPrincipalId = alias.OwnerPrincipalId, ModifyDate = alias.CreateDate });
        }
        foreach (var (_, collection) in schema.XmlSchemaCollections)
        {
            this.Steps.Add(new ReplayStep
            {
                Sql = $"CREATE XML SCHEMA COLLECTION {Qualified(schema, collection.Name)} AS N'{collection.XsdText.Replace("'", "''", StringComparison.Ordinal)}'",
                XmlCollectionIds = [collection.Id],
                CreateDate = collection.CreateDate,
            });
            this.Types.Add(new TypeImage { Id = collection.Id, IsXmlSchemaCollection = true, OwnerPrincipalId = collection.PrincipalId, ModifyDate = collection.ModifyDate });
        }
        foreach (var (_, sequence) in schema.Sequences)
        {
            this.Steps.Add(new ReplayStep
            {
                Sql = $"CREATE SEQUENCE {Qualified(schema, sequence.Name)} AS {TypeSql(sequence.DeclaredType, sequence.SpelledNumeric)}",
                SortKey = sequence.ObjectId,
                ObjectIds = [sequence.ObjectId],
                CreateDate = sequence.CreateDate,
            });
            this.AddObject(sequence, schema);
            this.Sequences.Add(new SequenceImage
            {
                SchemaName = schema.Name,
                Name = sequence.Name,
                ObjectId = sequence.ObjectId,
                StartValue = sequence.StartValue,
                Increment = sequence.Increment,
                MinValue = sequence.MinValue,
                MaxValue = sequence.MaxValue,
                Cycle = sequence.Cycle,
                CacheSize = sequence.CacheSize,
                CurrentValue = sequence.CurrentValue,
                IsExhausted = sequence.IsExhausted,
                LastUsedValue = sequence.LastUsedValue,
            });
        }
        foreach (var (_, type) in schema.TableTypes)
        {
            this.Steps.Add(TableTypeStep(schema, type));
            this.AddObject(type, schema);
        }
        foreach (var (_, table) in schema.HeapTables)
            this.CaptureTable(database, schema, table, rows);
        foreach (var (_, view) in schema.Views)
        {
            this.AddModuleStep(view, schema);
            this.AddObject(view, schema);
            foreach (var index in view.Indexes)
                this.Steps.Add(IndexStep(schema, view.Name, view.OutputColumns, index));
        }
        foreach (var (_, function) in schema.Functions)
        {
            this.AddModuleStep(function, schema);
            this.AddObject(function, schema);
        }
        foreach (var (_, procedure) in schema.Procedures)
        {
            this.AddModuleStep(procedure, schema);
            this.AddObject(procedure, schema);
        }
        foreach (var (_, trigger) in schema.Triggers)
        {
            this.AddModuleStep(trigger, schema);
            this.AddObject(trigger, schema);
        }
        foreach (var (_, synonym) in schema.Synonyms)
        {
            this.Steps.Add(new ReplayStep
            {
                Sql = $"CREATE SYNONYM {Qualified(schema, synonym.Name)} FOR {synonym.BaseObjectName}",
                SortKey = synonym.ObjectId,
                ObjectIds = [synonym.ObjectId],
                CreateDate = synonym.CreateDate,
            });
            this.AddObject(synonym, schema);
        }
    }

    private void AddModuleStep(SchemaObject module, Schema? schema) => this.Steps.Add(new ReplayStep
    {
        Sql = module.DefinitionText!,
        DefaultSchema = schema?.Name,
        MayDrawMoreIds = true,
        SortKey = module.ObjectId,
        ObjectIds = ModuleObjectIds(module),
        QuotedIdentifier = module.UsesQuotedIdentifier,
        AnsiNulls = module.UsesAnsiNulls,
        CreateDate = module.CreateDate,
    });

    /// <summary>
    /// The ids a module's <c>CREATE</c> draws, in order: binding a DML
    /// trigger's body draws two for its inserted and deleted tables ahead of
    /// the trigger's own, which are thrown away; a multi-statement function's
    /// return-table constraints take theirs ahead of the function's, with
    /// whatever its binding draws between them, so the whole range they span
    /// is served again.
    /// </summary>
    private static int[] ModuleObjectIds(SchemaObject module)
    {
        switch (module)
        {
            case Trigger:
                return [module.ObjectId, module.ObjectId, module.ObjectId];
            case MultiStatementTableValuedFunction function when function.KeyConstraints.Length != 0 || function.CheckConstraints.Length != 0
                || Array.Exists(function.OutputColumns, static column => column.DefaultConstraint is not null):
                {
                    var owned = function.KeyConstraints.Select(static key => key.ObjectId)
                        .Concat(function.CheckConstraints.Select(static check => check.ObjectId))
                        .Concat(function.OutputColumns.Where(static column => column.DefaultConstraint is not null).Select(static column => column.DefaultConstraint!.ObjectId))
                        .Append(function.ObjectId)
                        .ToArray();
                    var first = owned.Min();
                    return [.. Enumerable.Range(first, owned.Max() - first + 1)];
                }
            default:
                return [module.ObjectId];
        }
    }

    private void AddObject(SchemaObject obj, Schema? schema)
    {
        var image = new ObjectImage
        {
            SchemaName = schema?.Name,
            Name = obj.Name,
            ObjectId = obj.ObjectId,
            ModifyDate = obj.ModifyDate,
            OwnerPrincipalId = obj.OwnerPrincipalId,
            ExecuteAsPrincipalId = obj.ExecuteAsPrincipalId,
        };
        if (obj is Trigger trigger)
        {
            image.IsDisabled = trigger.IsDisabled;
            image.NotForReplication = trigger.NotForReplication;
            image.FirstForActions = (int)trigger.FirstForActions;
            image.LastForActions = (int)trigger.LastForActions;
        }
        else if (obj is DdlTrigger ddlTrigger)
        {
            image.IsDisabled = ddlTrigger.IsDisabled;
        }
        this.Objects.Add(image);
    }

    private void CaptureTable(Database database, Schema schema, HeapTable table, bool rows)
    {
        var image = new TableImage
        {
            SchemaName = schema.Name,
            Name = table.Name,
            ObjectId = table.ObjectId,
            ModifyDate = table.ModifyDate,
            OwnerPrincipalId = table.OwnerPrincipalId,
            UsesAnsiNulls = table.UsesAnsiNulls,
            LockEscalation = table.LockEscalation,
            FilegroupId = table.FilegroupId,
            LobFilegroupId = table.LobFilegroupId,
            HeapDataCompression = table.HeapDataCompression,
            HeapXmlCompression = table.HeapXmlCompression,
            PageCompressed = table.PageCompressed,
            TextInRowLimit = table.TextInRowLimit,
            LargeValueTypesOutOfRow = table.LargeValueTypesOutOfRow,
            LockOnBulkLoad = table.LockOnBulkLoad,
            HeapAllowRowLocks = table.HeapAllowRowLocks,
            HeapAllowPageLocks = table.HeapAllowPageLocks,
            KeysMayExceedLimit = table.KeysMayExceedLimit,
            MaxColumnIdUsed = table.MaxColumnIdUsed,
        };
        foreach (var column in table.Columns)
        {
            image.Columns.Add((column.ColumnId, column.IsAnsiPaddingOff, column.IsUntypedNull));
            if (column.Identity is { } identity)
                (image.IdentityHighWaterMark, image.IdentityReseededStart) = identity.ReseedSnapshot();
        }

        var qualified = Qualified(schema, table.Name);
        var sql = new StringBuilder("CREATE TABLE ").Append(qualified).Append(" (");
        var defaultIds = new List<int>();
        var checkIds = new List<int>();
        // A column's CHECK declared with the table goes inline; one added later
        // runs as its own ALTER TABLE, which reads the single column it checks
        // as its column all the same.
        var columnChecks = table.CheckConstraints.Where(check => check.InlineColumn is not null && check.CreateDate == table.CreateDate).ToList();
        for (var c = 0; c < table.Columns.Length; c++)
        {
            var column = table.Columns[c];
            if (c > 0)
                _ = sql.Append(", ");
            AppendColumn(sql, column, database);
            if (column.DefaultConstraint is { } columnDefault)
            {
                image.Defaults.Add(new ConstraintState
                {
                    Name = column.Name,
                    IsSystemNamed = columnDefault.IsSystemNamed,
                    ModifyDate = columnDefault.ModifyDate,
                });
                if (columnDefault.CreateDate == table.CreateDate)
                {
                    _ = sql.Append(" CONSTRAINT ").Append(Quote(columnDefault.Name)).Append(" DEFAULT ").Append(columnDefault.Definition);
                    defaultIds.Add(columnDefault.ObjectId);
                }
                else
                {
                    this.Steps.Add(new ReplayStep
                    {
                        Sql = $"ALTER TABLE {qualified} ADD CONSTRAINT {Quote(columnDefault.Name)} DEFAULT {columnDefault.Definition} FOR {Quote(column.Name)}",
                        SortKey = columnDefault.ObjectId,
                        ObjectIds = [columnDefault.ObjectId],
                        CreateDate = columnDefault.CreateDate,
                    });
                }
            }
            foreach (var check in columnChecks)
            {
                if (database.Collation.Equals(check.InlineColumn, column.Name))
                {
                    _ = sql.Append(" CONSTRAINT ").Append(Quote(check.Name)).Append(" CHECK ").Append(CheckDefinition(check));
                    checkIds.Add(check.ObjectId);
                }
            }
        }

        // The keys declared with the table, in its id order: the clustered one
        // first, then the rest in reverse declaration order (see
        // Simulation.AllocateDeclarationObjectIds), so each comes back under its id.
        var inlineKeys = table.KeyConstraints.Where(key => key.CreateDate == table.CreateDate).ToList();
        foreach (var key in inlineKeys)
            _ = sql.Append(", ").Append(KeySql(table, key));
        var keyIds = new List<int>();
        var clustered = inlineKeys.FindIndex(static key => key.IsClustered);
        if (clustered >= 0)
            keyIds.Add(inlineKeys[clustered].ObjectId);
        for (var k = inlineKeys.Count - 1; k >= 0; k--)
        {
            if (k != clustered)
                keyIds.Add(inlineKeys[k].ObjectId);
        }
        _ = sql.Append(')');
        this.Steps.Add(new ReplayStep
        {
            Sql = sql.ToString(),
            SortKey = table.ObjectId,
            ObjectIds = [.. defaultIds, .. keyIds, .. checkIds, table.ObjectId],
            AnsiNulls = table.UsesAnsiNulls,
            CreateDate = table.CreateDate,
        });

        foreach (var key in table.KeyConstraints)
        {
            image.Keys.Add(CaptureIndexState(key.Name, key.IndexId, key.FillFactor, key.IsPadded, key.AllowRowLocks, key.AllowPageLocks,
                key.OptimizeForSequentialKey, key.DataCompression, key.XmlCompression, key.StatisticsNoRecompute, key.IsDisabled,
                key.FilegroupId, key.KeyMayExceedLimit, key.IgnoreDupKey, key.Statistics, key.ModifyDate));
            if (key.CreateDate != table.CreateDate)
            {
                this.Steps.Add(new ReplayStep
                {
                    Sql = $"ALTER TABLE {qualified} ADD {KeySql(table, key)}",
                    SortKey = key.ObjectId,
                    ObjectIds = [key.ObjectId],
                    CreateDate = key.CreateDate,
                });
            }
        }
        foreach (var check in table.CheckConstraints)
        {
            image.Checks.Add(new ConstraintState
            {
                Name = check.Name,
                IsSystemNamed = check.IsSystemNamed,
                IsNotTrusted = check.IsNotTrusted,
                NotForReplication = check.NotForReplication,
                IsDisabled = check.IsDisabled,
                Definition = check.Definition,
                ModifyDate = check.ModifyDate,
            });
            if (!columnChecks.Contains(check))
            {
                this.Steps.Add(new ReplayStep
                {
                    Sql = $"ALTER TABLE {qualified} ADD CONSTRAINT {Quote(check.Name)} CHECK {CheckDefinition(check)}",
                    SortKey = check.ObjectId,
                    ObjectIds = [check.ObjectId],
                    AnsiNulls = table.UsesAnsiNulls,
                    CreateDate = check.CreateDate,
                });
            }
        }
        foreach (var index in table.Indexes)
        {
            image.Indexes.Add(CaptureIndexState(index.Name, index.IndexId, index.FillFactor, index.IsPadded, index.AllowRowLocks, index.AllowPageLocks,
                index.OptimizeForSequentialKey, index.DataCompression, index.XmlCompression, index.StatisticsNoRecompute, index.IsDisabled,
                index.FilegroupId, index.KeyMayExceedLimit, index.IgnoreDupKey, index.Statistics, default));
            this.Steps.Add(IndexStep(schema, table.Name, table.Columns, index));
        }
        foreach (var statistic in table.UserStatistics)
        {
            image.Statistics.Add(new StatisticState
            {
                Name = statistic.Name,
                StatsId = statistic.StatsId,
                AutoCreated = statistic.AutoCreated,
                NoRecompute = statistic.NoRecompute,
                AutoDrop = statistic.Statistics.AutoDrop,
                HasPersistedSample = statistic.Statistics.HasPersistedSample,
                HasStatistics = statistic.Statistics.Snapshot is not null,
                CreateDate = statistic.CreateDate,
                Columns = statistic.ColumnFullOrdinals,
                FilterDefinition = statistic.FilterDefinition,
            });
            var columns = string.Join(", ", statistic.ColumnFullOrdinals.Select(ordinal => Quote(table.Columns[ordinal].Name)));
            var filter = statistic.FilterDefinition is { } definition ? $" WHERE {Unwrap(definition)}" : "";
            this.Steps.Add(new ReplayStep
            {
                Sql = $"CREATE STATISTICS {Quote(statistic.Name)} ON {qualified} ({columns}){filter}",
                Phase = ReplayPhase.Statistics,
                SortKey = table.ObjectId,
                AnsiNulls = table.UsesAnsiNulls,
                CreateDate = statistic.CreateDate,
            });
        }
        foreach (var foreignKey in table.OutgoingForeignKeys)
        {
            image.ForeignKeys.Add(new ConstraintState
            {
                Name = foreignKey.Name,
                IsSystemNamed = foreignKey.IsSystemNamed,
                IsNotTrusted = foreignKey.IsNotTrusted,
                NotForReplication = foreignKey.NotForReplication,
                IsDisabled = foreignKey.IsDisabled,
                ModifyDate = foreignKey.ModifyDate,
            });
            var referenced = foreignKey.ReferencedTable;
            var referencedSchema = database.Schemas.Select(static pair => pair.Value).First(candidate => candidate.SchemaId == referenced.SchemaId);
            this.Steps.Add(new ReplayStep
            {
                Sql = $"ALTER TABLE {qualified} WITH NOCHECK ADD CONSTRAINT {Quote(foreignKey.Name)} FOREIGN KEY ({ColumnList(table.Columns, foreignKey.ChildColumnOrdinals)}) "
                    + $"REFERENCES {Qualified(referencedSchema, referenced.Name)} ({ColumnList(referenced.Columns, foreignKey.ReferencedColumnOrdinals)}) "
                    + $"ON DELETE {ActionSql(foreignKey.DeleteAction)} ON UPDATE {ActionSql(foreignKey.UpdateAction)}",
                Phase = ReplayPhase.ForeignKeys,
                SortKey = foreignKey.ObjectId,
                ObjectIds = [foreignKey.ObjectId],
                CreateDate = foreignKey.CreateDate,
            });
        }
        if (rows)
            CaptureRows(table, image);
        this.Tables.Add(image);
    }

    private static void CaptureRows(HeapTable table, TableImage image)
    {
        using var rows = new MemoryStream();
        var count = 0;
        foreach (var row in table.Heap.EnumerateRows())
        {
            BackupRowCodec.WriteRow(table.StoredColumns, RowDecoder.DecodeRow(table.StoredColumns, row, table.Heap), rows);
            count++;
        }
        image.Rows = rows.ToArray();
        image.RowCount = count;
    }

    private static IndexState CaptureIndexState(string name, int indexId, byte fillFactor, bool isPadded, bool allowRowLocks, bool allowPageLocks,
        bool optimizeForSequentialKey, byte dataCompression, bool xmlCompression, bool statisticsNoRecompute, bool isDisabled,
        int filegroupId, bool keyMayExceedLimit, bool ignoreDupKey, StatisticsState statistics, DateTime modifyDate) => new()
        {
            Name = name,
            IndexId = indexId,
            FillFactor = fillFactor,
            IsPadded = isPadded,
            AllowRowLocks = allowRowLocks,
            AllowPageLocks = allowPageLocks,
            OptimizeForSequentialKey = optimizeForSequentialKey,
            DataCompression = dataCompression,
            XmlCompression = xmlCompression,
            StatisticsNoRecompute = statisticsNoRecompute,
            IsDisabled = isDisabled,
            FilegroupId = filegroupId,
            KeyMayExceedLimit = keyMayExceedLimit,
            IgnoreDupKey = ignoreDupKey,
            AutoDrop = statistics.AutoDrop,
            HasPersistedSample = statistics.HasPersistedSample,
            HasStatistics = statistics.Snapshot is not null,
            ModifyDate = modifyDate,
        };

    private static ReplayStep IndexStep(Schema schema, string tableName, HeapColumn[] columns, Index index)
    {
        var sql = new StringBuilder("CREATE ");
        if (index.IsUnique)
            _ = sql.Append("UNIQUE ");
        _ = sql.Append(index.IsClustered ? "CLUSTERED" : "NONCLUSTERED").Append(" INDEX ").Append(Quote(index.Name))
            .Append(" ON ").Append(Qualified(schema, tableName)).Append(" (")
            .Append(string.Join(", ", index.KeyColumns.Select(key => Quote(columns[key.ColumnOrdinal].Name) + (key.IsDescending ? " DESC" : " ASC"))))
            .Append(')');
        if (index.IncludedColumnOrdinals.Length != 0)
            _ = sql.Append(" INCLUDE (").Append(ColumnList(columns, index.IncludedColumnOrdinals)).Append(')');
        if (index.FilterDefinition is { } filter)
            _ = sql.Append(" WHERE ").Append(Unwrap(filter));
        return new ReplayStep
        {
            Sql = sql.ToString(),
            SortKey = index.ObjectId,
            ObjectIds = [index.ObjectId],
        };
    }

    private static ReplayStep TableTypeStep(Schema schema, TableType type)
    {
        var database = schema.Database;
        var sql = new StringBuilder("CREATE TYPE ").Append(Qualified(schema, type.Name)).Append(" AS TABLE (");
        var defaultIds = new List<int>();
        var shape = type.CatalogShape;
        for (var c = 0; c < type.Columns.Length; c++)
        {
            var column = type.Columns[c];
            if (c > 0)
                _ = sql.Append(", ");
            AppendColumn(sql, column, database);
            if (column.DefaultConstraint is { } columnDefault)
            {
                if (!columnDefault.IsSystemNamed)
                    _ = sql.Append(" CONSTRAINT ").Append(Quote(columnDefault.Name));
                _ = sql.Append(" DEFAULT ").Append(columnDefault.Definition);
                defaultIds.Add(columnDefault.ObjectId);
            }
            for (var k = 0; k < type.PendingChecks.Length; k++)
            {
                var check = type.PendingChecks[k];
                if (check.InlineColumn is { } inline && inline == column.Name)
                    AppendTypeCheck(sql, check);
            }
        }

        // The keys and indexes interleave as declared, which is the sequence
        // their ids are handed out over.
        var sequence = new List<(bool IsIndex, int Position, bool Clustered, int ObjectId)>();
        var keys = 0;
        for (var i = 0; i < type.PendingIndexes.Length; i++)
        {
            while (keys < type.PendingIndexes[i].KeysBefore && keys < type.PendingKeys.Length)
                AddKey(keys++);
            var index = type.PendingIndexes[i];
            sequence.Add((true, i, index.IsClustered, shape.Indexes.First(candidate => candidate.Name == index.Name).ObjectId));
        }
        while (keys < type.PendingKeys.Length)
            AddKey(keys++);
        foreach (var (isIndex, position, _, _) in sequence)
        {
            _ = sql.Append(", ");
            if (isIndex)
                AppendTypeIndex(sql, type.PendingIndexes[position]);
            else
                AppendTypeKey(sql, type, type.PendingKeys[position]);
        }
        foreach (var check in type.PendingChecks)
        {
            if (check.InlineColumn is null)
            {
                _ = sql.Append(", ");
                AppendTypeCheck(sql, check);
            }
        }
        _ = sql.Append(')');

        var keyIds = new List<int>();
        var clustered = sequence.FindIndex(static entry => entry.Clustered);
        if (clustered >= 0)
            keyIds.Add(sequence[clustered].ObjectId);
        for (var s = sequence.Count - 1; s >= 0; s--)
        {
            if (s != clustered)
                keyIds.Add(sequence[s].ObjectId);
        }
        return new ReplayStep
        {
            Sql = sql.ToString(),
            SortKey = type.ObjectId,
            ObjectIds = [.. defaultIds, type.ObjectId, .. keyIds, .. shape.CheckConstraints.Select(static check => check.ObjectId)],
            UserTypeIds = [type.UserTypeId],
            CreateDate = type.CreateDate,
            MayDrawMoreIds = true,
        };

        void AddKey(int position)
        {
            var (kind, _, _, clustered, _, _) = type.PendingKeys[position];
            var resolved = shape.KeyConstraints[position];
            sequence.Add((false, position, clustered ?? (kind == KeyConstraintKind.PrimaryKey && !type.PendingIndexes.Any(static index => index.IsClustered)), resolved.ObjectId));
        }
    }

    private static void AppendTypeKey(StringBuilder sql, TableType type, (KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending) key)
    {
        if (key.Name is not null)
            _ = sql.Append("CONSTRAINT ").Append(Quote(key.Name)).Append(' ');
        _ = sql.Append(key.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE");
        if (key.Clustered is { } clustered)
            _ = sql.Append(clustered ? " CLUSTERED" : " NONCLUSTERED");
        _ = sql.Append(" (");
        for (var i = 0; i < key.FullOrdinals.Length; i++)
        {
            if (i > 0)
                _ = sql.Append(", ");
            _ = sql.Append(Quote(type.Columns[key.FullOrdinals[i]].Name)).Append(key.Descending.Length > i && key.Descending[i] ? " DESC" : " ASC");
        }
        _ = sql.Append(')');
        if (key.Options.IgnoreDupKey)
            _ = sql.Append(" WITH (IGNORE_DUP_KEY = ON)");
    }

    private static void AppendTypeIndex(StringBuilder sql, Simulation.PendingInlineIndex index)
    {
        _ = sql.Append("INDEX ").Append(Quote(index.Name));
        if (index.IsUnique)
            _ = sql.Append(" UNIQUE");
        _ = sql.Append(index.IsClustered ? " CLUSTERED" : " NONCLUSTERED").Append(" (")
            .Append(string.Join(", ", index.Columns.Select(static column => Quote(column.ColumnName) + (column.IsDescending ? " DESC" : " ASC"))))
            .Append(')');
        if (index.IncludeColumnNames.Count != 0)
            _ = sql.Append(" INCLUDE (").Append(string.Join(", ", index.IncludeColumnNames.Select(Quote))).Append(')');
        if (index.FilterDefinition is { } filter)
            _ = sql.Append(" WHERE ").Append(Unwrap(filter));
    }

    private static void AppendTypeCheck(StringBuilder sql, (string? Name, Parser.BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication) check)
    {
        if (check.Name is not null)
            _ = sql.Append(" CONSTRAINT ").Append(Quote(check.Name));
        _ = sql.Append(" CHECK ");
        if (check.NotForReplication)
            _ = sql.Append("NOT FOR REPLICATION ");
        _ = sql.Append(check.Definition.StartsWith('(') ? check.Definition : $"({check.Definition})");
    }

    private static string KeySql(HeapTable table, KeyConstraint key)
    {
        var sql = new StringBuilder("CONSTRAINT ").Append(Quote(key.Name)).Append(' ')
            .Append(key.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")
            .Append(key.IsClustered ? " CLUSTERED (" : " NONCLUSTERED (");
        for (var i = 0; i < key.FullOrdinals.Length; i++)
        {
            if (i > 0)
                _ = sql.Append(", ");
            _ = sql.Append(Quote(table.Columns[key.FullOrdinals[i]].Name)).Append(key.IsDescending(i) ? " DESC" : " ASC");
        }
        _ = sql.Append(')');
        if (key.IgnoreDupKey)
            _ = sql.Append(" WITH (IGNORE_DUP_KEY = ON)");
        return sql.ToString();
    }

    private static string CheckDefinition(CheckConstraint check) =>
        check.Definition is { } definition
            ? definition.StartsWith('(') ? definition : $"({definition})"
            : throw new NotSupportedException($"BACKUP DATABASE of the CHECK constraint '{check.Name}', which has no captured definition, isn't modeled by the simulator.");

    /// <summary>A column's definition as <c>CREATE TABLE</c> takes it, without its constraints.</summary>
    private static void AppendColumn(StringBuilder sql, HeapColumn column, Database database)
    {
        _ = sql.Append(Quote(column.Name)).Append(' ');
        if (column.Computed is not null)
        {
            _ = sql.Append("AS ").Append(column.ComputedDefinition ?? throw new NotSupportedException(
                $"BACKUP DATABASE of the computed column '{column.Name}', which has no captured definition, isn't modeled by the simulator."));
            if (column.IsPersisted)
                _ = sql.Append(column.Nullable ? " PERSISTED" : " PERSISTED NOT NULL");
            return;
        }
        if (column.AliasType is { } alias)
        {
            _ = sql.Append(Qualified(alias.Schema, alias.Name));
        }
        else if (column.XmlSchemaCollection is { } collection)
        {
            var collectionSchema = database.Schemas.Select(static pair => pair.Value).First(candidate => candidate.SchemaId == collection.SchemaId);
            _ = sql.Append("xml(").Append(column.XmlDocument ? "DOCUMENT " : "CONTENT ").Append(Qualified(collectionSchema, collection.Name)).Append(')');
        }
        else
        {
            _ = sql.Append(TypeSql(column.Type, column.SpelledNumeric));
        }
        if (column.IsColumnSet)
        {
            _ = sql.Append(" COLUMN_SET FOR ALL_SPARSE_COLUMNS");
            return;
        }
        if (column.Collation is { } collation)
            _ = sql.Append(" COLLATE ").Append(collation);
        if (column.Identity is { } identity)
        {
            _ = sql.Append(" IDENTITY(").Append(identity.Seed.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(identity.Increment.ToString(CultureInfo.InvariantCulture)).Append(')');
            if (identity.NotForReplication)
                _ = sql.Append(" NOT FOR REPLICATION");
        }
        if (column.IsRowGuidCol)
            _ = sql.Append(" ROWGUIDCOL");
        if (column.IsSparse)
            _ = sql.Append(" SPARSE");
        _ = sql.Append(column.Nullable ? " NULL" : " NOT NULL");
    }

    /// <summary>A built-in type as a declaration writes it.</summary>
    private static string TypeSql(SqlType type, bool spelledNumeric) => type switch
    {
        DecimalSqlType d => string.Create(CultureInfo.InvariantCulture, $"{(spelledNumeric ? "numeric" : "decimal")}({d.precision}, {d.scale})"),
        _ => type.ToString()!,
    };

    private static string ActionSql(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetDefault => "SET DEFAULT",
        ReferentialAction.SetNull => "SET NULL",
        _ => "NO ACTION",
    };

    private static string ColumnList(HeapColumn[] columns, int[] ordinals) => string.Join(", ", ordinals.Select(ordinal => Quote(columns[ordinal].Name)));

    /// <summary>A filter definition without the one paren pair the catalog wraps it in.</summary>
    private static string Unwrap(string definition)
    {
        if (definition.Length < 2 || definition[0] != '(' || definition[^1] != ')')
            return definition;
        var depth = 0;
        var quoted = '\0';
        for (var i = 0; i < definition.Length; i++)
        {
            var c = definition[i];
            if (quoted != '\0')
            {
                if (c == quoted)
                    quoted = '\0';
                continue;
            }
            switch (c)
            {
                case '\'':
                    quoted = '\'';
                    break;
                case '[':
                    quoted = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0 && i != definition.Length - 1)
                        return definition;
                    break;
            }
        }
        return definition[1..^1];
    }

    internal static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Qualified(Schema schema, string name) => $"{Quote(schema.Name)}.{Quote(name)}";
}
