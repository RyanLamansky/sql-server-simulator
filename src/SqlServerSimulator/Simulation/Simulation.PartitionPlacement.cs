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
    /// <c>TEXTIMAGE_ON name</c>. A name takes any identifier form, so SSMS's
    /// bracketed <c>[PRIMARY]</c> passes. No-op when neither keyword is next.
    /// Cursor on exit: the first token past the clauses.
    /// </summary>
    internal static DataSpaceClause? ParseOptionalDataSpaceClause(ParserContext context, out bool textImageOn)
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
        textImageOn = context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.TextImage_On };
        if (textImageOn)
        {
            if (context.GetNextRequired() is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
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
    internal static void PlaceNewTable(BatchContext batch, HeapTable table, DataSpaceClause? written, bool textImageOn)
    {
        var rows = written is null ? null : ResolveDataSpaceClause(batch, written, table);
        if (textImageOn && rows is not null)
            throw SimulatedSqlException.TextImageOnPartitionedTable();
        table.Partitioning = rows;
        foreach (var key in table.KeyConstraints)
        {
            if (!key.IsClustered)
                continue;
            table.Partitioning = key.WrittenDataSpace is { } clustered ? ResolveDataSpaceClause(batch, clustered, table) : rows;
            RequirePartitionColumnInUniqueKey(table.Partitioning, table, key.FullOrdinals, key.Name, isConstraint: true);
        }
        foreach (var key in table.KeyConstraints)
        {
            if (key.IsClustered)
                continue;
            key.Partitioning = PlacementFor(batch, table, key.WrittenDataSpace);
            RequirePartitionColumnInUniqueKey(key.Partitioning, table, key.FullOrdinals, key.Name, isConstraint: true);
        }
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
        if (index.IsClustered)
            table.Partitioning = placement;
        else
            index.Partitioning = placement;
    }

    /// <summary>
    /// The placement of the index <paramref name="identity"/> names: the
    /// table's for its heap or clustered index, else the index's or key's own.
    /// </summary>
    internal static PartitionPlacement? PlacementOf(HeapTable table, IndexIdentity identity) =>
        identity.IndexId <= 1 ? table.Partitioning : identity.Constraint?.Partitioning ?? identity.Index?.Partitioning;

    /// <summary>
    /// <c>ALTER DATABASE … ADD FILEGROUP name [CONTAINS …]</c> and <c>REMOVE
    /// FILEGROUP name</c>, entered on <c>ADD</c> / <c>REMOVE</c>. A filegroup is
    /// catalog metadata only: adding one registers the name (Msg 5035 when it
    /// is taken), removing one drops it (Msg 5014 when it doesn't exist,
    /// Msg 5042 for <c>PRIMARY</c> and while a partition scheme maps a
    /// partition — or its next-used slot — to it, the class-0 Msg 5044 when
    /// done). The file forms, <c>ADD [LOG] FILE (…)
    /// [TO FILEGROUP name]</c> and <c>REMOVE FILE name</c>, parse and change
    /// nothing, there being no file model.
    /// </summary>
    private static bool TryParseAlterDatabaseFilegroup(ParserContext context, Database target, bool add)
    {
        var word = context.GetNextRequired();
        if (word is Name { Value: var logWord } && add && BuiltInToken.Equals(logWord, "LOG"))
            word = context.GetNextRequired();
        if (word is ReservedKeyword { Keyword: Keyword.File })
        {
            if (!add)
            {
                if (context.GetNextRequired() is not Name)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
                return true;
            }
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            SkipBalancedParens(context);
            context.MoveNextOptional();
            while (context.Token is Operator { Character: ',' })
            {
                if (context.GetNextRequired() is not Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                SkipBalancedParens(context);
                context.MoveNextOptional();
            }
            if (context.Token is ReservedKeyword { Keyword: Keyword.To })
            {
                if (context.GetNextRequired() is not Name { Value: var toWord } || !BuiltInToken.Equals(toWord, "FILEGROUP"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Name)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
            }
            return true;
        }
        if (word is not Name { Value: var kind } || !BuiltInToken.Equals(kind, "FILEGROUP"))
            return false;
        if (context.GetNextRequired() is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        if (add && context.Token is ReservedKeyword { Keyword: Keyword.Contains })
        {
            if (context.GetNextRequired() is not (Name or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        if (context.Batch.IsSkipping)
            return true;

        if (add)
        {
            if (target.Filegroups.ContainsKey(name.Value))
                throw SimulatedSqlException.FilegroupAlreadyExists(name.Value);
            _ = target.RegisterFilegroup(name.Value);
            return true;
        }
        if (!target.Filegroups.TryGetValue(name.Value, out var dataSpaceId))
            throw SimulatedSqlException.FilegroupDoesNotExist(name.Value, target.Name);
        if (dataSpaceId == Database.PrimaryFilegroupId)
            throw SimulatedSqlException.FilegroupNotEmpty(name.Value, state: 6);
        if (target.PartitionSchemes.Values.Any(scheme => scheme.NextUsed == dataSpaceId || scheme.Destinations.Contains(dataSpaceId)))
            throw SimulatedSqlException.FilegroupNotEmpty(name.Value, state: 12);
        _ = target.Filegroups.TryRemove(name.Value, out _);
        context.Batch.AppendInfoError(0, 1, 5044, SimulatedSqlException.FilegroupRemovedMessage(name.Value));
        return true;
    }
}
