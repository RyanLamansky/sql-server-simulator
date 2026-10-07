using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FULLTEXTCATALOGPROPERTY('catalog_name', 'property')</c>: returns an
/// <c>int</c> property of a full-text catalog.
/// <c>ItemCount</c> counts the rows the catalog's indexes cover and
/// <c>UniqueKeyCount</c> the distinct terms in them — both computed by reading
/// those tables, which is how the search pipeline answers everything else (see
/// <c>docs/claude/full-text.md</c>). <c>AccentSensitivity</c> reflects the
/// catalog's DDL-captured <c>ACCENT_SENSITIVITY</c> option. The size / status /
/// age properties report the idle answers real gives a settled catalog
/// (<c>0</c>), since nothing here is crawled in the background.
/// An unknown catalog or unrecognized property returns NULL; property names are
/// case-insensitive. All probe-confirmed against SQL Server 2025 with Full-Text
/// installed.
/// Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/fulltextcatalogproperty-transact-sql
/// </summary>
internal sealed class FullTextCatalogProperty : Expression
{
    private readonly Expression catalogArg;
    private readonly Expression propertyArg;

    public FullTextCatalogProperty(ParserContext context)
    {
        this.catalogArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var catalogValue = this.catalogArg.Run(runtime);
        var propertyValue = this.propertyArg.Run(runtime);
        if (catalogValue.IsNull || propertyValue.IsNull)
            return SqlValue.Null(SqlType.Int32);

        var catalogName = catalogValue.CoerceTo(SqlType.NVarchar).AsString;
        if (!runtime.Batch.CurrentDatabase.FullTextCatalogs.TryGetValue(catalogName, out var catalog))
            return SqlValue.Null(SqlType.Int32);

        var property = propertyValue.CoerceTo(SqlType.NVarchar).AsString;
        if (property.Length > 32)
            return SqlValue.Null(SqlType.Int32);
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "ACCENTSENSITIVITY" => SqlValue.FromInt32(catalog.IsAccentSensitive ? 1 : 0),
            "IMPORTSTATUS" => SqlValue.FromInt32(0),
            "INDEXSIZE" => SqlValue.FromInt32(0),
            "ITEMCOUNT" => SqlValue.FromInt32(CountIndexedRows(runtime, catalog.Id)),
            "LOGSIZE" => SqlValue.FromInt32(0),
            "MERGESTATUS" => SqlValue.FromInt32(0),
            "POPULATECOMPLETIONAGE" => SqlValue.FromInt32(0),
            "POPULATESTATUS" => SqlValue.FromInt32(0),
            "UNIQUEKEYCOUNT" => SqlValue.FromInt32(CountDistinctTerms(runtime, catalog)),
            _ => SqlValue.Null(SqlType.Int32),
        };
    }

    /// <summary>
    /// Rows covered by every full-text index attached to this catalog — real's
    /// <c>ItemCount</c>, which counts indexed rows rather than terms.
    /// </summary>
    private static int CountIndexedRows(RuntimeContext runtime, int catalogId)
    {
        var total = 0;
        foreach (var table in IndexedTables(runtime, catalogId))
            total += table.Heap.RowCount;
        return total;
    }

    /// <summary>
    /// Real's <c>UniqueKeyCount</c>: each index's distinct terms across its
    /// columns, plus the <c>END OF FILE</c> key an index with any row holds —
    /// a row of NULLs included — summed over the catalog's indexes, so a term
    /// two tables share counts twice (probed 2026-10-06 against SQL Server
    /// 2025). Stopwords are excluded because they never enter an index that
    /// applies the stoplist.
    /// </summary>
    private static int CountDistinctTerms(RuntimeContext runtime, Schemas.FullTextCatalog catalog)
    {
        var count = 0;
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in IndexedTables(runtime, catalog.Id))
        {
            var index = table.FullTextIndex!;
            terms.Clear();
            var endOfFile = 0;
            foreach (var bytes in table.Heap.EnumerateRows())
            {
                endOfFile = 1;
                foreach (var column in index.Columns)
                {
                    var ordinal = column.ColumnId - 1;
                    if (ordinal < 0 || ordinal >= table.Columns.Length || !table.Columns[ordinal].IsStored)
                        continue;
                    var value = RowDecoder.DecodeColumn(table.StoredColumns, bytes, table.StorageOrdinals[ordinal], table.Heap);
                    var extension = column.TypeColumnId is int typeColumnId && typeColumnId - 1 < table.Columns.Length && table.Columns[typeColumnId - 1].IsStored
                        ? RowDecoder.DecodeColumn(table.StoredColumns, bytes, table.StorageOrdinals[typeColumnId - 1], table.Heap)
                        : (SqlValue?)null;
                    if (FullText.FullTextBinding.TextOf(value, extension) is not { } text)
                        continue;
                    var language = FullText.FullTextLanguage.For(column.LanguageId);
                    foreach (var term in FullText.FullTextWordBreaker.Break(text, catalog.IsAccentSensitive))
                    {
                        if (index.StoplistOff || !language.IsNoise(term.Text))
                            _ = terms.Add(term.Text);
                    }
                }
            }
            count += terms.Count + endOfFile;
        }
        return count;
    }

    private static IEnumerable<HeapTable> IndexedTables(RuntimeContext runtime, int catalogId)
    {
        foreach (var (_, schema) in runtime.Batch.CurrentDatabase.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.FullTextIndex?.CatalogId == catalogId)
                    yield return table;
            }
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"FULLTEXTCATALOGPROPERTY({this.catalogArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.catalogArg).Child(this.propertyArg);
}
