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
/// <c>IsDeterministic</c> / <c>IsPrecise</c> answer 1 or 0 for a computed
/// column and NULL otherwise, as do <c>IsSystemVerified</c> (1) and the two
/// data-access properties (0); <c>IsIndexable</c> is 0 for a MAX or LOB
/// type, else 1 for a stored column and, for a computed one, 1 when it is
/// deterministic and either persisted or precise (probed 2026-09-26 against
/// SQL Server 2025 under the default SET options, which that answer also
/// weighs on real). <c>IsFulltextIndexed</c> / <c>IsXmlIndexable</c> are 0.
/// Unsupported / physical-storage properties (<c>StatisticalSemantics</c>,
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

        var database = runtime.Batch.CurrentDatabase;
        var (column, ordinal, scope) = FindColumn(database, id, columnName);
        return column is null
            ? SqlValue.Null(SqlType.Int32)
            : EvaluateColumnProperty(column, ordinal, prop) is int result
                ? SqlValue.FromInt32(result)
                : EvaluateIndexingProperty(database, ObjectProperty.FindObject(database, id) as HeapTable, scope, column, prop) is int computed
                    ? SqlValue.FromInt32(computed)
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
    private static (HeapColumn? Column, int Ordinal, HeapColumn[] Scope) FindColumn(Database database, int id, string name)
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
                            return (new HeapColumn(name, parameter.Type, parameter.DeclaredMaxLength, nullable: true), i + 1, []);
                    }
                    break;
                case UserDefinedFunction function:
                    for (var i = 0; i < function.Parameters.Length; i++)
                    {
                        var parameter = function.Parameters[i];
                        if (Collation.Baseline.Equals(parameter.Name, parameterName))
                            return (new HeapColumn(name, parameter.Type, maxLength: null, nullable: true), i + 1, []);
                    }
                    break;
            }
            return (null, 0, []);
        }

        var columns = ColumnsOf(database, id);
        for (var i = 0; i < columns?.Length; i++)
        {
            var column = columns[i];
            if (Collation.Baseline.Equals(column.Name, name))
                return (column, column.ColumnId == 0 ? i + 1 : column.ColumnId, columns);
        }
        return (null, 0, []);
    }

    /// <summary>
    /// The columns object <paramref name="id"/> carries — a table's, view's,
    /// table-valued function's result, table type's or catalog view's — or
    /// null for anything else. A column's <c>column_id</c> is its
    /// <see cref="HeapColumn.ColumnId"/> where it has one and its position
    /// otherwise.
    /// </summary>
    internal static HeapColumn[]? ColumnsOf(Database database, int id)
    {
        var columns = ObjectProperty.FindObject(database, id) switch
        {
            HeapTable table => table.Columns,
            View view => view.OutputColumns,
            InlineTableValuedFunction inline => inline.OutputColumns,
            MultiStatementTableValuedFunction multiStatement => multiStatement.OutputColumns,
            null => CatalogViewColumns(id),
            _ => null,
        };
        if (columns is not null)
            return columns;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var tableType in schema.TableTypes.Values)
            {
                if (tableType.ObjectId == id)
                    return tableType.Columns;
            }
        }
        return null;
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
                "ISIDENTITY" => column.Identity is not null || column.IdentitySource is not null ? 1 : 0,
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

    // The indexing properties: full-text membership, XML-index eligibility and those
    // reading a computed column's expression.
    private static int? EvaluateIndexingProperty(Database database, HeapTable? table, HeapColumn[] scope, HeapColumn column, string property)
    {
        Span<char> upper = stackalloc char[property.Length];
        var name = upper[..property.AsSpan().ToUpperInvariant(upper)];
        switch (name)
        {
            case "ISFULLTEXTINDEXED":
                return table?.FullTextIndex?.Columns.Exists(entry => entry.ColumnId == column.ColumnId) is true ? 1 : 0;
            case "ISXMLINDEXABLE":
                return table is null ? null : column.Type is XmlSqlType ? 1 : 0;
        }
        if (column.Computed is null || column.ComputedDefinition is not { } definition)
            return name is "ISINDEXABLE" ? (column.IsLob ? 0 : 1) : null;
        return name switch
        {
            "ISDETERMINISTIC" => Schemas.ModuleDeterminism.IsComputedColumnDeterministic(database, scope, definition) ? 1 : 0,
            "ISINDEXABLE" => !column.IsLob
                && column.Type is not (VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel })
                && Schemas.ModuleDeterminism.IsComputedColumnDeterministic(database, scope, definition)
                && (column.IsPersisted || Schemas.ComputedColumnPrecision.IsPrecise(scope, column.Type, definition)) ? 1 : 0,
            "ISPRECISE" => Schemas.ComputedColumnPrecision.IsPrecise(scope, column.Type, definition) ? 1 : 0,
            "ISSYSTEMVERIFIED" => 1,
            "SYSTEMDATAACCESS" or "USERDATAACCESS" => 0,
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
