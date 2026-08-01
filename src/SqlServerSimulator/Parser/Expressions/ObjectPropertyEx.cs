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
/// Like real SQL Server, the result is always <c>sql_variant</c>
/// (<see cref="SqlType.SqlVariant"/>); each property carries its probed inner
/// base type — <c>BaseType</c> as <c>char(2)</c>, <c>Cardinality</c> as
/// <see cref="SqlType.BigInt"/>, and every other shipped property
/// (<c>SchemaId</c>, the <c>Is*</c> booleans, the <c>TableHas*</c> flags) as
/// <see cref="SqlType.Int32"/>.
/// </para>
/// <para>
/// Shipped properties (all probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description>All <c>Is*</c> booleans from <see cref="ObjectProperty"/>
/// (delegates to the shared <c>ObjectProperty.EvaluateProperty</c> helper).</description></item>
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
            return SqlValue.Null(SqlType.SqlVariant);
        var id = ScalarArguments.CoerceToInt(idValue);
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        var obj = ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, id);
        // Boolean Is-X props share OBJECTPROPERTY's dispatch verbatim; real
        // carries them as an int inner base type inside the sql_variant.
        return obj is null
            ? SqlValue.Null(SqlType.SqlVariant)
            : ObjectProperty.EvaluateProperty(runtime.Batch.CurrentDatabase, obj, prop) is int booleanResult
                ? SqlValue.FromVariant(SqlValue.FromInt32(booleanResult))
                : EvaluateExtendedProperty(obj, prop, runtime.Batch);
    }

    private static SqlValue EvaluateExtendedProperty(SchemaObject obj, string property, BatchContext batch)
    {
        var database = batch.CurrentDatabase;
        Span<char> upper = stackalloc char[property.Length];
        var len = property.AsSpan().ToUpperInvariant(upper);

        return len switch
        {
            8 => upper[..len] switch
            {
                // BaseType's inner type is char(2) in the database collation
                // (probe-confirmed) — the 2-char object-type code with its
                // trailing-space padding.
                "BASETYPE" => BaseTypeVariant(obj, batch, database),
                "SCHEMAID" => IntVariant(ObjectProperty.FindOwningSchema(database, obj)?.SchemaId),
                _ => SqlValue.Null(SqlType.SqlVariant),
            },
            11 => upper[..len] switch
            {
                // Cardinality's inner type is bigint (probe-confirmed).
                "CARDINALITY" => obj is HeapTable table
                    ? SqlValue.FromVariant(SqlValue.FromInt64(table.Heap.RowCount))
                    : SqlValue.Null(SqlType.SqlVariant),
                _ => SqlValue.Null(SqlType.SqlVariant),
            },
            // The TableHas* family — shared verbatim with the non-EX
            // OBJECTPROPERTY, which real supports for every one of these
            // (probe-confirmed; only BaseType / Cardinality above are
            // genuinely EX-only, returning NULL from the non-EX form).
            13 or 16 or 17 or 18 => IntVariant(TableFlagByName(obj, upper[..len]) is bool flag ? flag ? 1 : 0 : null),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
    }

    /// <summary>
    /// Maps an upper-cased <c>TableHas*</c> property name to its answer for
    /// <paramref name="obj"/>: <see langword="null"/> when the object isn't a
    /// table or the name isn't one of the family (both are NULL on real), else
    /// the flag. Single source of truth for <c>OBJECTPROPERTY</c> and
    /// <c>OBJECTPROPERTYEX</c>, which expose the identical set.
    /// </summary>
    internal static bool? TableFlagByName(SchemaObject obj, ReadOnlySpan<char> upperName) =>
        obj is not HeapTable table ? null : upperName switch
        {
            "TABLEHASCHECKCNST" => HasCheckConstraint(table),
            "TABLEHASCLUSTINDEX" => HasClusteredIndex(table),
            "TABLEHASFOREIGNKEY" => HasOutgoingForeignKey(table),
            "TABLEHASFOREIGNREF" => HasIncomingForeignKey(table),
            "TABLEHASIDENTITY" => HasIdentity(table),
            "TABLEHASINDEX" => HasAnyIndex(table),
            "TABLEHASPRIMARYKEY" => HasPrimaryKey(table),
            "TABLEHASROWGUIDCOL" => HasRowGuidCol(table),
            "TABLEHASUNIQUECNST" => HasUniqueConstraint(table),
            _ => null,
        };

    private static SqlValue IntVariant(int? value) => value is int v
        ? SqlValue.FromVariant(SqlValue.FromInt32(v))
        : SqlValue.Null(SqlType.SqlVariant);

    private static bool HasAnyIndex(HeapTable table) => table.Indexes.Count > 0 || table.KeyConstraints.Count > 0;

    private static bool HasCheckConstraint(HeapTable table) => table.CheckConstraints.Count > 0;

    private static bool HasOutgoingForeignKey(HeapTable table) => table.OutgoingForeignKeys.Count > 0;

    private static bool HasIncomingForeignKey(HeapTable table) => table.IncomingForeignKeys.Count > 0;

    /// <summary>
    /// Real reports 1 when any column carries the <c>ROWGUIDCOL</c> marker
    /// (probe-confirmed); the per-column marker is tracked on
    /// <see cref="HeapColumn.IsRowGuidCol"/>, which
    /// <c>sys.columns.is_rowguidcol</c> already projects.
    /// </summary>
    private static bool HasRowGuidCol(HeapTable table)
    {
        foreach (var col in table.Columns)
        {
            if (col.IsRowGuidCol)
                return true;
        }
        return false;
    }

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

    /// <summary>
    /// <c>BaseType</c> as a <c>char(2)</c> sql_variant. A synonym reports the
    /// type of the object it points at rather than <c>'SN'</c> — probe-confirmed
    /// <c>'U '</c> for a table base, <c>'P '</c> for a procedure, <c>'FN'</c>
    /// for a scalar function — and NULL when the base doesn't resolve.
    /// </summary>
    private static SqlValue BaseTypeVariant(SchemaObject obj, BatchContext batch, Database database)
    {
        var reported = obj;
        if (obj is Synonym synonym)
        {
            if (!batch.TryResolveSynonymBase(synonym, out var target))
                return SqlValue.Null(SqlType.SqlVariant);
            reported = target;
        }
        return SqlValue.FromVariant(SqlValue.FromChar(CharSqlType.Get(2, database.Collation, Coercibility.Implicit), BaseTypeFor(reported)));
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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() =>
        $"OBJECTPROPERTYEX({this.idArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
