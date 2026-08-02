using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TYPEPROPERTY('type_name', 'property')</c>: per-system-type
/// metadata flags / catalog values. Returns <c>int</c>; NULL on any NULL
/// arg, unknown type name, or unknown property. Names are case-insensitive.
/// </summary>
/// <remarks>
/// <para>
/// Backed by a static lookup table keyed by the system type's canonical name.
/// User-defined alias types (UDDT) and table types would also be reachable
/// through real SQL Server's <c>TYPEPROPERTY</c>, but they're rare in
/// application code and not in this shipped slice. Property values are
/// probe-confirmed against SQL Server 2025 (2026-05-23).
/// </para>
/// <para>
/// Shipped properties:
/// <list type="bullet">
/// <item><description><c>Precision</c> — the type's catalog precision
/// (e.g. <c>int</c>=10, <c>bigint</c>=19, <c>decimal</c>=38,
/// <c>varchar</c>=8000, <c>money</c>=19, <c>xml</c>=-1).</description></item>
/// <item><description><c>Scale</c> — the type's catalog scale
/// (<c>int</c>=0, <c>money</c>=4, <c>decimal</c>=38).</description></item>
/// <item><description><c>AllowsNull</c> — 1 for nullable types, 0 for
/// <c>timestamp</c> / <c>sysname</c>.</description></item>
/// <item><description><c>UsesAnsiTrim</c> — 1 for the blank-padded
/// single-byte types and <c>sql_variant</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// A property a type has no value for answers NULL rather than 0, which is
/// most of the table: only the exact-numeric and date/time types carry a
/// <c>Scale</c>, and only <c>char</c> / <c>varchar</c> / <c>binary</c> /
/// <c>varbinary</c> / <c>sql_variant</c> carry a <c>UsesAnsiTrim</c> — the
/// national-character types answer NULL there.
/// Type names real doesn't recognize here answer NULL for every property, and
/// that includes two spellings the T-SQL grammar itself accepts as synonyms,
/// <c>integer</c> and <c>rowversion</c> (their canonical <c>int</c> and
/// <c>timestamp</c> resolve).
/// </para>
/// </remarks>
internal sealed class TypeProperty : Expression
{
    private readonly Expression typeArg;
    private readonly Expression propertyArg;

    public TypeProperty(ParserContext context)
    {
        this.typeArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var typeValue = this.typeArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (typeValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var typeName = typeValue.CoerceTo(SqlType.NVarchar).AsString;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;

        return LookupType(typeName) is not TypeMetadata meta
            ? SqlValue.Null(SqlType.Int32)
            : EvaluateProperty(meta, prop) is int result
                ? SqlValue.FromInt32(result)
                : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>One row of the table; a null field is a property the type has
    /// no value for, which answers NULL.</summary>
    private readonly struct TypeMetadata(int? precision, int? scale, int? allowsNull, int? usesAnsiTrim)
    {
        public readonly int? Precision = precision;
        public readonly int? Scale = scale;
        public readonly int? AllowsNull = allowsNull;
        public readonly int? UsesAnsiTrim = usesAnsiTrim;
    }

    private static TypeMetadata? LookupType(string typeName)
    {
        Span<char> upper = stackalloc char[typeName.Length];
        return typeName.AsSpan().ToUpperInvariant(upper) switch
        {
            3 => upper switch
            {
                "BIT" => new(1, null, 1, null),
                "INT" => new(10, 0, 1, null),
                "XML" => new(-1, null, 1, null),
                _ => null,
            },
            4 => upper switch
            {
                "CHAR" => new(8000, null, 1, 1),
                "DATE" => new(10, 0, 1, null),
                "REAL" => new(24, null, 1, null),
                "TEXT" => new(2147483647, null, 1, null),
                "TIME" => new(16, 7, 1, null),
                _ => null,
            },
            5 => upper switch
            {
                "FLOAT" => new(53, null, 1, null),
                "IMAGE" => new(2147483647, null, 1, null),
                "MONEY" => new(19, 4, 1, null),
                "NCHAR" => new(4000, null, 1, null),
                "NTEXT" => new(1073741823, null, 1, null),
                _ => null,
            },
            6 => upper switch
            {
                "BIGINT" => new(19, 0, 1, null),
                "BINARY" => new(8000, null, 1, 1),
                _ => null,
            },
            7 => upper switch
            {
                "DECIMAL" => new(38, 38, 1, null),
                "NUMERIC" => new(38, 38, 1, null),
                "SYSNAME" => new(128, null, 0, null),
                "TINYINT" => new(3, 0, 1, null),
                "VARCHAR" => new(8000, null, 1, 1),
                _ => null,
            },
            8 => upper switch
            {
                "DATETIME" => new(23, 3, 1, null),
                "NVARCHAR" => new(4000, null, 1, null),
                "SMALLINT" => new(5, 0, 1, null),
                _ => null,
            },
            9 => upper switch
            {
                "DATETIME2" => new(27, 7, 1, null),
                "TIMESTAMP" => new(8, null, 0, null),
                "VARBINARY" => new(8000, null, 1, 1),
                _ => null,
            },
            10 => upper switch
            {
                "SMALLMONEY" => new(10, 4, 1, null),
                _ => null,
            },
            11 => upper switch
            {
                "HIERARCHYID" => new(892, null, 1, null),
                "SQL_VARIANT" => new(0, null, 1, 1),
                _ => null,
            },
            13 => upper switch
            {
                "SMALLDATETIME" => new(16, 0, 1, null),
                _ => null,
            },
            14 => upper switch
            {
                "DATETIMEOFFSET" => new(34, 7, 1, null),
                _ => null,
            },
            16 => upper switch
            {
                "UNIQUEIDENTIFIER" => new(16, null, 1, null),
                _ => null,
            },
            _ => null,
        };
    }

    private static int? EvaluateProperty(TypeMetadata meta, string property)
    {
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            5 => upper switch { "SCALE" => meta.Scale, _ => null },
            9 => upper switch { "PRECISION" => meta.Precision, _ => null },
            10 => upper switch { "ALLOWSNULL" => meta.AllowsNull, _ => null },
            12 => upper switch { "USESANSITRIM" => meta.UsesAnsiTrim, _ => null },
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"TYPEPROPERTY({this.typeArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
