using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Placing a table or an index on a partition scheme: the ON clause's parse,
// its resolution when the statement runs, and the checks real makes of the
// partition column (probed 2026-09-27 against SQL Server 2025).
partial class Simulation
{
    /// <summary>
    /// Skips trailing <c>ON &lt;filegroup&gt;</c> and <c>TEXTIMAGE_ON &lt;filegroup&gt;</c>
    /// placement clauses where the placement isn't recorded.
    /// </summary>
    internal static void SkipOptionalFilegroupClause(ParserContext context) =>
        _ = ParseOptionalDataSpaceClause(context, out _);

    /// <summary>
    /// Parses the trailing placement clauses of a table, an index or a key
    /// constraint: <c>ON name</c> or <c>ON scheme(column [, …])</c>, then
    /// <c>TEXTIMAGE_ON name</c>, whose name comes back in
    /// <paramref name="textImageOn"/>. A name takes any identifier form, so
    /// SSMS's bracketed <c>[PRIMARY]</c> passes. No-op when neither keyword is
    /// next. Cursor on exit: the first token past the clauses.
    /// </summary>
    internal static DataSpaceClause? ParseOptionalDataSpaceClause(ParserContext context, out string? textImageOn)
    {
        DataSpaceClause? clause = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            if (context.GetNextRequired() is not Name name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            List<string>? columns = null;
            if (context.GetNextOptional() is Operator { Character: '(' })
            {
                columns = [];
                do
                {
                    if (context.GetNextRequired() is not Name column)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    columns.Add(column.Value);
                    context.MoveNextRequired();
                } while (context.Token is Operator { Character: ',' });
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
            }
            clause = new DataSpaceClause(name.Value, columns);
        }
        textImageOn = null;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.TextImage_On })
        {
            if (context.GetNextRequired() is not Name textImageName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            textImageOn = textImageName.Value;
            context.MoveNextOptional();
        }
        return clause;
    }

    /// <summary>
    /// Resolves a placement clause against <paramref name="table"/>: a scheme
    /// with its partition column, or null for a filegroup. A name with a column
    /// list must be a scheme (Msg 1921), and the list must name one column
    /// once (Msg 2703, then 2726) that exists (Msg 1911), is persisted if
    /// computed (Msg 7724) and has exactly the function's parameter type
    /// (Msg 7726) and collation (Msg 7727). A scheme written without a column
    /// list is Msg 2726, and an unknown filegroup Msg 1921.
    /// </summary>
    /// <remarks>
    /// A filegroup is one the database registered — <c>PRIMARY</c>, one its
    /// <c>CREATE DATABASE</c> or an <c>ALTER DATABASE … ADD FILEGROUP</c>
    /// declared, or one a BACPAC carried — or the <c>"default"</c> keyword;
    /// any other name is Msg 1921.
    /// </remarks>
    internal static PartitionPlacement? ResolveDataSpaceClause(BatchContext batch, DataSpaceClause clause, HeapTable table)
    {
        var database = table.OwningDatabase ?? batch.Connection.Simulation.Databases[TempdbDatabaseName];
        if (clause.Columns is not { } columns)
        {
            return database.PartitionSchemes.TryGetValue(clause.Name, out var unlisted)
                ? throw SimulatedSqlException.PartitionColumnCountMismatch(unlisted.Function.Name)
                : database.Filegroups.ContainsKey(clause.Name) || BuiltInToken.Equals(clause.Name, "default")
                    ? null
                    : throw SimulatedSqlException.InvalidDataSpace(scheme: false, clause.Name);
        }
        if (!database.PartitionSchemes.TryGetValue(clause.Name, out var scheme))
            throw SimulatedSqlException.InvalidDataSpace(scheme: true, clause.Name);
        var collation = database.Collation;
        for (var i = 1; i < columns.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (collation.Equals(columns[i], columns[j]))
                    throw SimulatedSqlException.PartitionColumnDuplicate(columns[i]);
            }
        }
        var function = scheme.Function;
        if (columns.Count != 1)
            throw SimulatedSqlException.PartitionColumnCountMismatch(function.Name);
        var column = Array.Find(table.Columns, candidate => collation.Equals(candidate.Name, columns[0]))
            ?? throw SimulatedSqlException.PartitionColumnNotFound(columns[0]);
        if (column.Computed is not null && !column.IsPersisted)
            throw SimulatedSqlException.PartitionColumnNotPersisted(column.Name, table.Name);
        var parameterType = function.ParameterType;
        var parameterColumn = new HeapColumn(string.Empty, parameterType, function.DeclaredMaxLength, nullable: true);
        if (column.Type.SystemTypeId != parameterType.SystemTypeId
            || BuiltInResources.GetSysColumnMetadata(column) != BuiltInResources.GetSysColumnMetadata(parameterColumn))
        {
            throw SimulatedSqlException.PartitionColumnTypeMismatch(column.Name, PartitionTypeText(column.Type), function.Name, PartitionTypeText(parameterType));
        }
        if (SqlType.IsCollatedString(parameterType) && !string.Equals(column.Type.Collation?.Name, parameterType.Collation?.Name, StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.PartitionColumnCollationMismatch(column.Name, function.Name);
        return new PartitionPlacement(scheme, column);
    }

    /// <summary>How the partition errors write a type: <c>int</c>, <c>varchar(10)</c>.</summary>
    private static string PartitionTypeText(SqlType type) => type.ToString()!.Replace("(MAX)", "(max)", StringComparison.Ordinal);

    /// <summary>
    /// Where an index or key constraint of <paramref name="table"/> lands: its
    /// own <c>ON</c> clause, else — aligned — wherever the table's rows are.
    /// </summary>
    internal static PartitionPlacement? PlacementFor(BatchContext batch, HeapTable table, DataSpaceClause? written) =>
        written is null ? table.Partitioning : ResolveDataSpaceClause(batch, written, table);

    /// <summary>
    /// Refuses a unique index or key placed on a scheme whose partition column
    /// isn't among its key columns (Msg 1908, followed by Msg 1750 for a
    /// constraint).
    /// </summary>
    internal static void RequirePartitionColumnInUniqueKey(PartitionPlacement? placement, HeapTable table, int[] keyFullOrdinals, string indexName, bool isConstraint)
    {
        if (placement is null || Array.IndexOf(keyFullOrdinals, Array.IndexOf(table.Columns, placement.Column)) >= 0)
            return;
        var error = SimulatedSqlException.PartitionColumnNotInUniqueKey(placement.Column.Name, indexName);
        throw isConstraint ? SimulatedSqlException.FollowedByConstraintNotCreated(error) : error;
    }

    /// <summary>
    /// Places a table a <c>CREATE TABLE</c> built, and the key constraints it
    /// declared: the table's own <c>ON</c> clause places its rows, a clustered
    /// key's own clause overrides that, and a nonclustered key follows its own
    /// clause or the rows. <c>TEXTIMAGE_ON</c> on a partitioned table is
    /// Msg 1707.
    /// </summary>
    internal static void PlaceNewTable(BatchContext batch, HeapTable table, DataSpaceClause? written, string? textImageOn, bool fileStreamOn = false)
    {
        var rows = written is null ? null : ResolveDataSpaceClause(batch, written, table);
        if (textImageOn is not null && rows is not null)
            throw SimulatedSqlException.TextImageOnPartitionedTable();
        table.Partitioning = rows;
        // Without an ON clause the rows land on the default filegroup
        // (probed 2026-09-28 against SQL Server 2025).
        var database = DatabaseOf(batch, table);
        table.FilegroupId = written is null ? database.DefaultFilegroupId : FilegroupFor(batch, table, written);
        foreach (var key in table.KeyConstraints)
        {
            if (!key.IsClustered)
                continue;
            table.Partitioning = key.WrittenDataSpace is { } clustered ? ResolveDataSpaceClause(batch, clustered, table) : rows;
            table.FilegroupId = FilegroupFor(batch, table, key.WrittenDataSpace);
            RequirePartitionColumnInUniqueKey(table.Partitioning, table, key.FullOrdinals, key.Name, isConstraint: true);
        }
        foreach (var key in table.KeyConstraints)
        {
            if (key.IsClustered)
                continue;
            key.Partitioning = PlacementFor(batch, table, key.WrittenDataSpace);
            key.FilegroupId = FilegroupFor(batch, table, key.WrittenDataSpace);
            RequirePartitionColumnInUniqueKey(key.Partitioning, table, key.FullOrdinals, key.Name, isConstraint: true);
        }
        table.LobFilegroupId = table.FilegroupId;
        if (textImageOn is not null)
        {
            if (!table.HasLobColumn())
                throw SimulatedSqlException.TextImageOnWithoutLobColumn();
            table.LobFilegroupId = ResolveWritableFilegroup(database, textImageOn);
        }
        if (fileStreamOn)
            throw SimulatedSqlException.FileStreamOnWithoutFileStreamColumns();
    }

    /// <summary>The database a table's placement names filegroups of — its own, or <c>tempdb</c> for a temporary one.</summary>
    private static Database DatabaseOf(BatchContext batch, HeapTable table) =>
        table.OwningDatabase ?? batch.Connection.Simulation.Databases[TempdbDatabaseName];

    /// <summary>
    /// The filegroup a placement clause puts rows on: the table's rows' own
    /// when the clause is absent or names a scheme, else the named filegroup
    /// (<c>"default"</c> reading the default one), which a new table or index
    /// may not be created on while it is read-only (Msg 1924).
    /// </summary>
    internal static int FilegroupFor(BatchContext batch, HeapTable table, DataSpaceClause? written) =>
        written is null || written.Columns is not null || DatabaseOf(batch, table).PartitionSchemes.ContainsKey(written.Name)
            ? table.FilegroupId
            : ResolveWritableFilegroup(DatabaseOf(batch, table), written.Name);

    /// <summary>
    /// A filegroup named by a placement clause: a registered one or
    /// <c>"default"</c> (else Msg 1921), and not read-only (Msg 1924).
    /// </summary>
    private static int ResolveWritableFilegroup(Database database, string name)
    {
        var id = BuiltInToken.Equals(name, "default") ? database.DefaultFilegroupId
            : database.Filegroups.TryGetValue(name, out var registered) ? registered
            : throw SimulatedSqlException.InvalidDataSpace(scheme: false, name);
        return database.IsFilegroupReadOnly(id) ? throw SimulatedSqlException.FilegroupIsReadOnly(FilegroupName(database, id)) : id;
    }

    /// <summary>The name <paramref name="dataSpaceId"/> is registered under.</summary>
    internal static string FilegroupName(Database database, int dataSpaceId)
    {
        foreach (var (name, id) in database.Filegroups)
        {
            if (id == dataSpaceId)
                return name;
        }
        return "PRIMARY";
    }

    /// <summary>
    /// Refuses a write to <paramref name="table"/> reaching a rowset on a
    /// read-only filegroup (Msg 652, naming the heap as <c>""</c>) or, for an
    /// <c>INSERT</c> or <c>MERGE</c>, on a filegroup without files (Msg 622) —
    /// settled once per statement rather than per row written, so a statement
    /// that writes no row is refused too. An <c>UPDATE</c> reaches a
    /// nonclustered index only through the <paramref name="updatedColumns"/>
    /// it keys or includes (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static void RejectWriteToUnwritableFilegroup(HeapTable table, BatchContext batch, string verb, IReadOnlyList<int>? updatedColumns = null)
    {
        if (batch.IsSkipping || batch.CreateTimeBinding || table.Partitioning is not null || table.IsTableVariable)
            return;
        var anyElsewhere = table.FilegroupId != Database.PrimaryFilegroupId;
        foreach (var index in table.Indexes)
            anyElsewhere |= !index.IsClustered && index.FilegroupId != Database.PrimaryFilegroupId;
        foreach (var key in table.KeyConstraints)
            anyElsewhere |= !key.IsClustered && key.FilegroupId != Database.PrimaryFilegroupId;
        if (!anyElsewhere)
            return;

        var database = DatabaseOf(batch, table);
        var inserting = verb is "INSERT" or "MERGE";
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.IndexId > 1 && (PlacementOf(table, identity) is not null || (updatedColumns is not null && !IndexCoversAny(identity, updatedColumns))))
                continue;
            var filegroup = FilegroupOf(table, identity);
            if (filegroup == Database.PrimaryFilegroupId)
                continue;
            if (database.IsFilegroupReadOnly(filegroup))
            {
                throw SimulatedSqlException.RowsetOnReadOnlyFilegroup(
                    identity.Name ?? "", SchemaQualifyTableName(table, database), PartitionCensus.UnpartitionedId(table, identity.IndexId), FilegroupName(database, filegroup));
            }
            if (inserting && FileCount(database, filegroup) == 0)
                throw SimulatedSqlException.FilegroupHasNoFiles(FilegroupName(database, filegroup));
        }
    }

    /// <summary>Whether the nonclustered index or key <paramref name="identity"/> keys or includes one of <paramref name="columns"/>.</summary>
    private static bool IndexCoversAny(IndexIdentity identity, IReadOnlyList<int> columns)
    {
        foreach (var column in columns)
        {
            if (identity.Constraint is { } key && Array.IndexOf(key.FullOrdinals, column) >= 0)
                return true;
            if (identity.Index is { } index
                && (Array.Exists(index.KeyColumns, keyColumn => keyColumn.ColumnOrdinal == column) || Array.IndexOf(index.IncludedColumnOrdinals, column) >= 0))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Refuses an index about to be built on <paramref name="filegroupId"/>
    /// over a table that has rows while that filegroup has no files (Msg 622,
    /// which ends the statement).
    /// </summary>
    internal static void RejectIndexOnEmptyFilegroup(BatchContext batch, HeapTable table, int filegroupId)
    {
        if (filegroupId == Database.PrimaryFilegroupId || table.Heap.RowCount == 0)
            return;
        var database = DatabaseOf(batch, table);
        if (FileCount(database, filegroupId) != 0)
            return;
        var error = SimulatedSqlException.FilegroupHasNoFiles(FilegroupName(database, filegroupId));
        error.EndedColumnRewrite = true;
        throw error;
    }

    /// <summary>
    /// Places an index just added to <paramref name="table"/>: a clustered one
    /// moves the table's rows onto its placement, a nonclustered one keeps its
    /// own. A unique one must key on the partition column (Msg 1908).
    /// </summary>
    internal static void PlaceNewIndex(BatchContext batch, HeapTable table, Storage.Index index)
    {
        var placement = PlacementFor(batch, table, index.WrittenDataSpace);
        if (index.IsUnique)
            RequirePartitionColumnInUniqueKey(placement, table, [.. index.KeyColumns.Select(static key => key.ColumnOrdinal)], index.Name, isConstraint: false);
        var filegroup = FilegroupFor(batch, table, index.WrittenDataSpace);
        if (index.IsClustered)
        {
            table.Partitioning = placement;
            table.FilegroupId = filegroup;
        }
        else
        {
            index.Partitioning = placement;
            index.FilegroupId = filegroup;
        }
    }

    /// <summary>
    /// The placement of the index <paramref name="identity"/> names: the
    /// table's for its heap or clustered index, else the index's or key's own.
    /// </summary>
    internal static PartitionPlacement? PlacementOf(HeapTable table, IndexIdentity identity) =>
        identity.IndexId <= 1 ? table.Partitioning : identity.Constraint?.Partitioning ?? identity.Index?.Partitioning;

    /// <summary>
    /// The filegroup of the index <paramref name="identity"/> names, read where
    /// <see cref="PlacementOf"/> is null: the table's rows' for its heap or
    /// clustered index, else the index's or key's own.
    /// </summary>
    internal static int FilegroupOf(HeapTable table, IndexIdentity identity) =>
        identity.IndexId <= 1 ? table.FilegroupId : identity.Constraint?.FilegroupId ?? identity.Index?.FilegroupId ?? table.FilegroupId;

    /// <summary>
    /// Whether a table, one of its indexes or its LOB data is placed on
    /// <paramref name="dataSpaceId"/> — with <paramref name="withRowsOnly"/>,
    /// a table that holds rows or the pages deleted ones left — which keeps the filegroup (Msg 5042 state 8)
    /// or its last file (state 1) from being removed (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal static bool FilegroupHoldsObjects(Database database, int dataSpaceId, bool withRowsOnly)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                // A table keeps its pages when its rows are deleted.
                if (withRowsOnly && table.Heap.RowCount == 0 && table.Heap.Pages.Count == 0)
                    continue;
                if (table.Partitioning is null && table.FilegroupId == dataSpaceId)
                    return true;
                if (table.HasLobColumn() && table.LobFilegroupId == dataSpaceId)
                    return true;
                foreach (var index in table.Indexes)
                {
                    if (!index.IsClustered && index.Partitioning is null && index.FilegroupId == dataSpaceId)
                        return true;
                }
                foreach (var key in table.KeyConstraints)
                {
                    if (!key.IsClustered && key.Partitioning is null && key.FilegroupId == dataSpaceId)
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// <c>ALTER DATABASE … ADD FILEGROUP name [CONTAINS …]</c> and <c>REMOVE
    /// FILEGROUP name</c>, entered on <c>ADD</c> / <c>REMOVE</c>, which also
    /// route the file forms. Adding a filegroup registers the name (Msg 5035
    /// when it is taken); removing one drops it (Msg 5014 when it doesn't
    /// exist, Msg 5042 for <c>PRIMARY</c>, while it has files, and while a
    /// partition scheme maps a partition — or its next-used slot — to it, the
    /// class-0 Msg 5044 when done). A read-only database refuses either with a
    /// plain Msg 3906 (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static bool TryParseAlterDatabaseFilegroup(ParserContext context, Database target, bool add)
    {
        var word = context.GetNextRequired();
        if (word is Name { Value: var logWord } && add && BuiltInToken.Equals(logWord, "LOG"))
        {
            return context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.File }
                ? TryParseAlterDatabaseAddFile(context, target, isLog: true)
                : throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (word is ReservedKeyword { Keyword: Keyword.File })
            return add ? TryParseAlterDatabaseAddFile(context, target, isLog: false) : TryParseAlterDatabaseRemoveFile(context, target);
        if (word is not Name { Value: var kind } || !BuiltInToken.Equals(kind, "FILEGROUP"))
            return false;
        if (context.GetNextRequired() is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        var memoryOptimized = false;
        if (add && context.Token is ReservedKeyword { Keyword: Keyword.Contains })
        {
            if (context.GetNextRequired() is not (Name or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            memoryOptimized = context.Token is Name { Value: var contentKind } && BuiltInToken.Equals(contentKind, "MEMORY_OPTIMIZED_DATA");
            context.MoveNextOptional();
        }
        if (context.Batch.IsSkipping)
            return true;

        target.RejectWriteWhenReadOnly();
        if (add)
        {
            if (target.Filegroups.ContainsKey(name.Value))
                throw SimulatedSqlException.FilegroupAlreadyExists(name.Value);
            if (memoryOptimized && target.MemoryOptimizedFilegroupId != 0)
                throw SimulatedSqlException.SecondMemoryOptimizedFilegroup();
            var registered = target.RegisterFilegroup(name.Value);
            if (memoryOptimized)
                target.MemoryOptimizedFilegroupId = registered;
            return true;
        }
        if (!target.Filegroups.TryGetValue(name.Value, out var dataSpaceId))
            throw SimulatedSqlException.FilegroupDoesNotExist(name.Value, target.Name);
        if (dataSpaceId == Database.PrimaryFilegroupId)
            throw SimulatedSqlException.FilegroupNotEmpty(name.Value, state: 6);
        // A MEMORY_OPTIMIZED_DATA filegroup goes only with its database (probed
        // 2026-10-02 against SQL Server 2025).
        if (dataSpaceId == target.MemoryOptimizedFilegroupId)
            throw SimulatedSqlException.FilegroupNotEmpty(name.Value, state: 8);
        if (FileCount(target, dataSpaceId) > 0)
            throw SimulatedSqlException.FilegroupHasFiles(name.Value);
        if (target.PartitionSchemes.EnumerateValues().Any(scheme => scheme.NextUsed == dataSpaceId || scheme.Destinations.Contains(dataSpaceId)))
            throw SimulatedSqlException.FilegroupNotEmpty(name.Value, state: 12);
        if (FilegroupHoldsObjects(target, dataSpaceId, withRowsOnly: false))
            throw SimulatedSqlException.FilegroupNotEmpty(name.Value, state: 8);
        lock (target.Filegroups)
        {
            _ = target.Filegroups.TryRemove(name.Value, out _);
            _ = target.ReadOnlyFilegroups.Remove(dataSpaceId);
            _ = target.AutogrowAllFilesFilegroups.Remove(dataSpaceId);
        }
        context.Batch.AppendInfoError(0, 1, 5044, SimulatedSqlException.FilegroupRemovedMessage(name.Value));
        return true;
    }
}
