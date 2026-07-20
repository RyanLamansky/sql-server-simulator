using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FULLTEXTCATALOGPROPERTY('catalog_name', 'property')</c>: returns an
/// <c>int</c> property of a full-text catalog. The simulator has no full-text
/// indexing engine, so the population / size / status properties report the
/// idle-empty answers a freshly created catalog gives (all <c>0</c>);
/// <c>AccentSensitivity</c> reflects the catalog's DDL-captured
/// <c>ACCENT_SENSITIVITY</c> option (see <see cref="Schemas.FullTextCatalog"/>).
/// An unknown catalog or unrecognized property returns NULL; property names are
/// case-insensitive. All probe-confirmed against SQL Server 2025 (2026-07-20).
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
            "ITEMCOUNT" => SqlValue.FromInt32(0),
            "LOGSIZE" => SqlValue.FromInt32(0),
            "MERGESTATUS" => SqlValue.FromInt32(0),
            "POPULATECOMPLETIONAGE" => SqlValue.FromInt32(0),
            "POPULATESTATUS" => SqlValue.FromInt32(0),
            "UNIQUEKEYCOUNT" => SqlValue.FromInt32(0),
            _ => SqlValue.Null(SqlType.Int32),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"FULLTEXTCATALOGPROPERTY({this.catalogArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void VisitColumnReferences(Action<MultiPartName> visit)
    {
        this.catalogArg.VisitColumnReferences(visit);
        this.propertyArg.VisitColumnReferences(visit);
    }
}
