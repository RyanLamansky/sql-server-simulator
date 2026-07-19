using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COLUMNPROPERTY(table_or_proc_id, 'column_or_param_name', 'property')</c>:
/// per-column metadata flags / counts for a table column. Returns <c>int</c>;
/// unknown property / column / id / NULL on any arg → NULL (matches real
/// SQL Server, probe-confirmed 2026-05-23). Property names and column names
/// are both case-insensitive.
/// </summary>
/// <remarks>
/// <para>
/// Shipped properties (all probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description><c>AllowsNull</c> — 1 / 0 from <see cref="HeapColumn.Nullable"/>.</description></item>
/// <item><description><c>IsIdentity</c> — 1 / 0 from <see cref="HeapColumn.Identity"/>.</description></item>
/// <item><description><c>IsComputed</c> — 1 / 0 from <see cref="HeapColumn.Computed"/>.</description></item>
/// <item><description><c>IsRowGuidCol</c> — 1 / 0 from <see cref="HeapColumn.IsRowGuidCol"/>.</description></item>
/// <item><description><c>IsIdNotForRepl</c> — 1 when the column is an IDENTITY
/// declared NOT FOR REPLICATION, else 0 (0 on non-identity columns, matching
/// real, probe-confirmed 2026-07-17).</description></item>
/// <item><description><c>Precision</c> — type-dependent: integer family yields
/// (decimal-equivalent precision), <c>varchar(N)</c> / <c>nvarchar(N)</c> yield
/// <c>N</c>, money family yields 19 / 10.</description></item>
/// <item><description><c>Scale</c> — type-dependent: integer family yields 0,
/// money yields 4, decimal yields the declared scale.</description></item>
/// <item><description><c>CharMaxLen</c> — <c>N</c> for <c>varchar(N)</c> /
/// <c>nvarchar(N)</c>; NULL for non-character types (matches real, which
/// returns -1 on the catalog view but NULL through COLUMNPROPERTY).</description></item>
/// <item><description><c>ColumnId</c> — 1-based declaration ordinal in
/// <see cref="HeapTable.Columns"/>.</description></item>
/// <item><description><c>UsesAnsiTrim</c> — 1 for character types (the
/// simulator's ANSI-trim behavior is always on), 0 otherwise.</description></item>
/// </list>
/// Unsupported / physical-storage properties (<c>IsDeterministic</c>,
/// <c>IsIndexable</c>, <c>IsPrecise</c>, <c>IsSparse</c>, <c>IsColumnSet</c>,
/// <c>StatisticalSemantics</c>, <c>GeneratedAlwaysType</c>) return NULL —
/// callers reading them on a real server with no stats / column-set also get
/// NULL, so this matches the common case.
/// </para>
/// </remarks>
internal sealed class ColumnProperty : Expression
{
    private readonly Expression idArg;
    private readonly Expression columnArg;
    private readonly Expression propertyArg;

    public ColumnProperty(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.columnArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        var columnValue = this.columnArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (idValue.IsNull || columnValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        var columnName = columnValue.CoerceTo(SqlType.NVarchar).AsString;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        if (ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, id) is not HeapTable table)
            return SqlValue.Null(SqlType.Int32);

        var (column, ordinal) = FindColumn(table, columnName);
        return column is null
            ? SqlValue.Null(SqlType.Int32)
            : EvaluateColumnProperty(column, ordinal, prop) is int result
                ? SqlValue.FromInt32(result)
                : SqlValue.Null(SqlType.Int32);
    }

    private static (HeapColumn? Column, int Ordinal) FindColumn(HeapTable table, string columnName)
    {
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (Collation.Baseline.Equals(table.Columns[i].Name, columnName))
                return (table.Columns[i], i + 1);
        }
        return (null, 0);
    }

    private static int? EvaluateColumnProperty(HeapColumn column, int ordinal, string property)
    {
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            5 => upper switch { "SCALE" => GetScale(column.Type), _ => null },
            8 => upper switch
            {
                "COLUMNID" => ordinal,
                _ => null,
            },
            9 => upper switch
            {
                "PRECISION" => GetPrecision(column),
                _ => null,
            },
            10 => upper switch
            {
                "ALLOWSNULL" => column.Nullable ? 1 : 0,
                "CHARMAXLEN" => GetCharMaxLen(column),
                "ISCOMPUTED" => column.Computed is null ? 0 : 1,
                "ISIDENTITY" => column.Identity is null ? 0 : 1,
                _ => null,
            },
            12 => upper switch
            {
                "ISROWGUIDCOL" => column.IsRowGuidCol ? 1 : 0,
                "USESANSITRIM" => SqlType.IsStringCategory(column.Type) ? 1 : 0,
                _ => null,
            },
            14 => upper switch
            {
                "ISIDNOTFORREPL" => column.Identity is { NotForReplication: true } ? 1 : 0,
                _ => null,
            },
            _ => null,
        };
    }

    private static int GetPrecision(HeapColumn column) => column.Type switch
    {
        { Category: SqlTypeCategory.Integer } t => SqlType.IntegerAsDecimal(t).Precision,
        { Category: SqlTypeCategory.Money } t => SqlType.MoneyAsDecimal(t).Precision,
        DecimalSqlType d => d.precision,
        _ when SqlType.IsStringCategory(column.Type)
            && column.MaxLength is int n
            && n != SqlType.MaxLengthSentinel => n,
        _ => 0,
    };

    private static int GetScale(SqlType type) => type switch
    {
        { Category: SqlTypeCategory.Money } t => SqlType.MoneyAsDecimal(t).Scale,
        DecimalSqlType d => d.scale,
        _ => 0,
    };

    private static int? GetCharMaxLen(HeapColumn column) =>
        SqlType.IsStringCategory(column.Type) && column.MaxLength is int n && n != SqlType.MaxLengthSentinel
            ? n
            : null;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"COLUMNPROPERTY({this.idArg.DebugDisplay()}, {this.columnArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
