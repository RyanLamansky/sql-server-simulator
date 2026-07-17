using System.Xml.Linq;

namespace SqlServerSimulator.Bacpac;

/// <summary>
/// Per-table fluent surface returned by
/// <see cref="BacpacBuilder.Table(string, string, Action{TableBuilder})"/>.
/// Holds the column list + accumulated rows + accumulated constraint
/// declarations; emits the <c>&lt;Element Type="SqlTable"&gt;</c> XML
/// for the table itself plus one sibling Element per constraint (Sql{PK,
/// Unique, ForeignKey, Check, Default}Constraint) on Build.
/// </summary>
public sealed class TableBuilder
{
    internal readonly string SchemaName;
    internal readonly string TableName;
    private readonly List<ColumnDef> _columns = [];
    private readonly List<object?[]> _rows = [];
    internal readonly List<ConstraintDef> Constraints = [];
    internal readonly List<IndexDef> Indexes = [];
    private readonly List<ComputedColumnDef> _computedColumns = [];
    internal IReadOnlyList<ComputedColumnDef> ComputedColumns => _computedColumns;
    // Declaration order of every column (simple + computed), so a computed
    // column added between two simple columns emits at its true model ordinal
    // — the ordinal the loader must preserve so sys.columns.column_id matches
    // the source database (and system-versioned base/history pairs align).
    private readonly List<(bool Computed, int Index)> _order = [];
    internal string? HistorySchemaName;
    internal string? HistoryTableName;

    internal TableBuilder(string schemaName, string tableName)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    internal IReadOnlyList<object?[]> Rows => _rows;
    internal int RowCount => _rows.Count;
    internal IReadOnlyList<ColumnDef> Columns => _columns;

    /// <summary>
    /// Adds a column. <paramref name="sqlType"/> is the raw SQL Server
    /// type name as it would appear in a CREATE TABLE column-type position
    /// (e.g. <c>"int"</c>, <c>"nvarchar(50)"</c>, <c>"decimal(18, 4)"</c>).
    /// Default nullability is NOT NULL — the bacpac convention is
    /// IsNullable=True default, but for test setup NOT NULL is more
    /// useful so explicit assertions on NULL-handling become opt-in.
    /// </summary>
    public TableBuilder Column(string name, string sqlType, bool nullable = false, bool identity = false, int identitySeed = 1, int identityIncrement = 1, PeriodColumnKind periodKind = PeriodColumnKind.None, string? collation = null, bool rowGuidCol = false, string? xmlSchemaCollection = null)
    {
        _columns.Add(new ColumnDef(name, sqlType, nullable, identity, identitySeed, identityIncrement, periodKind, collation, rowGuidCol, xmlSchemaCollection));
        _order.Add((Computed: false, _columns.Count - 1));
        return this;
    }

    /// <summary>
    /// Marks this table as system-versioned with the given history-table
    /// sibling. The loader emits this as
    /// <c>ALTER TABLE base SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))</c>
    /// in phase 5, after both endpoints have been created. The base table's
    /// row-start / row-end columns must also be marked via
    /// <see cref="Column"/> with <c>periodKind: PeriodColumnKind.Start</c>
    /// or <c>PeriodColumnKind.End</c>.
    /// </summary>
    public TableBuilder SystemVersioned(string historySchema, string historyTable)
    {
        HistorySchemaName = historySchema;
        HistoryTableName = historyTable;
        return this;
    }

    /// <summary>
    /// Adds a computed column. The loader emits it inline in CREATE TABLE at
    /// its model ordinal (or in phase 8 via <c>ALTER TABLE … ADD col AS (expr)</c>
    /// when the expression forward-references a not-yet-created UDF). When
    /// <paramref name="persisted"/> is set the model carries
    /// <c>IsPersisted=True</c> (and <c>IsPersistedNullable</c>), which the loader
    /// translates to a <c>PERSISTED</c> / <c>PERSISTED NOT NULL</c> marker — the
    /// column then has a storage slot and its value is computed at BCP-load
    /// time (the BCP wire carries no bytes for computed columns).
    /// </summary>
    public TableBuilder ComputedColumn(string name, string expression, bool persisted = false, bool persistedNullable = true)
    {
        _computedColumns.Add(new ComputedColumnDef(name, expression, persisted, persistedNullable));
        _order.Add((Computed: true, _computedColumns.Count - 1));
        return this;
    }

    /// <summary>
    /// Adds one row to the table's BCP data. Value count must match
    /// the current column count; types must match the column types
    /// (e.g. <c>int</c> columns expect <c>int</c> values).
    /// </summary>
    public TableBuilder Row(params object?[] values)
    {
        if (values.Length != _columns.Count)
            throw new InvalidOperationException($"Row value count ({values.Length}) doesn't match column count ({_columns.Count}) on [{SchemaName}].[{TableName}].");
        _rows.Add(values);
        return this;
    }

    /// <summary>
    /// Adds a named <c>PRIMARY KEY</c> constraint over <paramref name="columns"/>.
    /// PK defaults to CLUSTERED. Loader emits as a top-level
    /// <c>SqlPrimaryKeyConstraint</c> element that runs in phase 3
    /// (after the SqlTable element creates the table itself).
    /// </summary>
    public TableBuilder PrimaryKey(string name, params string[] columns)
    {
        Constraints.Add(new PrimaryKeyDef(name, columns));
        return this;
    }

    /// <summary>
    /// Adds a named <c>UNIQUE</c> constraint over <paramref name="columns"/>.
    /// UQ defaults to NONCLUSTERED.
    /// </summary>
    public TableBuilder Unique(string name, params string[] columns)
    {
        Constraints.Add(new UniqueDef(name, columns));
        return this;
    }

    /// <summary>
    /// Adds a named <c>CHECK (expression)</c> constraint. The expression
    /// is raw T-SQL — feeds directly to the simulator's CHECK parser.
    /// </summary>
    public TableBuilder Check(string name, string expression)
    {
        Constraints.Add(new CheckDef(name, expression));
        return this;
    }

    /// <summary>
    /// Adds a named <c>DEFAULT (expression) FOR <paramref name="column"/></c>
    /// constraint. The expression is raw T-SQL.
    /// </summary>
    public TableBuilder Default(string name, string column, string expression)
    {
        Constraints.Add(new DefaultDef(name, column, expression));
        return this;
    }

    /// <summary>
    /// Adds a named <c>FOREIGN KEY (<paramref name="childColumns"/>)
    /// REFERENCES <paramref name="parentSchema"/>.<paramref name="parentTable"/>
    /// (<paramref name="parentColumns"/>) [ON DELETE …] [ON UPDATE …]</c>
    /// constraint. Referential-action strings ("CASCADE" / "SET NULL" /
    /// "SET DEFAULT") map to the DACFx integer property; null /
    /// "NO ACTION" omits the property entirely. The parent table must
    /// have a matching PK / UQ over the referenced columns — declare
    /// the parent first in the builder so its PK lands in phase 3
    /// before this FK runs in phase 4.
    /// </summary>
    public TableBuilder ForeignKey(
        string name,
        string[] childColumns,
        string parentSchema,
        string parentTable,
        string[] parentColumns,
        string? onDelete = null,
        string? onUpdate = null)
    {
        Constraints.Add(new ForeignKeyDef(name, childColumns, parentSchema, parentTable, parentColumns, onDelete, onUpdate));
        return this;
    }

    /// <summary>
    /// Adds a named index over <paramref name="keyColumns"/>, with optional
    /// <paramref name="includedColumns"/> (<c>INCLUDE</c> clause) and
    /// <paramref name="unique"/> / <paramref name="clustered"/> toggles
    /// (both default false → <c>CREATE NONCLUSTERED INDEX</c>). Lands as
    /// a top-level <c>SqlIndex</c> element that runs in phase 8 (after
    /// constraints + computed columns + views).
    /// </summary>
    public TableBuilder Index(string name, string[] keyColumns, string[]? includedColumns = null, bool unique = false, bool clustered = false)
    {
        Indexes.Add(new IndexDef(name, keyColumns, includedColumns ?? [], unique, clustered));
        return this;
    }

    internal void WriteBcpRow(Stream stream, object?[] row)
    {
        for (var i = 0; i < _columns.Count; i++)
            BacpacBuilder.EncodeBcpValue(stream, _columns[i], row[i]);
    }

    internal XElement ToModelElement(XNamespace ns)
    {
        var columnsRelationship = new XElement(ns + "Relationship",
            new XAttribute("Name", "Columns"));
        foreach (var (computed, index) in _order)
        {
            columnsRelationship.Add(computed
                ? new XElement(ns + "Entry", ComputedColumnElement(ns, _computedColumns[index]))
                : new XElement(ns + "Entry", ColumnElement(ns, _columns[index])));
        }

        var tableElement = new XElement(ns + "Element",
            new XAttribute("Type", "SqlTable"),
            new XAttribute("Name", $"[{SchemaName}].[{TableName}]"),
            columnsRelationship);

        if (HistorySchemaName is not null && HistoryTableName is not null)
        {
            tableElement.Add(new XElement(ns + "Relationship",
                new XAttribute("Name", "TemporalSystemVersioningHistoryTable"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{HistorySchemaName}].[{HistoryTableName}]")))));
        }

        return tableElement;
    }

    private XElement ComputedColumnElement(XNamespace ns, ComputedColumnDef computed)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlComputedColumn"),
            new XAttribute("Name", $"[{SchemaName}].[{TableName}].[{computed.Name}]"));
        element.Add(new XElement(ns + "Property",
            new XAttribute("Name", "ExpressionScript"),
            new XElement(ns + "Value", new XCData(computed.Expression))));
        if (computed.Persisted)
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsPersisted"),
                new XAttribute("Value", "True")));
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsPersistedNullable"),
                new XAttribute("Value", computed.PersistedNullable ? "True" : "False")));
        }
        return element;
    }

    internal IEnumerable<XElement> ConstraintElements(XNamespace ns)
    {
        var table = $"[{SchemaName}].[{TableName}]";
        foreach (var constraint in Constraints)
        {
            yield return constraint switch
            {
                PrimaryKeyDef pk => KeyConstraintElement(ns, "SqlPrimaryKeyConstraint", pk.Name, pk.Columns, isPrimary: true),
                UniqueDef uq => KeyConstraintElement(ns, "SqlUniqueConstraint", uq.Name, uq.Columns, isPrimary: false),
                CheckDef ck => CheckConstraintElement(ns, ck.Name, ck.Expression),
                DefaultDef df => DefaultConstraintElement(ns, df.Name, df.Column, df.Expression),
                ForeignKeyDef fk => ForeignKeyConstraintElement(ns, fk, table),
                _ => throw new InvalidOperationException($"Unknown constraint kind: {constraint.GetType().Name}"),
            };
        }
    }

    internal IEnumerable<XElement> IndexElements(XNamespace ns)
    {
        foreach (var index in Indexes)
        {
            var element = new XElement(ns + "Element",
                new XAttribute("Type", "SqlIndex"),
                new XAttribute("Name", $"[{SchemaName}].[{TableName}].[{index.Name}]"),
                new XElement(ns + "Relationship",
                    new XAttribute("Name", "IndexedObject"),
                    new XElement(ns + "Entry",
                        new XElement(ns + "References",
                            new XAttribute("Name", $"[{SchemaName}].[{TableName}]")))));

            if (index.Unique)
                element.Add(PropertyElement(ns, "IsUnique", "True"));
            if (index.Clustered)
                element.Add(PropertyElement(ns, "IsClustered", "True"));

            var columnSpecs = new XElement(ns + "Relationship",
                new XAttribute("Name", "ColumnSpecifications"));
            foreach (var col in index.KeyColumns)
            {
                var spec = new XElement(ns + "Element",
                    new XAttribute("Type", "SqlIndexedColumnSpecification"),
                    new XElement(ns + "Relationship",
                        new XAttribute("Name", "Column"),
                        new XElement(ns + "Entry",
                            new XElement(ns + "References",
                                new XAttribute("Name", $"[{SchemaName}].[{TableName}].[{col}]")))));
                columnSpecs.Add(new XElement(ns + "Entry", spec));
            }
            element.Add(columnSpecs);

            if (index.IncludedColumns.Length > 0)
            {
                var includes = new XElement(ns + "Relationship",
                    new XAttribute("Name", "IncludedColumns"));
                foreach (var col in index.IncludedColumns)
                {
                    includes.Add(new XElement(ns + "Entry",
                        new XElement(ns + "References",
                            new XAttribute("Name", $"[{SchemaName}].[{TableName}].[{col}]"))));
                }
                element.Add(includes);
            }
            yield return element;
        }
    }

    private XElement KeyConstraintElement(XNamespace ns, string elementType, string name, string[] columns, bool isPrimary)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", elementType),
            new XAttribute("Name", $"[{SchemaName}].[{name}]"),
            DefiningTableRelationship(ns));

        // UQ defaults to NONCLUSTERED, PK to CLUSTERED — the loader only
        // diverges from the default when the IsClustered property is set.
        // Builder follows the loader's default by leaving the property
        // absent unless the test demands non-default behavior (future).
        if (!isPrimary)
        {
            // For UNIQUE, emit IsClustered=False explicitly to mirror DACFx
            // output — keeps probe-vs-builder cross-checks honest if we
            // ever need to diff against a real bacpac's serialized form.
            element.Add(PropertyElement(ns, "IsClustered", "False"));
        }

        var columnSpecs = new XElement(ns + "Relationship",
            new XAttribute("Name", "ColumnSpecifications"));
        foreach (var col in columns)
        {
            var spec = new XElement(ns + "Element",
                new XAttribute("Type", "SqlIndexedColumnSpecification"),
                new XElement(ns + "Relationship",
                    new XAttribute("Name", "Column"),
                    new XElement(ns + "Entry",
                        new XElement(ns + "References",
                            new XAttribute("Name", $"[{SchemaName}].[{TableName}].[{col}]")))));
            columnSpecs.Add(new XElement(ns + "Entry", spec));
        }
        element.Add(columnSpecs);
        return element;
    }

    private XElement CheckConstraintElement(XNamespace ns, string name, string expression) =>
        new(ns + "Element",
            new XAttribute("Type", "SqlCheckConstraint"),
            new XAttribute("Name", $"[{SchemaName}].[{name}]"),
            DefiningTableRelationship(ns),
            new XElement(ns + "Property",
                new XAttribute("Name", "CheckExpressionScript"),
                new XElement(ns + "Value", new XCData(expression))));

    private XElement DefaultConstraintElement(XNamespace ns, string name, string column, string expression) =>
        new(ns + "Element",
            new XAttribute("Type", "SqlDefaultConstraint"),
            new XAttribute("Name", $"[{SchemaName}].[{name}]"),
            DefiningTableRelationship(ns),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "ForColumn"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{SchemaName}].[{TableName}].[{column}]")))),
            new XElement(ns + "Property",
                new XAttribute("Name", "DefaultExpressionScript"),
                new XElement(ns + "Value", new XCData(expression))));

    private XElement ForeignKeyConstraintElement(XNamespace ns, ForeignKeyDef fk, string definingTable)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlForeignKeyConstraint"),
            new XAttribute("Name", $"[{SchemaName}].[{fk.Name}]"),
            DefiningTableRelationship(ns));

        var childCols = new XElement(ns + "Relationship", new XAttribute("Name", "Columns"));
        foreach (var col in fk.ChildColumns)
        {
            childCols.Add(new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("Name", $"{definingTable}.[{col}]"))));
        }
        element.Add(childCols);

        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "ForeignTable"),
            new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("Name", $"[{fk.ParentSchema}].[{fk.ParentTable}]")))));

        var parentCols = new XElement(ns + "Relationship", new XAttribute("Name", "ForeignColumns"));
        foreach (var col in fk.ParentColumns)
        {
            parentCols.Add(new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("Name", $"[{fk.ParentSchema}].[{fk.ParentTable}].[{col}]"))));
        }
        element.Add(parentCols);

        var deleteEnum = ReferentialActionEnum(fk.OnDelete);
        if (deleteEnum is not null)
            element.Add(PropertyElement(ns, "OnDeleteAction", deleteEnum));
        var updateEnum = ReferentialActionEnum(fk.OnUpdate);
        if (updateEnum is not null)
            element.Add(PropertyElement(ns, "OnUpdateAction", updateEnum));
        return element;
    }

    private XElement DefiningTableRelationship(XNamespace ns) =>
        new(ns + "Relationship",
            new XAttribute("Name", "DefiningTable"),
            new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("Name", $"[{SchemaName}].[{TableName}]"))));

    private static string? ReferentialActionEnum(string? action) => action?.ToUpperInvariant() switch
    {
        null or "" or "NO ACTION" => null,
        "CASCADE" => "1",
        "SET NULL" => "2",
        "SET DEFAULT" => "3",
        _ => throw new ArgumentException($"Unknown referential action: '{action}'. Expected CASCADE / SET NULL / SET DEFAULT / NO ACTION / null."),
    };

    /// <summary>Emits the table-type-flavored column element (SqlTableTypeSimpleColumn).</summary>
    internal XElement TableTypeColumnElement(XNamespace ns, ColumnDef column)
    {
        var qualifiedName = $"[{SchemaName}].[{TableName}].[{column.Name}]";
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlTableTypeSimpleColumn"),
            new XAttribute("Name", qualifiedName));
        if (!column.Nullable)
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsNullable"),
                new XAttribute("Value", "False")));
        }
        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "TypeSpecifier"),
            new XElement(ns + "Entry", TypeSpecifierElement(ns, column.SqlType))));
        return element;
    }

    private XElement ColumnElement(XNamespace ns, ColumnDef column)
    {
        var qualifiedName = $"[{SchemaName}].[{TableName}].[{column.Name}]";
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlSimpleColumn"),
            new XAttribute("Name", qualifiedName));

        if (!column.Nullable)
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsNullable"),
                new XAttribute("Value", "False")));
        }

        if (column.Identity)
        {
            element.Add(PropertyElement(ns, "IsIdentity", "True"));
            element.Add(PropertyElement(ns, "IdentitySeed", column.IdentitySeed.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            element.Add(PropertyElement(ns, "IdentityIncrement", column.IdentityIncrement.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (column.PeriodKind == PeriodColumnKind.Start)
            element.Add(PropertyElement(ns, "GeneratedAlwaysType", "1"));
        else if (column.PeriodKind == PeriodColumnKind.End)
            element.Add(PropertyElement(ns, "GeneratedAlwaysType", "2"));
        if (column.Collation is not null)
            element.Add(PropertyElement(ns, "Collation", column.Collation));
        if (column.RowGuidCol)
            element.Add(PropertyElement(ns, "IsRowGuidColumn", "True"));

        var typeSpecifier = column.XmlSchemaCollectionRef is { } collectionRef
            ? XmlTypeSpecifierElement(ns, collectionRef)
            : TypeSpecifierElement(ns, column.SqlType);
        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "TypeSpecifier"),
            new XElement(ns + "Entry", typeSpecifier)));
        return element;
    }

    /// <summary>
    /// Emits a <c>SqlXmlTypeSpecifier</c> bound to a schema collection — the
    /// shape DACFx uses for a typed-xml column (<c>xml([schema].[collection])</c>).
    /// A <c>Type</c> relationship to the <c>[xml]</c> built-in plus an
    /// <c>XmlSchemaCollection</c> relationship to the collection reference.
    /// </summary>
    private static XElement XmlTypeSpecifierElement(XNamespace ns, string collectionRef) =>
        new(ns + "Element",
            new XAttribute("Type", "SqlXmlTypeSpecifier"),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "Type"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", "[xml]"),
                        new XAttribute("ExternalSource", "BuiltIns")))),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "XmlSchemaCollection"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", collectionRef)))));

    private static XElement TypeSpecifierElement(XNamespace ns, string sqlType)
    {
        // Bracketed 2-part name → UDDT reference (no ExternalSource, no
        // base-type args). The simulator's name resolver routes this
        // through Schema.AliasTypes lookup.
        if (sqlType.StartsWith('['))
        {
            return new XElement(ns + "Element",
                new XAttribute("Type", "SqlTypeSpecifier"),
                new XElement(ns + "Relationship",
                    new XAttribute("Name", "Type"),
                    new XElement(ns + "Entry",
                        new XElement(ns + "References",
                            new XAttribute("Name", sqlType)))));
        }

        var (baseName, length, precision, scale, isMax) = ParseSqlType(sqlType);
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlTypeSpecifier"),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "Type"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{baseName}]"),
                        new XAttribute("ExternalSource", "BuiltIns")))));

        if (isMax)
            element.Add(PropertyElement(ns, "IsMax", "True"));
        if (length is not null)
            element.Add(PropertyElement(ns, "Length", length));
        if (precision is not null)
            element.Add(PropertyElement(ns, "Precision", precision));
        if (scale is not null)
            element.Add(PropertyElement(ns, "Scale", scale));
        return element;
    }

    private static XElement PropertyElement(XNamespace ns, string name, string value) =>
        new(ns + "Property",
            new XAttribute("Name", name),
            new XAttribute("Value", value));

    private static (string Base, string? Length, string? Precision, string? Scale, bool IsMax) ParseSqlType(string sqlType)
    {
        var trimmed = sqlType.Trim();
        var openParen = trimmed.IndexOf('(');
        if (openParen < 0)
            return (trimmed.ToLowerInvariant(), null, null, null, false);

        var baseName = trimmed[..openParen].Trim().ToLowerInvariant();
        var args = trimmed[(openParen + 1)..^1].Trim();
        if (string.Equals(args, "max", StringComparison.OrdinalIgnoreCase))
            return (baseName, null, null, null, true);

        var commaIndex = args.IndexOf(',');
        if (commaIndex < 0)
        {
            return baseName switch
            {
                "decimal" or "numeric" => (baseName, null, args.Trim(), null, false),
                "time" or "datetime2" or "datetimeoffset" => (baseName, null, null, args.Trim(), false),
                _ => (baseName, args.Trim(), null, null, false),
            };
        }
        var precision = args[..commaIndex].Trim();
        var scale = args[(commaIndex + 1)..].Trim();
        return (baseName, null, precision, scale, false);
    }
}

/// <summary>Period-column role flag for system-versioned tables.</summary>
public enum PeriodColumnKind
{
    /// <summary>Non-period column (default).</summary>
    None = 0,
    /// <summary>Period start column — emitted with <c>GeneratedAlwaysType=1</c>.</summary>
    Start = 1,
    /// <summary>Period end column — emitted with <c>GeneratedAlwaysType=2</c>.</summary>
    End = 2,
}

/// <summary>Column metadata captured by <see cref="TableBuilder.Column"/>.</summary>
internal readonly record struct ColumnDef(string Name, string SqlType, bool Nullable, bool Identity = false, int IdentitySeed = 1, int IdentityIncrement = 1, PeriodColumnKind PeriodKind = PeriodColumnKind.None, string? Collation = null, bool RowGuidCol = false, string? XmlSchemaCollectionRef = null);

/// <summary>Base for constraint declarations accumulated on a table.</summary>
internal abstract record ConstraintDef(string Name);

internal sealed record PrimaryKeyDef(string Name, string[] Columns) : ConstraintDef(Name);
internal sealed record UniqueDef(string Name, string[] Columns) : ConstraintDef(Name);
internal sealed record CheckDef(string Name, string Expression) : ConstraintDef(Name);
internal sealed record DefaultDef(string Name, string Column, string Expression) : ConstraintDef(Name);
internal sealed record ForeignKeyDef(
    string Name,
    string[] ChildColumns,
    string ParentSchema,
    string ParentTable,
    string[] ParentColumns,
    string? OnDelete,
    string? OnUpdate) : ConstraintDef(Name);

/// <summary>Index declaration accumulated via <see cref="TableBuilder.Index"/>.</summary>
internal readonly record struct IndexDef(string Name, string[] KeyColumns, string[] IncludedColumns, bool Unique, bool Clustered);

/// <summary>Computed column declaration accumulated via <see cref="TableBuilder.ComputedColumn"/>.</summary>
internal readonly record struct ComputedColumnDef(string Name, string Expression, bool Persisted, bool PersistedNullable);
