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
        if (value.IsNull || flag is { IsNull: true })
            return SqlValue.Null(resultType);
        var encoded = Convert.ToBase64String(value.AsBytes);
        if (flag is { } urlSafeFlag && urlSafeFlag.CoerceTo(SqlType.BigInt).AsInt64 != 0)
            encoded = encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return SqlValue.FromString(resultType, encoded);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = this.input.GetSqlType(batch, resolveColumnType);
        if (inputType is not (VarbinarySqlType or BinarySqlType) && !IsUntypedNullLiteral(this.input))
            throw SimulatedSqlException.InvalidArgumentDataType(inputType.SqlServerName, 1, "base64_encode");
        _ = this.urlSafe?.GetSqlType(batch, resolveColumnType);
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
        var text = value.AsString.Replace('-', '+').Replace('_', '/');
        if (text.Length % 4 == 1)
            throw SimulatedSqlException.InvalidBase64Data();
        text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        var bytes = new byte[text.Length / 4 * 3];
        return Convert.TryFromBase64String(text, bytes, out var written)
            ? SqlValue.FromVarbinary(resultType, bytes.AsSpan(0, written).ToArray())
            : throw SimulatedSqlException.InvalidBase64Data();
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = this.input.GetSqlType(batch, resolveColumnType);
        return inputType is VarcharSqlType or CharSqlType
            ? ResultTypeFor(inputType)
            : throw SimulatedSqlException.InvalidArgumentDataType(inputType.SqlServerName, 1, "base64_decode");
    }

    private static VarbinarySqlType ResultTypeFor(SqlType inputType) =>
        inputType is VarcharSqlType { length: SqlType.MaxLengthSentinel } ? VarbinarySqlType.MaxForm : VarbinarySqlType.Get(8000);

    internal override string DebugDisplay() => $"BASE64_DECODE({this.input.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.input);
}
