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
/// Every documented property answers as real does (probed 2026-09-26 against
/// SQL Server 2025 over table, view, table-valued-function-result and
/// parameter sites and every built-in type); see <see cref="Evaluate"/> for
/// which sites each property concerns.
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
        return FindColumn(database, id, columnName) is { } found && Evaluate(database, found, prop) is int result
            ? SqlValue.FromInt32(result)
            : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>What a column or parameter name resolved to on its object.</summary>
    private enum ColumnSite
    {
        /// <summary>A table's or table type's column.</summary>
        Table,

        /// <summary>A view's or catalog view's column.</summary>
        View,

        /// <summary>A table-valued function's result column.</summary>
        FunctionResult,

        /// <summary>A procedure's or function's parameter, or a scalar function's return value.</summary>
        Parameter,
    }

    private readonly struct FoundColumn(HeapColumn column, int columnId, ColumnSite site, HeapColumn[] scope, HeapTable? table, bool isOutput = false, bool isCursor = false, View? view = null, int position = -1)
    {
        public readonly HeapColumn Column = column;
        public readonly int ColumnId = columnId;
        public readonly ColumnSite Site = site;
        public readonly HeapColumn[] Scope = scope;
        public readonly HeapTable? Table = table;

        /// <summary>The view a view-site column belongs to, null for a catalog view's or any other site's.</summary>
        public readonly View? View = view;

        /// <summary>The column's position among its object's columns.</summary>
        public readonly int Position = position;
        public readonly bool IsOutput = isOutput;
        public readonly bool IsCursor = isCursor;
    }

    /// <summary>
    /// The column or parameter <paramref name="name"/> names on object
    /// <paramref name="id"/>: a table's, view's or catalog view's column, a
    /// table-valued function's result column, or — written with its <c>@</c> —
    /// a procedure's or function's parameter, whose id is its position and
    /// which always allows NULL, and the empty name a scalar function's return
    /// value, id 0 (probed 2026-09-25 / 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static FoundColumn? FindColumn(Database database, int id, string name)
    {
        var owner = ObjectProperty.FindObject(database, id);
        if (name.StartsWith('@'))
        {
            var parameterName = name[1..];
            switch (owner)
            {
                case Procedure procedure:
                    for (var i = 0; i < procedure.Parameters.Length; i++)
                    {
                        var parameter = procedure.Parameters[i];
                        if (Collation.Baseline.Equals(parameter.Name, parameterName))
                        {
                            return new(new HeapColumn(name, parameter.Type, parameter.DeclaredMaxLength, nullable: true), i + 1, ColumnSite.Parameter, [], null,
                                isOutput: parameter.IsOutput, isCursor: parameter.IsCursor);
                        }
                    }
                    break;
                case UserDefinedFunction function:
                    for (var i = 0; i < function.Parameters.Length; i++)
                    {
                        var parameter = function.Parameters[i];
                        if (Collation.Baseline.Equals(parameter.Name, parameterName))
                            return new(new HeapColumn(name, parameter.Type, maxLength: null, nullable: true), i + 1, ColumnSite.Parameter, [], null);
                    }
                    break;
            }
            return null;
        }
        if (name.Length == 0)
        {
            return owner is ScalarFunction scalar
                ? new(new HeapColumn(name, scalar.ReturnType, maxLength: null, nullable: true), 0, ColumnSite.Parameter, [], null, isOutput: true)
                : null;
        }

        var site = owner switch
        {
            HeapTable => ColumnSite.Table,
            InlineTableValuedFunction or MultiStatementTableValuedFunction or ClrTableValuedFunction => ColumnSite.FunctionResult,
            View or null => ColumnSite.View,
            _ => ColumnSite.Table,
        };
        var columns = ColumnsOf(database, id);
        for (var i = 0; i < columns?.Length; i++)
        {
            var column = columns[i];
            if (Collation.Baseline.Equals(column.Name, name))
            {
                // A table type's columns answer as a table's; a catalog view's
                // as a view's.
                var resolvedSite = owner is null && !Simulation.CatalogViews.Values.Any(view => view.ObjectId == id) ? ColumnSite.Table : site;
                return new(column, column.ColumnId == 0 ? i + 1 : column.ColumnId, resolvedSite, columns, owner as HeapTable, view: owner as View, position: i);
            }
        }
        return null;
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
            ClrTableValuedFunction clr => clr.OutputColumns,
            null => CatalogViewColumns(id),
            _ => null,
        };
        if (columns is not null)
            return columns;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, tableType) in schema.TableTypes)
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

    /// <summary>
    /// One property of a resolved column or parameter (probed 2026-09-26
    /// against SQL Server 2025, every documented property over table, view,
    /// function-result and parameter sites and every built-in type). The type
    /// geometry — <c>Precision</c>, <c>Scale</c>, <c>CharMaxLen</c>,
    /// <c>UsesAnsiTrim</c> — and the parameter flags answer everywhere; the
    /// column-only flags answer NULL for a parameter; the computed-expression
    /// properties answer only for a computed table column or a view column;
    /// <c>IsIndexable</c> only for a table or view column; and
    /// <c>IsXmlIndexable</c> only for a table column.
    /// </summary>
    private static int? Evaluate(Database database, FoundColumn found, string property)
    {
        var column = found.Column;
        var isColumn = found.Site != ColumnSite.Parameter;
        Span<char> upper = stackalloc char[property.Length];
        return upper[..property.AsSpan().ToUpperInvariant(upper)] switch
        {
            "ALLOWSNULL" => Flag(column.Nullable),
            "CHARMAXLEN" => CharMaxLen(column),
            "COLUMNID" => found.ColumnId,
            // A full-text indexed column's type column, or -1 when it has none.
            "FULLTEXTTYPECOLUMN" => isColumn ? FullTextTypeColumn(found) : null,
            "GENERATEDALWAYSTYPE" => isColumn ? (int)column.GeneratedAs : null,
            // Column sets aren't modeled, so no column is one.
            "ISCOLUMNSET" or "STATISTICALSEMANTICS" => isColumn ? 0 : null,
            "ISCOMPUTED" => isColumn ? Flag(column.Computed is not null) : null,
            "ISCURSORTYPE" => Flag(found.IsCursor),
            "ISDETERMINISTIC" or "ISPRECISE" or "ISSYSTEMVERIFIED" or "SYSTEMDATAACCESS" or "USERDATAACCESS" => ExpressionProperty(database, found, upper[..property.Length]),
            "ISFULLTEXTINDEXED" => isColumn ? Flag(found.Table?.FullTextIndex?.Columns.Exists(entry => entry.ColumnId == column.ColumnId) is true) : null,
            "ISHIDDEN" => isColumn ? Flag(column.IsHidden) : null,
            "ISIDENTITY" => isColumn ? Flag(column.Identity is not null || column.IdentitySource is not null) : null,
            "ISIDNOTFORREPL" => isColumn ? Flag(column.Identity is { NotForReplication: true }) : null,
            "ISINDEXABLE" => found.Site switch
            {
                ColumnSite.Table => IsIndexable(database, found),
                ColumnSite.View => IsViewColumnIndexable(database, found),
                _ => null,
            },
            "ISMASKED" => isColumn ? Flag(column.MaskingFunction is not null) : null,
            "ISOUTPARAM" => Flag(found.IsOutput),
            "ISROWGUIDCOL" => isColumn ? Flag(column.IsRowGuidCol) : null,
            "ISSPARSE" => isColumn ? Flag(column.IsSparse) : null,
            "ISXMLINDEXABLE" => found.Site == ColumnSite.Table ? Flag(column.Type is XmlSqlType) : null,
            "PRECISION" => Precision(column),
            "SCALE" => Scale(column.Type),
            // The types ANSI_PADDING governs: the single-byte strings, the
            // binary pair and sql_variant.
            "USESANSITRIM" => column.Type is CharSqlType or VarcharSqlType or BinarySqlType or VarbinarySqlType or SqlVariantSqlType or VectorSqlType ? 1 : null,
            _ => null,
        };
    }

    private static int Flag(bool value) => value ? 1 : 0;

    private static int FullTextTypeColumn(FoundColumn found)
    {
        var entries = found.Table?.FullTextIndex?.Columns;
        var at = entries?.FindIndex(entry => entry.ColumnId == found.Column.ColumnId) ?? -1;
        return at < 0 ? 0 : entries![at].TypeColumnId ?? -1;
    }

    /// <summary>
    /// The properties reading a column's expression: a computed table column's
    /// own answers, and for a view's column the constants real gives a view
    /// that isn't schema-bound — neither deterministic, precise nor verified,
    /// and reading both user and system data. A schema-bound view's column is
    /// verified, reads no data of either kind, is deterministic as its view is,
    /// and precise unless it is float or real (probed 2026-10-02 against SQL
    /// Server 2025).
    /// </summary>
    private static int? ExpressionProperty(Database database, FoundColumn found, ReadOnlySpan<char> name)
    {
        if (found.Site == ColumnSite.View)
        {
            if (found.View is not { IsSchemaBound: true } view)
                return name is "SYSTEMDATAACCESS" or "USERDATAACCESS" ? 1 : 0;
            return name switch
            {
                "ISDETERMINISTIC" => Schemas.ModuleDeterminism.Evaluate(database, view),
                "ISPRECISE" => Flag(found.Column.Type is not (FloatSqlType or RealSqlType)),
                "ISSYSTEMVERIFIED" => 1,
                _ => 0,
            };
        }
        if (found.Site != ColumnSite.Table || found.Column.Computed is null || found.Column.ComputedDefinition is not { } definition)
            return null;
        return name switch
        {
            "ISDETERMINISTIC" => Flag(Schemas.ModuleDeterminism.IsComputedColumnDeterministic(database, found.Scope, definition)),
            "ISPRECISE" => Flag(Schemas.ComputedColumnPrecision.IsPrecise(found.Scope, found.Column.Type, definition)),
            "ISSYSTEMVERIFIED" => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// A view column's <c>IsIndexable</c>: 0 unless the view is schema-bound,
    /// then — as a table's column — 0 for a MAX, LOB, xml, spatial or vector
    /// type, and otherwise 1 for a deterministic column that is precise or
    /// passes a base column through unchanged (probed 2026-10-02 against SQL
    /// Server 2025: a float column read straight answers 1, a float expression
    /// 0).
    /// </summary>
    private static int IsViewColumnIndexable(Database database, FoundColumn found)
    {
        if (found.View is not { IsSchemaBound: true } view || IsUnindexableType(found.Column))
            return 0;
        var passesThrough = found.Position >= 0
            && (view.BaseColumnOrdinals.Length > found.Position ? view.BaseColumnOrdinals[found.Position] >= 0
                : view.DerivedOutputColumns is { } derived && derived.Length > found.Position && !derived[found.Position]);
        return Flag(Schemas.ModuleDeterminism.Evaluate(database, view) == 1
            && (passesThrough || found.Column.Type is not (FloatSqlType or RealSqlType)));
    }

    private static bool IsUnindexableType(HeapColumn column) =>
        column.IsLob || column.Type is XmlSqlType or GeographySqlType or GeometrySqlType or VectorSqlType
            or VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel };

    /// <summary>
    /// A table column's <c>IsIndexable</c>: 0 for a MAX, LOB, xml or spatial
    /// type or a vector, else 1 for a stored column and, for a computed one, 1 when it is
    /// deterministic and either persisted or precise (probed 2026-09-26 against
    /// SQL Server 2025 under the default SET options, which that answer also
    /// weighs on real).
    /// </summary>
    private static int IsIndexable(Database database, FoundColumn found)
    {
        var column = found.Column;
        if (IsUnindexableType(column))
            return 0;
        if (column.Computed is null || column.ComputedDefinition is not { } definition)
            return 1;
        return Flag(Schemas.ModuleDeterminism.IsComputedColumnDeterministic(database, found.Scope, definition)
            && (column.IsPersisted || Schemas.ComputedColumnPrecision.IsPrecise(found.Scope, column.Type, definition)));
    }

    /// <summary>
    /// <c>Precision</c>: a number's decimal precision (53 for float, 24 for
    /// real), a date / time type's character width, a string's or binary's
    /// declared length (-1 for MAX), a vector's storage length, and each
    /// remaining type's fixed width.
    /// </summary>
    internal static int Precision(HeapColumn column) => column.Type switch
    {
        { Category: SqlTypeCategory.Integer } t => SqlType.IntegerAsDecimal(t).Precision,
        { Category: SqlTypeCategory.Money } t => SqlType.MoneyAsDecimal(t).Precision,
        DecimalSqlType d => d.precision,
        FloatSqlType => 53,
        RealSqlType => 24,
        DateSqlType => 10,
        TimeSqlType time => 8 + FractionWidth(time.precision),
        DateTimeSqlType => 23,
        SmallDateTimeSqlType => 16,
        DateTime2SqlType dateTime2 => 19 + FractionWidth(dateTime2.precision),
        DateTimeOffsetSqlType offset => 26 + FractionWidth(offset.precision),
        UniqueIdentifierSqlType => 16,
        RowVersionSqlType => 8,
        VectorSqlType vector => vector.ByteLength,
        _ => CharMaxLen(column) ?? 0,
    };

    private static int FractionWidth(int precision) => precision > 0 ? precision + 1 : 0;

    /// <summary>
    /// <c>Scale</c>: a number's, a time type's fractional digits, 3 for
    /// datetime; NULL for every type that has none.
    /// </summary>
    internal static int? Scale(SqlType type) => type switch
    {
        BitSqlType => null,
        { Category: SqlTypeCategory.Integer } => 0,
        { Category: SqlTypeCategory.Money } t => SqlType.MoneyAsDecimal(t).Scale,
        DecimalSqlType d => d.scale,
        TimeSqlType time => time.precision,
        DateTime2SqlType dateTime2 => dateTime2.precision,
        DateTimeOffsetSqlType offset => offset.precision,
        DateTimeSqlType => 3,
        DateSqlType or SmallDateTimeSqlType => 0,
        _ => null,
    };

    /// <summary>
    /// <c>CharMaxLen</c>: a string's or binary's declared length in characters
    /// or bytes, -1 for MAX, xml and the spatial pair, the legacy LOB types'
    /// maxima, and hierarchyid's and sql_variant's fixed answers; NULL for
    /// every other type.
    /// </summary>
    private static int? CharMaxLen(HeapColumn column) => column.Type switch
    {
        TextSqlType or ImageSqlType => int.MaxValue,
        NTextSqlType => int.MaxValue / 2,
        XmlSqlType or JsonSqlType or GeographySqlType or GeometrySqlType => -1,
        HierarchyIdSqlType => 892,
        ClrUdtSqlType udt => udt.Udt.MaxByteSize,
        SqlVariantSqlType => 0,
        SystemNameSqlType => 128,
        // A vector's storage length, as its Precision (probed 2026-10-02 against SQL Server 2025).
        VectorSqlType vector => vector.ByteLength,
        CharSqlType or VarcharSqlType or NCharSqlType or NVarcharSqlType or BinarySqlType or VarbinarySqlType =>
            column.MaxLength is int n ? (n == SqlType.MaxLengthSentinel ? -1 : n) : DeclaredLength(column.Type),
        _ => null,
    };

    /// <summary>
    /// The length a string or binary type declares, for a column that carries
    /// no separate <see cref="HeapColumn.MaxLength"/> — a view's or a catalog
    /// view's output, a function parameter.
    /// </summary>
    private static int DeclaredLength(SqlType type) => type switch
    {
        VarcharSqlType { length: var n } => n == SqlType.MaxLengthSentinel ? -1 : n,
        NVarcharSqlType { length: var n } => n == SqlType.MaxLengthSentinel ? -1 : n,
        VarbinarySqlType { length: var n } => n == SqlType.MaxLengthSentinel ? -1 : n,
        CharSqlType fixedChar => fixedChar.length,
        NCharSqlType fixedNChar => fixedNChar.length,
        BinarySqlType fixedBinary => fixedBinary.length,
        _ => 0,
    };

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"COLUMNPROPERTY({this.idArg.DebugDisplay()}, {this.columnArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg).Child(this.columnArg).Child(this.propertyArg);
}
