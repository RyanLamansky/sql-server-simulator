using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TERTIARY_WEIGHTS(expression)</c>: one tertiary sort weight per
/// character of a <c>char</c> / <c>varchar</c> value under one of the SQL
/// collations whose names carry <c>Pref</c> — 2 for a lowercase letter, 1 for
/// anything else, and a pair for a character that sorts as two (<c>ß</c> is
/// <c>0x0101</c>, <c>æ</c> <c>0x0202</c>), so a case-insensitive index can
/// order uppercase first. Under any other collation the result is NULL, and a
/// MAX argument yields no weights at all. The result is
/// <c>varbinary(2 × n)</c>, capped at 8000, or <c>varbinary(max)</c>.
/// Anything but <c>char</c> / <c>varchar</c> is Msg 8116 while compiling.
/// Every byte of each code page's table was read off real (probed 2026-10-06
/// against SQL Server 2025) — the code page 850 and 437 ones from databases
/// whose default collation stores those bytes unconverted.
/// </summary>
internal sealed class TertiaryWeights(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.operand.Run(runtime);
        var resultType = ResultType(value.Type);
        var collation = value.Type.Collation ?? Collation.Baseline;
        if (value.IsNull || Table(collation.Name) is not { } table)
            return SqlValue.Null(resultType);
        if (value.Type is VarcharSqlType { length: -1 })
            return SqlValue.FromVarbinary(resultType, []);

        var weights = new List<byte>();
        foreach (var b in collation.StorageEncoding.GetBytes(value.AsString))
        {
            switch (table[b])
            {
                case 'A':
                    weights.Add(1);
                    weights.Add(1);
                    break;
                case 'B':
                    weights.Add(2);
                    weights.Add(2);
                    break;
                case var digit:
                    weights.Add((byte)(digit - '0'));
                    break;
            }
        }
        return SqlValue.FromVarbinary(resultType, [.. weights]);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = this.operand.GetSqlType(batch, resolveColumnType);
        if (type is not (VarcharSqlType or CharSqlType) || IsUntypedNullLiteral(this.operand))
        {
            throw SimulatedSqlException.InvalidArgumentDataType(
                IsUntypedNullLiteral(this.operand) ? "NULL" : SqlType.OperandName(type, this.operand), 1, "tertiary_weights");
        }
        return ResultType(type);
    }

    private static VarbinarySqlType ResultType(SqlType argument) => argument switch
    {
        VarcharSqlType { length: -1 } => SqlType.VarbinaryMax,
        VarcharSqlType varchar => VarbinarySqlType.Get(Math.Min(2 * varchar.length, 8000)),
        CharSqlType fixedLength => VarbinarySqlType.Get(Math.Min(2 * fixedLength.length, 8000)),
        _ => SqlType.VarbinaryMax,
    };

    /// <summary>
    /// Per byte of the collation's code page, its weight digit, or <c>A</c> /
    /// <c>B</c> for the pairs <c>0x0101</c> / <c>0x0202</c>; null for a
    /// collation that has no tertiary weights.
    /// </summary>
    private static string? Table(string collationName)
    {
        Span<char> upper = stackalloc char[collationName.Length];
        _ = collationName.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "SQL_ALTDICTION_PREF_CP850_CI_AS" or "SQL_LATIN1_GENERAL_PREF_CP850_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111111111111112222222222222222222222222211111"
                + "12222222222222111BA222221112111122222111111111111111111111111111"
                + "111111211111111121111111111111111A112111211121111111111111111111",
            "SQL_DANISH_PREF_CP1_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111111111111112222222222222222222222222211111"
                + "111111111111A111111111111121B11111111111111111111111111111111111"
                + "111111111111111111111111111111AA222222222222222222222221222222B2",
            "SQL_ICELANDIC_PREF_CP1_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111111111111112222222222222222222222222211111"
                + "111111111111A111111111111121B11111111111111111111111111111111111"
                + "1111111111111111111111111111111A22222222222222222222222122222222",
            "SQL_LATIN1_GENERAL_PREF_CP1_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111111111111112222222222222222222222222211111"
                + "1111111111111111111111111111111111111111111111111111111111111111"
                + "111111A111111111111111111111111A222222B2222222222222222122222221",
            "SQL_LATIN1_GENERAL_PREF_CP437_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111111111111112222222222222222222222222211111"
                + "12212122111111111BA121111111111111112111111111111111111111111111"
                + "1111111111111111111111111111111111111211111111111111111111111111",
            "SQL_SCANDINAVIAN_PREF_CP850_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111131111111112222222222222222222222422211111"
                + "1222222222222211121222221112111122222111111111111111111111111111"
                + "111111211111111121111111111111111A112111211121111111111111111111",
            "SQL_SWEDISHPHONE_PREF_CP1_CI_AS" or "SQL_SWEDISHSTD_PREF_CP1_CI_AS" =>
                "1111111111111111111111111111111111111111111111111111111111111111"
                + "1111111111111111111111111111111112222222222222222222222222211111"
                + "111111111111A111111111111121B11111111111111111111111111111111111"
                + "111111A11111111111111111111111AA222222B22222222222222221222222B2",
            _ => null,
        };
    }

    internal override string DebugDisplay() => $"TERTIARY_WEIGHTS({this.operand.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.operand);
}
