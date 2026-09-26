using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server 2025's <c>BASE64_ENCODE(varbinary [, url_safe])</c>: the
/// padded standard alphabet, or with a non-zero <c>url_safe</c> the
/// <c>-</c> / <c>_</c> alphabet unpadded, as <c>varchar(8000)</c> —
/// <c>varchar(max)</c> over a MAX input. Only a binary operand is taken (Msg
/// 8116); NULL answers NULL (probed 2026-09-26 against SQL Server 2025).
/// </summary>
internal sealed class Base64Encode : Expression
{
    private readonly Expression input;
    private readonly Expression? urlSafe;

    public Base64Encode(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            this.urlSafe = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.input.Run(runtime);
        var resultType = ResultTypeFor(value.Type, runtime.Batch);
        var flag = this.urlSafe?.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(resultType);
        var encoded = Convert.ToBase64String(value.AsBytes);
        // A NULL flag means the standard alphabet (probed 2026-09-26 against
        // SQL Server 2025).
        if (flag is { IsNull: false } urlSafeFlag && urlSafeFlag.CoerceTo(SqlType.BigInt).AsInt64 != 0)
            encoded = encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return SqlValue.FromString(resultType, encoded);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = this.input.GetSqlType(batch, resolveColumnType);
        if (inputType is not (VarbinarySqlType or BinarySqlType) && !IsUntypedNullLiteral(this.input))
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(inputType, this.input), 1, "base64_encode");
        // The URL-safe flag is an integer or a bit and nothing else.
        if (this.urlSafe is not null
            && this.urlSafe.GetSqlType(batch, resolveColumnType) is var flagType
            && flagType.Category != SqlTypeCategory.Integer
            && !IsUntypedNullLiteral(this.urlSafe))
        {
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(flagType, this.urlSafe), 2, "base64_encode");
        }
        return ResultTypeFor(inputType, batch);
    }

    private static VarcharSqlType ResultTypeFor(SqlType inputType, BatchContext batch) => VarcharSqlType.Get(
        inputType is VarbinarySqlType { length: SqlType.MaxLengthSentinel } ? SqlType.MaxLengthSentinel : 8000,
        batch.CurrentDatabase.Collation,
        Coercibility.CoercibleDefault);

    internal override string DebugDisplay() => $"BASE64_ENCODE({this.input.DebugDisplay()})";

    internal override void Describe(NodeShape shape)
    {
        _ = shape.Child(this.input);
        if (this.urlSafe is not null)
            _ = shape.Child(this.urlSafe);
    }
}

/// <summary>
/// SQL Server 2025's <c>BASE64_DECODE(varchar)</c>: reads either alphabet,
/// padded or not, as <c>varbinary(8000)</c> — <c>varbinary(max)</c> over a MAX
/// input. Only a <c>char</c> / <c>varchar</c> operand is taken (Msg 8116), and
/// text that isn't Base64 is Msg 9803 (probed 2026-09-26 against SQL Server
/// 2025).
/// </summary>
internal sealed class Base64Decode : Expression
{
    private readonly Expression input;

    public Base64Decode(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.input.Run(runtime);
        var resultType = ResultTypeFor(value.Type);
        if (value.IsNull)
            return SqlValue.Null(resultType);
        return SqlValue.FromVarbinary(resultType, Decode(value.AsString));
    }

    /// <summary>
    /// Decodes either alphabet, padded or not, skipping whitespace, and
    /// refuses anything else with the Msg 9803 state real gives for what was
    /// wrong (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static byte[] Decode(string text)
    {
        var data = new System.Text.StringBuilder(text.Length);
        var padding = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;
            if (c == '=')
            {
                padding++;
                continue;
            }
            if (padding > 0)
                throw SimulatedSqlException.InvalidBase64Data(22);
            _ = data.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ when char.IsAsciiLetterOrDigit(c) || c is '+' or '/' => c,
                _ => throw SimulatedSqlException.InvalidBase64Data(20),
            });
        }
        if (data.Length % 4 == 1)
            throw SimulatedSqlException.InvalidBase64Data(21);
        var needed = (4 - (data.Length % 4)) % 4;
        if (padding > needed)
            throw SimulatedSqlException.InvalidBase64Data(23);
        _ = data.Append('=', needed);
        return Convert.FromBase64String(data.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = this.input.GetSqlType(batch, resolveColumnType);
        return inputType is VarcharSqlType or CharSqlType || IsUntypedNullLiteral(this.input)
            ? ResultTypeFor(inputType)
            : throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(inputType, this.input), 1, "base64_decode");
    }

    private static VarbinarySqlType ResultTypeFor(SqlType inputType) =>
        inputType is VarcharSqlType { length: SqlType.MaxLengthSentinel } ? VarbinarySqlType.MaxForm : VarbinarySqlType.Get(8000);

    internal override string DebugDisplay() => $"BASE64_DECODE({this.input.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.input);
}
