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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var anyNational = false;
        var anyMax = false;
        var width = 0;
        var separatorWidth = 0;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            var type = this.arguments[i].GetSqlType(batch, resolveColumnType);
            anyNational |= IsNationalString(type);
            anyMax |= IsMaxForm(type);
            var argumentWidth = IsBareNullLiteral(this.arguments[i]) ? 0 : ArgumentWidth(type);
            if (this.kind == StringConcatKind.ConcatWs && i == 0)
                separatorWidth = argumentWidth;
            else
                width += argumentWidth;
        }
        return ResolveResultType(anyNational, anyMax, width + SeparatorTotal(separatorWidth), batch);
    }

    // CONCAT / CONCAT_WS never return NULL — NULL arguments are skipped and an
    // all-NULL input yields the empty string (a NULL CONCAT_WS separator degrades
    // to empty too), so the result-set metadata reports the column NOT NULL
    // regardless of operand nullability (probe-confirmed against SQL Server 2025;
    // exposed by go-mssqldb / tedious COLMETADATA fNullable).
    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => false;

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Resolve result type from runtime argument types: any national-string
        // input promotes to nvarchar, and any MAX-typed input widens the result
        // to MAX so a concatenation larger than the bounded wire prefix streams
        // as PLP. Computed inline (not from a GetSqlType cache) because Run is
        // reachable when GetSqlType wasn't called — e.g. when this expression is
        // nested inside a function whose own GetSqlType doesn't cascade into
        // operand types.
        var values = new SqlValue[this.arguments.Length];
        var anyNational = false;
        var anyMax = false;
        var width = 0;
        var separatorWidth = 0;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            values[i] = this.arguments[i].Run(runtime);
            anyNational |= IsNationalString(values[i].Type);
            anyMax |= IsMaxForm(values[i].Type);
            var argumentWidth = IsBareNullLiteral(this.arguments[i]) ? 0 : ArgumentWidth(values[i].Type);
            if (this.kind == StringConcatKind.ConcatWs && i == 0)
                separatorWidth = argumentWidth;
            else
                width += argumentWidth;
        }
        var resultType = ResolveResultType(anyNational, anyMax, width + SeparatorTotal(separatorWidth), runtime.Batch);

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

    /// <summary>
    /// A MAX-form argument (<c>varchar(max)</c> / <c>nvarchar(max)</c> or a
    /// <c>text</c> / <c>ntext</c> LOB) makes CONCAT / CONCAT_WS return a MAX
    /// result — probe-confirmed against SQL Server 2025. A bounded / literal
    /// argument does not.
    /// </summary>
    private static bool IsMaxForm(SqlType type) =>
        type.IsLob
            || type is NVarcharSqlType { length: SqlType.MaxLengthSentinel }
            || type is VarcharSqlType { length: SqlType.MaxLengthSentinel };

    /// <summary>
    /// National family wins <c>nvarchar</c> over <c>varchar</c>; a MAX input
    /// widens the chosen family to its MAX form. Otherwise the result is a
    /// bounded <c>varchar(width)</c> / <c>nvarchar(width)</c> — the sum of the
    /// per-argument widths (capped at 8000 / 4000, floored at 1) SQL Server
    /// projects for the described result (probe-confirmed 2026-07-22:
    /// <c>CONCAT('a',1,NULL,'b')</c> → <c>varchar(14)</c>,
    /// <c>CONCAT_WS('-','a','b','c')</c> → <c>varchar(5)</c>).
    /// </summary>
    private static SqlType ResolveResultType(bool anyNational, bool anyMax, int width, BatchContext batch) =>
        anyMax
            ? (anyNational ? SqlType.NVarcharMax : SqlType.VarcharMax)
            : StringScalars.SizedResultType(anyNational ? SqlType.NVarchar : SqlType.Varchar, width, batch);

    /// <summary>
    /// The separators' total contribution to a CONCAT_WS width: one separator
    /// between each pair of value arguments (value count = argument count − 1,
    /// so separator count = argument count − 2). Zero for CONCAT, which has no
    /// separator argument.
    /// </summary>
    private int SeparatorTotal(int separatorWidth) =>
        this.kind == StringConcatKind.ConcatWs ? separatorWidth * Math.Max(0, this.arguments.Length - 2) : 0;

    /// <summary>
    /// The maximum string width a single CONCAT / CONCAT_WS argument of
    /// <paramref name="type"/> contributes to the result length — probe-confirmed
    /// against SQL Server 2025 (2026-07-22). A string type contributes its
    /// declared length; the fixed-width types contribute their documented
    /// implicit-conversion maxima (bit 1, tinyint 4, smallint 6, int 12,
    /// bigint 24, real / float 23, money / smallmoney 40, decimal / numeric 41,
    /// date / time / datetime / datetimeoffset / uniqueidentifier 40).
    /// MAX-form arguments never drive the width — they route through
    /// <see cref="IsMaxForm"/> and force a MAX result. A bare untyped NULL
    /// literal contributes 0 (the caller special-cases it via
    /// <see cref="Expression.IsBareNullLiteral"/>).
    /// </summary>
    private static int ArgumentWidth(SqlType type) => type switch
    {
        VarcharSqlType v => v.length,
        NVarcharSqlType nv => nv.length,
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length,
        _ when type == SqlType.Bit => 1,
        _ when type == SqlType.TinyInt => 4,
        _ when type == SqlType.SmallInt => 6,
        _ when type == SqlType.Int32 => 12,
        _ when type == SqlType.BigInt => 24,
        _ => type.Category switch
        {
            SqlTypeCategory.Approximate => 23,
            SqlTypeCategory.Decimal => 41,
            SqlTypeCategory.DateTime => 40,
            SqlTypeCategory.Money => 40,
            SqlTypeCategory.UniqueIdentifier => 40,
            _ => 8000,
        },
    };

    private static string LowercaseName(StringConcatKind kind) => kind switch
    {
        StringConcatKind.Concat => "concat",
        StringConcatKind.ConcatWs => "concat_ws",
        _ => throw new InvalidOperationException($"Unknown {nameof(StringConcatKind)} {kind}."),
    };

    internal override string DebugDisplay() =>
        $"{LowercaseName(this.kind).ToUpperInvariant()}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
