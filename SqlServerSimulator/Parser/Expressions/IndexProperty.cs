using SqlServerSimulator.Storage;
using Index = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>INDEXPROPERTY(object_id, 'index_name', 'property')</c>: per-index
/// metadata flags. Returns <c>int</c>; NULL on any NULL arg, unknown index
/// name (within the resolved table), unknown property, or unknown
/// <c>object_id</c>. Property names and index names are
/// case-insensitive.
/// </summary>
/// <remarks>
/// <para>
/// Index lookup combines two sources on the resolved <see cref="HeapTable"/>:
/// <see cref="HeapTable.Indexes"/> (named indexes from CREATE INDEX) and
/// <see cref="HeapTable.KeyConstraints"/> (PK + UNIQUE constraints — these
/// surface in <c>sys.indexes</c> by constraint name).
/// </para>
/// <para>
/// Shipped properties:
/// <list type="bullet">
/// <item><description><c>IsClustered</c> / <c>IsUnique</c> — flags from
/// <see cref="Index.IsClustered"/> / <see cref="Index.IsUnique"/> (or
/// <see cref="KeyConstraint.Kind"/> for PK / UNIQUE constraints).</description></item>
/// <item><description><c>IsAutoStatistics</c>, <c>IndexDepth</c>,
/// <c>IndexFillFactor</c>, <c>IsHypothetical</c>, <c>IsPadIndex</c>,
/// <c>IsStatistics</c>, <c>IsFulltextKey</c>,
/// <c>IsOptimizedForSequentialKey</c> — always 0 (no B-tree storage;
/// matches probed real-server behavior on a freshly-created index with
/// no stats).</description></item>
/// </list>
/// </para>
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
        var indexValue = this.indexNameArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (idValue.IsNull || indexValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        var indexName = indexValue.CoerceTo(SqlType.NVarchar).AsString;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        if (ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, id) is not HeapTable table)
            return SqlValue.Null(SqlType.Int32);

        // Resolve as either a CREATE INDEX-declared index or a PK / UNIQUE
        // constraint-backed index. KeyConstraint also surfaces in sys.indexes
        // under the constraint's auto-generated name (e.g. PK__<table8>__<hex>).
        bool isUnique, isClustered;
        if (FindIndex(table, indexName) is Index idx)
        {
            isUnique = idx.IsUnique;
            isClustered = idx.IsClustered;
        }
        else if (FindKeyConstraint(table, indexName) is KeyConstraint kc)
        {
            isUnique = true;
            isClustered = kc.Kind == KeyConstraintKind.PrimaryKey;
        }
        else
        {
            return SqlValue.Null(SqlType.Int32);
        }

        return EvaluateIndexProperty(isUnique, isClustered, prop) is int result
            ? SqlValue.FromInt32(result)
            : SqlValue.Null(SqlType.Int32);
    }

    private static Index? FindIndex(HeapTable table, string name)
    {
        foreach (var idx in table.Indexes)
        {
            if (Collation.Baseline.Equals(idx.Name, name))
                return idx;
        }
        return null;
    }

    private static KeyConstraint? FindKeyConstraint(HeapTable table, string name)
    {
        foreach (var kc in table.KeyConstraints)
        {
            if (Collation.Baseline.Equals(kc.Name, name))
                return kc;
        }
        return null;
    }

    private static int? EvaluateIndexProperty(bool isUnique, bool isClustered, string property)
    {
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            8 => upper switch { "ISUNIQUE" => isUnique ? 1 : 0, _ => null },
            10 => upper switch
            {
                "INDEXDEPTH" => 0,
                "ISPADINDEX" => 0,
                _ => null,
            },
            11 => upper switch
            {
                "ISCLUSTERED" => isClustered ? 1 : 0,
                _ => null,
            },
            12 => upper switch
            {
                "ISSTATISTICS" => 0,
                _ => null,
            },
            13 => upper switch
            {
                // 0 for every modeled index — the full-text KEY index isn't
                // surfaced through INDEXPROPERTY (probe-confirmed 0 on a
                // non-full-text-key index).
                "ISFULLTEXTKEY" => 0,
                _ => null,
            },
            14 => upper switch
            {
                "ISHYPOTHETICAL" => 0,
                _ => null,
            },
            15 => upper switch
            {
                "INDEXFILLFACTOR" => 0,
                _ => null,
            },
            16 => upper switch
            {
                "ISAUTOSTATISTICS" => 0,
                _ => null,
            },
            27 => upper switch
            {
                "ISOPTIMIZEDFORSEQUENTIALKEY" => 0,
                _ => null,
            },
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"INDEXPROPERTY({this.idArg.DebugDisplay()}, {this.indexNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
