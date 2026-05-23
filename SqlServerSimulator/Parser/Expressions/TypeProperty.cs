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
/// <c>varchar</c>=8000, <c>money</c>=19).</description></item>
/// <item><description><c>Scale</c> — the type's catalog scale
/// (<c>int</c>=0, <c>money</c>=4, <c>decimal</c>=38).</description></item>
/// <item><description><c>AllowsNull</c> — 1 for nullable types, 0 for
/// <c>rowversion</c> / <c>sysname</c>.</description></item>
/// <item><description><c>UsesAnsiTrim</c> — 1 for character types
/// (the simulator's ANSI-trim behavior is always on), 0 otherwise.</description></item>
/// </list>
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

    private readonly record struct TypeMetadata(int Precision, int Scale, bool AllowsNull, bool UsesAnsiTrim);

    private static TypeMetadata? LookupType(string typeName)
    {
        Span<char> upper = stackalloc char[typeName.Length];
        return typeName.AsSpan().ToUpperInvariant(upper) switch
        {
            3 => upper switch
            {
                "INT" => new(10, 0, true, false),
                "BIT" => new(1, 0, true, false),
                "XML" => new(2147483647, 0, true, false),
                _ => null,
            },
            4 => upper switch
            {
                "CHAR" => new(8000, 0, true, true),
                "DATE" => new(10, 0, true, false),
                "REAL" => new(24, 0, true, false),
                "TEXT" => new(2147483647, 0, true, false),
                "TIME" => new(16, 7, true, false),
                _ => null,
            },
            5 => upper switch
            {
                "FLOAT" => new(53, 0, true, false),
                "IMAGE" => new(2147483647, 0, true, false),
                "MONEY" => new(19, 4, true, false),
                "NCHAR" => new(4000, 0, true, true),
                "NTEXT" => new(1073741823, 0, true, false),
                _ => null,
            },
            6 => upper switch
            {
                "BIGINT" => new(19, 0, true, false),
                "BINARY" => new(8000, 0, true, false),
                _ => null,
            },
            7 => upper switch
            {
                "DECIMAL" => new(38, 38, true, false),
                "INTEGER" => new(10, 0, true, false),
                "NUMERIC" => new(38, 38, true, false),
                "SYSNAME" => new(128, 0, false, true),
                "TINYINT" => new(3, 0, true, false),
                "VARCHAR" => new(8000, 0, true, true),
                _ => null,
            },
            8 => upper switch
            {
                "DATETIME" => new(23, 3, true, false),
                "NVARCHAR" => new(4000, 0, true, true),
                "SMALLINT" => new(5, 0, true, false),
                _ => null,
            },
            9 => upper switch
            {
                "DATETIME2" => new(27, 7, true, false),
                "TIMESTAMP" => new(8, 0, false, false),
                "VARBINARY" => new(8000, 0, true, false),
                _ => null,
            },
            10 => upper switch
            {
                "ROWVERSION" => new(8, 0, false, false),
                "SMALLMONEY" => new(10, 4, true, false),
                _ => null,
            },
            11 => upper switch
            {
                "HIERARCHYID" => new(892, 0, true, false),
                "SQL_VARIANT" => new(0, 0, true, false),
                _ => null,
            },
            13 => upper switch
            {
                "SMALLDATETIME" => new(16, 0, true, false),
                _ => null,
            },
            14 => upper switch
            {
                "DATETIMEOFFSET" => new(34, 7, true, false),
                _ => null,
            },
            16 => upper switch
            {
                "UNIQUEIDENTIFIER" => new(0, 0, true, false),
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
            10 => upper switch { "ALLOWSNULL" => meta.AllowsNull ? 1 : 0, _ => null },
            12 => upper switch { "USESANSITRIM" => meta.UsesAnsiTrim ? 1 : 0, _ => null },
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"TYPEPROPERTY({this.typeArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
