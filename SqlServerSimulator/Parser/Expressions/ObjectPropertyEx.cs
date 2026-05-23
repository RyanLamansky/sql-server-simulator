using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECTPROPERTYEX(object_id, 'property')</c>: extension of
/// <see cref="ObjectProperty"/> with extra properties whose values aren't
/// always integer-valued (e.g. <c>BaseType</c> returns the 2-char object-type
/// code <c>'U '</c> / <c>'V '</c> / <c>'P '</c>).
/// </summary>
/// <remarks>
/// <para>
/// Real SQL Server projects this as <c>sql_variant</c>; the simulator
/// returns <see cref="SqlType.NVarchar"/> throughout, matching the
/// <see cref="ServerProperty"/> convention. Integer-valued properties
/// surface as their decimal string form (SqlClient transparently casts
/// back through implicit conversion).
/// </para>
/// <para>
/// Shipped properties (all probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description>All <c>Is*</c> booleans from <see cref="ObjectProperty"/>
/// (delegates to the shared <see cref="ObjectProperty.EvaluateProperty"/> helper).</description></item>
/// <item><description><c>BaseType</c> — <c>'U '</c> for user table, <c>'V '</c>
/// for view, <c>'P '</c> for procedure, <c>'FN'</c> / <c>'IF'</c> / <c>'TF'</c>
/// for function variants, <c>'TR'</c> for trigger, <c>'SO'</c> for sequence.</description></item>
/// <item><description><c>SchemaId</c> — owning schema's <see cref="Schema.SchemaId"/>.</description></item>
/// <item><description><c>Cardinality</c> — table row count
/// (<see cref="Heap.RowCount"/>); NULL for non-tables.</description></item>
/// <item><description><c>TableHasIdentity</c> / <c>TableHasPrimaryKey</c> /
/// <c>TableHasClustIndex</c> / <c>TableHasIndex</c> /
/// <c>TableHasUniqueCnst</c> / <c>TableHasCheckCnst</c> /
/// <c>TableHasForeignKey</c> / <c>TableHasForeignRef</c> /
/// <c>TableHasRowGuidCol</c> — 1 / 0 per table feature, NULL for non-tables.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class ObjectPropertyEx : Expression
{
    private readonly Expression idArg;
    private readonly Expression propertyArg;

    public ObjectPropertyEx(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (idValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        var obj = ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, id);
        // Boolean Is-X props share OBJECTPROPERTY's dispatch verbatim.
        return obj is null
            ? SqlValue.Null(SqlType.NVarchar)
            : ObjectProperty.EvaluateProperty(obj, prop) is int booleanResult
                ? SqlValue.FromNVarchar(booleanResult.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : EvaluateExtendedProperty(obj, prop, runtime.Batch.CurrentDatabase);
    }

    private static SqlValue EvaluateExtendedProperty(SchemaObject obj, string property, Database database)
    {
        Span<char> upper = stackalloc char[property.Length];
        var len = property.AsSpan().ToUpperInvariant(upper);

        return len switch
        {
            8 => upper[..len] switch
            {
                "BASETYPE" => SqlValue.FromNVarchar(BaseTypeFor(obj)),
                "SCHEMAID" => IntValue(ObjectProperty.FindOwningSchema(database, obj)?.SchemaId),
                _ => SqlValue.Null(SqlType.NVarchar),
            },
            11 => upper[..len] switch
            {
                "CARDINALITY" => obj is HeapTable table
                    ? SqlValue.FromNVarchar(table.Heap.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : SqlValue.Null(SqlType.NVarchar),
                _ => SqlValue.Null(SqlType.NVarchar),
            },
            13 => upper[..len] switch
            {
                "TABLEHASINDEX" => TableFlag(obj, HasAnyIndex),
                _ => SqlValue.Null(SqlType.NVarchar),
            },
            16 => upper[..len] switch
            {
                "TABLEHASIDENTITY" => TableFlag(obj, HasIdentity),
                _ => SqlValue.Null(SqlType.NVarchar),
            },
            17 => upper[..len] switch
            {
                "TABLEHASCHECKCNST" => TableFlag(obj, HasCheckConstraint),
                _ => SqlValue.Null(SqlType.NVarchar),
            },
            18 => upper[..len] switch
            {
                "TABLEHASCLUSTINDEX" => TableFlag(obj, HasClusteredIndex),
                "TABLEHASFOREIGNKEY" => TableFlag(obj, HasOutgoingForeignKey),
                "TABLEHASFOREIGNREF" => TableFlag(obj, HasIncomingForeignKey),
                "TABLEHASPRIMARYKEY" => TableFlag(obj, HasPrimaryKey),
                "TABLEHASROWGUIDCOL" => TableFlag(obj, AlwaysFalse),
                "TABLEHASUNIQUECNST" => TableFlag(obj, HasUniqueConstraint),
                _ => SqlValue.Null(SqlType.NVarchar),
            },
            _ => SqlValue.Null(SqlType.NVarchar),
        };
    }

    private static SqlValue IntValue(int? value) => value is int v
        ? SqlValue.FromNVarchar(v.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : SqlValue.Null(SqlType.NVarchar);

    private static SqlValue TableFlag(SchemaObject obj, Func<HeapTable, bool> predicate) =>
        obj is HeapTable table
            ? SqlValue.FromNVarchar(predicate(table) ? "1" : "0")
            : SqlValue.Null(SqlType.NVarchar);

    private static bool HasAnyIndex(HeapTable table) => table.Indexes.Count > 0 || table.KeyConstraints.Count > 0;

    private static bool HasCheckConstraint(HeapTable table) => table.CheckConstraints.Count > 0;

    private static bool HasOutgoingForeignKey(HeapTable table) => table.OutgoingForeignKeys.Count > 0;

    private static bool HasIncomingForeignKey(HeapTable table) => table.IncomingForeignKeys.Count > 0;

    private static bool AlwaysFalse(HeapTable table) => false;

    private static bool HasIdentity(HeapTable table)
    {
        foreach (var col in table.Columns)
        {
            if (col.Identity is not null)
                return true;
        }
        return false;
    }

    private static bool HasPrimaryKey(HeapTable table)
    {
        foreach (var kc in table.KeyConstraints)
        {
            if (kc.Kind == KeyConstraintKind.PrimaryKey)
                return true;
        }
        return false;
    }

    private static bool HasUniqueConstraint(HeapTable table)
    {
        foreach (var kc in table.KeyConstraints)
        {
            if (kc.Kind == KeyConstraintKind.Unique)
                return true;
        }
        return false;
    }

    private static bool HasClusteredIndex(HeapTable table)
    {
        // Real SQL Server returns 1 when a clustered index exists. The
        // simulator's PK constraint creates a clustered index by default
        // (matching real SQL Server's PRIMARY KEY shipping clustered).
        foreach (var idx in table.Indexes)
        {
            if (idx.IsClustered)
                return true;
        }
        return HasPrimaryKey(table);
    }

    private static string BaseTypeFor(SchemaObject obj) => obj switch
    {
        HeapTable => "U ",
        View => "V ",
        Procedure => "P ",
        ScalarFunction => "FN",
        InlineTableValuedFunction => "IF",
        MultiStatementTableValuedFunction => "TF",
        Trigger => "TR",
        Sequence => "SO",
        _ => string.Empty,
    };

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() =>
        $"OBJECTPROPERTYEX({this.idArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
