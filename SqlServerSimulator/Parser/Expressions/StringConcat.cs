using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator selecting <c>CONCAT</c> vs <c>CONCAT_WS</c>. Determines the
/// minimum-argument count enforced via Msg 189 and whether the first argument
/// is interpreted as a separator.
/// </summary>
internal enum StringConcatKind
{
    /// <summary><c>CONCAT(expr1, expr2 [, ...])</c> — minimum 2 arguments.</summary>
    Concat,

    /// <summary><c>CONCAT_WS(separator, expr1, expr2 [, ...])</c> — minimum 3 arguments.</summary>
    ConcatWs,
}

/// <summary>
/// Backs <c>CONCAT</c> and <c>CONCAT_WS</c>. Both functions stringify each
/// argument via the standard CAST-to-varchar/nvarchar coercion path, skip NULL
/// arguments (rather than propagating NULL), and never return NULL — an
/// all-NULL input produces an empty string. Result type is <c>nvarchar</c>
/// when any argument is a national string type (<c>nvarchar</c> /
/// <c>nchar</c> / <c>ntext</c>); otherwise <c>varchar</c>.
/// </summary>
/// <remarks>
/// <para>
/// EF Core 10 emits <c>CONCAT</c> from server-evaluated <c>string.Concat</c>
/// and <c>CONCAT_WS</c> from <c>string.Join(sep, value1, value2, ...)</c> over
/// scalar arguments.
/// </para>
/// <para>
/// <c>CONCAT_WS</c> quirks confirmed against SQL Server 2025 (2026-05-09):
/// the separator being NULL silently degrades to empty-string (does NOT
/// propagate NULL); NULL value arguments are skipped entirely so no double
/// separators appear next to a missing value; the function requires at least
/// one separator and two values (3 arguments total) — fewer raises Msg 189.
/// </para>
/// <para>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/concat-transact-sql
/// </para>
/// </remarks>
internal sealed class StringConcat : Expression
{
    private readonly StringConcatKind kind;
    private readonly Expression[] arguments;

    public StringConcat(ParserContext context, StringConcatKind kind)
    {
        this.kind = kind;
        var min = kind == StringConcatKind.Concat ? 2 : 3;

        List<Expression> args = [];
        // Empty argument list (`concat()` / `concat_ws()`) leaves context on
        // the closing `)` already; the count check below produces Msg 189
        // rather than Msg 102 from a downstream Expression.Parse failure.
        if (context.Token is not Tokens.Operator { Character: ')' })
        {
            args.Add(Expression.Parse(context));
            while (context.Token is Tokens.Operator { Character: ',' })
            {
                context.MoveNextRequired();
                args.Add(Expression.Parse(context));
            }
        }

        if (args.Count < min)
            throw SimulatedSqlException.FunctionArgumentCount(LowercaseName(kind), min);

        this.arguments = [.. args];
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        for (var i = 0; i < this.arguments.Length; i++)
        {
            if (IsNationalString(this.arguments[i].GetSqlType(resolveColumnType)))
                return SqlType.NVarchar;
        }
        return SqlType.Varchar;
    }

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        // Resolve result type from runtime argument types: any national-string
        // input promotes to nvarchar. Computed inline (not from a GetSqlType
        // cache) because Run is reachable when GetSqlType wasn't called — e.g.
        // when this expression is nested inside a function whose own
        // GetSqlType doesn't cascade into operand types.
        var values = new SqlValue[this.arguments.Length];
        var anyNational = false;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            values[i] = this.arguments[i].Run(getColumnValue);
            if (IsNationalString(values[i].Type))
                anyNational = true;
        }
        SqlType resultType = anyNational ? SqlType.NVarchar : SqlType.Varchar;

        if (this.kind == StringConcatKind.Concat)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].IsNull)
                    continue;
                _ = sb.Append(StringifyForConcat(values[i], resultType));
            }
            return SqlValue.FromString(resultType, sb.ToString());
        }

        // CONCAT_WS: values[0] is the separator. NULL separator silently
        // degrades to empty string (probe-confirmed) — does NOT propagate NULL
        // and does NOT raise an error.
        var separator = values[0].IsNull ? string.Empty : StringifyForConcat(values[0], resultType);
        var output = new StringBuilder();
        var emittedAny = false;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i].IsNull)
                continue;
            if (emittedAny)
                _ = output.Append(separator);
            _ = output.Append(StringifyForConcat(values[i], resultType));
            emittedAny = true;
        }
        return SqlValue.FromString(resultType, output.ToString());
    }

    /// <summary>
    /// Coerces a non-NULL <see cref="SqlValue"/> to the result string type and
    /// returns the underlying string. String-category sources copy through;
    /// other categories pass through <see cref="SqlValue.CoerceTo"/>, which
    /// applies the same default formats as a <c>CAST(expr AS varchar)</c> /
    /// <c>CAST(expr AS nvarchar)</c>.
    /// </summary>
    private static string StringifyForConcat(SqlValue value, SqlType resultType) =>
        SqlType.IsStringCategory(value.Type)
            ? value.AsString
            : value.CoerceTo(resultType).AsString;

    private static bool IsNationalString(SqlType type) =>
        type is NVarcharSqlType or NCharSqlType || type == SqlType.NText;

    private static string LowercaseName(StringConcatKind kind) => kind switch
    {
        StringConcatKind.Concat => "concat",
        StringConcatKind.ConcatWs => "concat_ws",
        _ => throw new InvalidOperationException($"Unknown {nameof(StringConcatKind)} {kind}."),
    };

    internal override string DebugDisplay() =>
        $"{LowercaseName(this.kind).ToUpperInvariant()}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
