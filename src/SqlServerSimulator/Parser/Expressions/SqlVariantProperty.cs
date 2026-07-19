using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SQL_VARIANT_PROPERTY(expression, property)</c>: reports one facet of
/// the <c>sql_variant</c> that would capture the <c>expression</c> —
/// <c>BaseType</c> / <c>Precision</c> / <c>Scale</c> / <c>MaxLength</c> /
/// <c>TotalBytes</c> / <c>Collation</c>. Like real SQL Server, the result is
/// always <c>sql_variant</c> (<see cref="SqlType.SqlVariant"/>) carrying a
/// per-property inner base type: <c>BaseType</c> / <c>Collation</c> as
/// <see cref="SqlType.SystemName"/> (sysname, an <c>nvarchar</c> inner), the
/// four numeric facets as <see cref="SqlType.Int32"/> (probe-confirmed against
/// SQL Server 2025).
/// </summary>
/// <remarks>
/// SSMS's Database Properties dialog reads
/// <c>SQL_VARIANT_PROPERTY(value, 'BaseType')</c> off
/// <c>sys.database_scoped_configurations</c>. Property matching is
/// case-insensitive; a NULL expression, a NULL property, an unknown property,
/// or a value whose type can't live in a sql_variant (MAX strings, LOB,
/// xml, spatial) all return NULL. Because the simulator has a single decimal
/// family, decimal-family values report <c>BaseType</c> <c>numeric</c> — this
/// matches a numeric literal's inference but diverges from real, where
/// <c>CAST(1 AS decimal)</c> reports <c>decimal</c>.
/// </remarks>
internal sealed class SqlVariantProperty : Expression
{
    private enum PropertyKind
    {
        BaseType,
        Precision,
        Scale,
        MaxLength,
        TotalBytes,
        Collation,
    }

    private readonly Expression valueArg;
    private readonly Expression propertyArg;

    public SqlVariantProperty(ParserContext context)
    {
        this.valueArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var propertyValue = this.propertyArg.Run(runtime);
        if (propertyValue.IsNull || Resolve(propertyValue.CoerceTo(SqlType.NVarchar).AsString) is not { } kind)
            return SqlValue.Null(SqlType.SqlVariant);

        var result = Compute(kind, this.valueArg.Run(runtime));
        return result.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(result);
    }

    private static PropertyKind? Resolve(string property)
    {
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument.
        if (property.Length > 32)
            return null;
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "BASETYPE" => PropertyKind.BaseType,
            "COLLATION" => PropertyKind.Collation,
            "MAXLENGTH" => PropertyKind.MaxLength,
            "PRECISION" => PropertyKind.Precision,
            "SCALE" => PropertyKind.Scale,
            "TOTALBYTES" => PropertyKind.TotalBytes,
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    private static SqlValue Compute(PropertyKind kind, SqlValue value)
    {
        // A true sql_variant argument (the primary use — reading a
        // sql_variant column such as sys.database_scoped_configurations.value)
        // describes its inner value; a variant NULL yields NULL like any NULL.
        if (value.Type is SqlVariantSqlType)
            value = value.IsNull ? SqlValue.Null(SqlType.Int32) : value.AsVariantInner;

        var isStringResult = kind is PropertyKind.BaseType or PropertyKind.Collation;
        var nullResult = SqlValue.Null(isStringResult ? SqlType.SystemName : SqlType.Int32);
        return value.IsNull || Describe(value) is not { } info
            ? nullResult
            : kind switch
            {
                PropertyKind.BaseType => SqlValue.FromSystemName(info.BaseType),
                PropertyKind.Precision => SqlValue.FromInt32(info.Precision),
                PropertyKind.Scale => SqlValue.FromInt32(info.Scale),
                PropertyKind.MaxLength => SqlValue.FromInt32(info.MaxLength),
                PropertyKind.TotalBytes => SqlValue.FromInt32(info.TotalBytes),
                PropertyKind.Collation => info.CollationName is { } name ? SqlValue.FromSystemName(name) : SqlValue.Null(SqlType.SystemName),
                _ => nullResult,
            };
    }

    private readonly record struct VariantInfo(string BaseType, int Precision, int Scale, int MaxLength, int TotalBytes, string? CollationName);

    /// <summary>
    /// Derives the six facets from a value's type, or <see langword="null"/>
    /// when the type can't be stored in a sql_variant (MAX strings, text/ntext/
    /// image, xml, spatial, hierarchyid). <c>MaxLength</c> is the type's
    /// declared byte width; <c>TotalBytes</c> is the value's actual byte count
    /// plus a per-family overhead — strings +8, binary/decimal +4, the
    /// scale-carrying temporal types (time/datetime2/datetimeoffset) +3, every
    /// other type +2. All probe-confirmed against SQL Server 2025.
    /// </summary>
    private static VariantInfo? Describe(SqlValue value)
    {
        var t = value.Type;
        var actualBytes = t.IsFixedLength ? t.FixedLength : t.GetVariableByteCount(value);
        (int Precision, int Scale, int MaxLength, int Overhead)? metrics = t switch
        {
            _ when t == SqlType.Bit => (1, 0, 1, 2),
            _ when t == SqlType.TinyInt => (3, 0, 1, 2),
            _ when t == SqlType.SmallInt => (5, 0, 2, 2),
            _ when t == SqlType.Int32 => (10, 0, 4, 2),
            _ when t == SqlType.BigInt => (19, 0, 8, 2),
            _ when t == SqlType.Money => (19, 4, 8, 2),
            _ when t == SqlType.SmallMoney => (10, 4, 4, 2),
            _ when t == SqlType.Float => (53, 0, 8, 2),
            _ when t == SqlType.Real => (24, 0, 4, 2),
            _ when t == SqlType.Date => (10, 0, 3, 2),
            _ when t == SqlType.SmallDateTime => (16, 0, 4, 2),
            _ when t == SqlType.DateTime => (23, 3, 8, 2),
            _ when t == SqlType.UniqueIdentifier => (0, 0, 16, 2),
            DecimalSqlType d => (d.precision, d.scale, d.FixedLength, 4),
            TimeSqlType tm => (9 + tm.precision, tm.precision, tm.FixedLength, 3),
            DateTime2SqlType dt2 => (20 + dt2.precision, dt2.precision, dt2.FixedLength, 3),
            DateTimeOffsetSqlType dto => (27 + dto.precision, dto.precision, dto.FixedLength, 3),
            CharSqlType c => (0, 0, c.length, 8),
            NCharSqlType nc => (0, 0, nc.length * 2, 8),
            SystemNameSqlType => (0, 0, 256, 8),
            VarcharSqlType { length: SqlType.MaxLengthSentinel } => null,
            VarcharSqlType vc => (0, 0, vc.length > 0 ? vc.length : actualBytes, 8),
            NVarcharSqlType { length: SqlType.MaxLengthSentinel } => null,
            NVarcharSqlType nv => (0, 0, nv.length > 0 ? nv.length * 2 : actualBytes, 8),
            BinarySqlType bn => (0, 0, bn.length, 4),
            VarbinarySqlType { length: SqlType.MaxLengthSentinel } => null,
            VarbinarySqlType vb => (0, 0, vb.length > 0 ? vb.length : actualBytes, 4),
            _ => null,
        };
        if (metrics is not { } m)
            return null;
        var baseType = t is DecimalSqlType ? "numeric" : t is SystemNameSqlType ? "nvarchar" : t.SqlServerName;
        var collationName = t.Category == SqlTypeCategory.String ? (t.Collation ?? Collation.Baseline).Name : null;
        return new VariantInfo(baseType, m.Precision, m.Scale, m.MaxLength, actualBytes + m.Overhead, collationName);
    }

    internal override string DebugDisplay() => $"SQL_VARIANT_PROPERTY({this.valueArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
