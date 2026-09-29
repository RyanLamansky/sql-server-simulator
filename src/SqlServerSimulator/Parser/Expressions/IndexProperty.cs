using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>INDEXPROPERTY(object_id, 'index_name', 'property')</c>: per-index
/// metadata flags. Returns <c>int</c>; NULL on any NULL arg, unknown index
/// name (within the resolved table), unknown property, or unknown
/// <c>object_id</c>. Property names and index names are
/// case-insensitive.
/// </summary>
/// <remarks>
/// The name resolves against a table's key constraints, indexes, XML and
/// spatial indexes and user statistics, or an indexed view's indexes. Every
/// documented property answers as real does (probed 2026-09-26 against SQL
/// Server 2025 over each of those kinds): the option flags read what the
/// declaration and later <c>ALTER INDEX</c> recorded, a columnstore index
/// disallows row and page locks, a statistic answers <c>IsStatistics</c> 1 and
/// <c>IndexID</c> 0, and an XML or spatial index answers NULL for
/// <c>IndexDepth</c>.
/// </remarks>
internal sealed class IndexProperty : Expression
{
    private readonly Expression idArg;
    private readonly Expression indexNameArg;
    private readonly Expression propertyArg;

    public IndexProperty(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.indexNameArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        ScalarArguments.RequireIntegerArgument(idValue, this.idArg.ResultReportsNumeric, 1, "indexproperty");
        var indexValue = this.indexNameArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (idValue.IsNull || indexValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = ScalarArguments.CoerceToInt(idValue);
        var indexName = indexValue.CoerceTo(SqlType.NVarchar).AsString;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        return Resolve(ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, id), indexName) is { } found
            && Evaluate(found, prop) is int result
                ? SqlValue.FromInt32(result)
                : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>What an index name resolved to, with the answers every property reads.</summary>
    private readonly struct FoundIndex(
        int indexId, bool isUnique, bool isClustered, bool isColumnstore, bool isDisabled, byte fillFactor, bool isPadded,
        bool allowRowLocks, bool allowPageLocks, bool optimizeForSequentialKey, bool isFullTextKey, bool isStatistics, bool hasDepth)
    {
        public readonly int IndexId = indexId;
        public readonly bool IsUnique = isUnique;
        public readonly bool IsClustered = isClustered;
        public readonly bool IsColumnstore = isColumnstore;
        public readonly bool IsDisabled = isDisabled;
        public readonly byte FillFactor = fillFactor;
        public readonly bool IsPadded = isPadded;
        public readonly bool AllowRowLocks = allowRowLocks;
        public readonly bool AllowPageLocks = allowPageLocks;
        public readonly bool OptimizeForSequentialKey = optimizeForSequentialKey;
        public readonly bool IsFullTextKey = isFullTextKey;
        public readonly bool IsStatistics = isStatistics;
        public readonly bool HasDepth = hasDepth;
    }

    private static FoundIndex? Resolve(Schemas.SchemaObject? owner, string name)
    {
        var (identities, table) = owner switch
        {
            HeapTable heapTable => (heapTable.IndexIdentities(), heapTable),
            Schemas.View view => (view.IndexIdentities(), null),
            _ => (null, null),
        };
        if (identities is null)
            return null;
        var fullTextKey = table?.FullTextIndex?.KeyIndexName;
        foreach (var identity in identities)
        {
            if (identity.Constraint is { } key && Collation.Baseline.Equals(key.Name, name))
            {
                return new(identity.IndexId, true, key.IsClustered, false, key.IsDisabled, key.FillFactor, key.IsPadded,
                    key.AllowRowLocks, key.AllowPageLocks, key.OptimizeForSequentialKey,
                    fullTextKey is not null && Collation.Baseline.Equals(fullTextKey, key.Name), false, true);
            }
            if (identity.Index is { } index && Collation.Baseline.Equals(index.Name, name))
            {
                return new(identity.IndexId, index.IsUnique, index.IsClustered, index.IsColumnstore, index.IsDisabled, index.FillFactor, index.IsPadded,
                    index.AllowRowLocks && !index.IsColumnstore, index.AllowPageLocks && !index.IsColumnstore, index.OptimizeForSequentialKey,
                    fullTextKey is not null && Collation.Baseline.Equals(fullTextKey, index.Name), false, true);
            }
        }
        if (table is null)
            return null;
        foreach (var xmlIndex in table.XmlIndexes)
        {
            if (Collation.Baseline.Equals(xmlIndex.Name, name))
                return Auxiliary(xmlIndex.IndexId);
        }
        foreach (var spatialIndex in table.SpatialIndexes)
        {
            if (Collation.Baseline.Equals(spatialIndex.Name, name))
                return Auxiliary(spatialIndex.IndexId);
        }
        foreach (var jsonIndex in table.JsonIndexes)
        {
            if (Collation.Baseline.Equals(jsonIndex.Name, name))
                return Auxiliary(jsonIndex.IndexId);
        }
        foreach (var vectorIndex in table.VectorIndexes)
        {
            if (Collation.Baseline.Equals(vectorIndex.Name, name))
                return Auxiliary(vectorIndex.IndexId);
        }
        foreach (var statistic in table.UserStatistics)
        {
            if (Collation.Baseline.Equals(statistic.Name, name))
                return new(0, false, false, false, false, 0, false, true, true, false, false, true, true);
        }
        return null;

        static FoundIndex Auxiliary(int indexId) => new(indexId, false, false, false, false, 0, false, true, true, false, false, false, false);
    }

    private static int? Evaluate(FoundIndex found, string property)
    {
        Span<char> upper = stackalloc char[property.Length];
        return upper[..property.AsSpan().ToUpperInvariant(upper)] switch
        {
            "INDEXDEPTH" => found.HasDepth ? 0 : null,
            "INDEXFILLFACTOR" => found.FillFactor,
            "INDEXID" => found.IndexId,
            "ISAUTOSTATISTICS" or "ISHYPOTHETICAL" => 0,
            "ISCLUSTERED" => Flag(found.IsClustered),
            "ISCOLUMNSTORE" => Flag(found.IsColumnstore),
            "ISDISABLED" => Flag(found.IsDisabled),
            "ISFULLTEXTKEY" => Flag(found.IsFullTextKey),
            "ISOPTIMIZEDFORSEQUENTIALKEY" => Flag(found.OptimizeForSequentialKey),
            "ISPADINDEX" => Flag(found.IsPadded),
            "ISPAGELOCKDISALLOWED" => Flag(!found.AllowPageLocks),
            "ISROWLOCKDISALLOWED" => Flag(!found.AllowRowLocks),
            "ISSTATISTICS" => Flag(found.IsStatistics),
            "ISUNIQUE" => Flag(found.IsUnique),
            _ => null,
        };
    }

    private static int Flag(bool value) => value ? 1 : 0;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"INDEXPROPERTY({this.idArg.DebugDisplay()}, {this.indexNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg).Child(this.indexNameArg).Child(this.propertyArg);
}
