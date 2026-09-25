using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COLUMNPROPERTY(table_or_proc_id, 'column_or_param_name', 'property')</c>:
/// per-column metadata flags / counts for a column or module parameter (see
/// <see cref="FindColumn"/> for what the id may name). Returns <c>int</c>;
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
/// <item><description><c>ColumnId</c> — the stable
/// <see cref="HeapColumn.ColumnId"/>, agreeing with
/// <c>sys.columns.column_id</c> after a DROP COLUMN rather than tracking the
/// column's shifted position (probe-confirmed that real reports the same
/// value from both surfaces).</description></item>
/// <item><description><c>UsesAnsiTrim</c> — 1 for character types (the
/// simulator's ANSI-trim behavior is always on), 0 otherwise.</description></item>
/// </list>
/// Unsupported / physical-storage properties (<c>IsDeterministic</c>,
/// <c>IsIndexable</c>, <c>IsPrecise</c>, <c>StatisticalSemantics</c>,
/// <c>GeneratedAlwaysType</c>) return NULL —
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
        ScalarArguments.RequireIntegerArgument(idValue, this.idArg.ResultReportsNumeric, 1, "columnproperty");
        var columnValue = this.columnArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (idValue.IsNull || columnValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = ScalarArguments.CoerceToInt(idValue);
        var columnName = columnValue.CoerceTo(SqlType.NVarchar).AsString;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        var (column, ordinal) = FindColumn(runtime.Batch.CurrentDatabase, id, columnName);
        return column is null
            ? SqlValue.Null(SqlType.Int32)
            : EvaluateColumnProperty(column, ordinal, prop) is int result
                ? SqlValue.FromInt32(result)
                : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>
    /// The column or parameter <paramref name="name"/> names on object
    /// <paramref name="id"/>, with its <c>ColumnId</c>: a table's, view's or
    /// catalog view's column, a table-valued function's result column, or —
    /// written with its <c>@</c> — a procedure's or function's parameter,
    /// whose id is its position and which always allows NULL (probed
    /// 2026-09-25 against SQL Server 2025).
    /// </summary>
    private static (HeapColumn? Column, int Ordinal) FindColumn(Database database, int id, string name)
    {
        if (name.StartsWith('@'))
        {
            var parameterName = name[1..];
            switch (ObjectProperty.FindObject(database, id))
            {
                case Procedure procedure:
                    for (var i = 0; i < procedure.Parameters.Length; i++)
                    {
                        var parameter = procedure.Parameters[i];
                        if (Collation.Baseline.Equals(parameter.Name, parameterName))
                            return (new HeapColumn(name, parameter.Type, parameter.DeclaredMaxLength, nullable: true), i + 1);
                    }
                    break;
                case UserDefinedFunction function:
                    for (var i = 0; i < function.Parameters.Length; i++)
                    {
                        var parameter = function.Parameters[i];
                        if (Collation.Baseline.Equals(parameter.Name, parameterName))
                            return (new HeapColumn(name, parameter.Type, maxLength: null, nullable: true), i + 1);
                    }
                    break;
            }
            return (null, 0);
        }

        var columns = ObjectProperty.FindObject(database, id) switch
        {
            HeapTable table => table.Columns,
            View view => view.OutputColumns,
            InlineTableValuedFunction inline => inline.OutputColumns,
            MultiStatementTableValuedFunction multiStatement => multiStatement.OutputColumns,
            null => CatalogViewColumns(id),
            _ => null,
        };
        for (var i = 0; i < columns?.Length; i++)
        {
            var column = columns[i];
            if (Collation.Baseline.Equals(column.Name, name))
                return (column, column.ColumnId == 0 ? i + 1 : column.ColumnId);
        }
        return (null, 0);
    }

    private static HeapColumn[]? CatalogViewColumns(int id)
    {
        foreach (var view in Simulation.CatalogViews.Values)
        {
            if (view.ObjectId == id)
                return view.Columns;
        }
        return null;
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
                "ISSPARSE" => column.IsSparse ? 1 : 0,
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
            11 => upper switch
            {
                // Column sets aren't modeled, so no column is one.
                "ISCOLUMNSET" => 0,
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
        _ => DeclaredCharacterLength(column.Type) ?? 0,
    };

    /// <summary>
    /// The character length a string type declares, for a column that
    /// carries no separate <see cref="HeapColumn.MaxLength"/> — a view's or a
    /// catalog view's output, a function parameter.
    /// </summary>
    private static int? DeclaredCharacterLength(SqlType type) => type switch
    {
        VarcharSqlType { length: > 0 and var n } => n,
        NVarcharSqlType { length: > 0 and var n } => n,
        CharSqlType fixedChar => fixedChar.length,
        NCharSqlType fixedNChar => fixedNChar.length,
        SystemNameSqlType => 128,
        _ => null,
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
            : DeclaredCharacterLength(column.Type);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"COLUMNPROPERTY({this.idArg.DebugDisplay()}, {this.columnArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg).Child(this.columnArg).Child(this.propertyArg);
}
