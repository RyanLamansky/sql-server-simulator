using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Security.Cryptography;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>CRYPT_GEN_RANDOM(length [, seed])</c>: <c>length</c> fresh
/// random bytes as <c>varbinary(8000)</c>. Only an <c>int</c> length and a
/// <c>varbinary</c> seed are taken (Msg 8116, even for <c>tinyint</c>); a
/// length outside 1–8000, or a non-empty seed shorter than it, answers NULL;
/// a seed past 8000 bytes is Msg 8152 converting to the parameter's
/// <c>varbinary(8000)</c> (probed 2026-09-26 against SQL Server 2025). The
/// seed takes no part in the draw.
/// </summary>
internal sealed class CryptGenRandom : Expression
{
    private const string FunctionName = "Crypt_Gen_Random";

    private readonly Expression length;
    private readonly Expression? seed;

    public CryptGenRandom(ParserContext context)
    {
        if (context.Token is Operator { Character: ')' })
            throw SimulatedSqlException.FunctionArgumentCountRange(FunctionName, 1, 2);
        this.length = Parse(context);
        if (context.Token is Operator { Character: ',' })
            this.seed = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.FunctionArgumentCountRange(FunctionName, 1, 2);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var lengthValue = this.length.Run(runtime);
        var seedValue = this.seed?.Run(runtime);
        runtime.Batch.Connection.VolatileEvaluations++;
        if (lengthValue.IsNull)
            return SqlValue.Null(ResultType);
        var count = lengthValue.AsInt32;
        var seedLength = seedValue is { IsNull: false } written ? written.AsBytes.Length : 0;
        if (seedLength > 8000)
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy(10);
        return count is < 1 or > 8000 || (seedLength > 0 && seedLength < count)
            ? SqlValue.Null(ResultType)
            : SqlValue.FromVarbinary(ResultType, RandomNumberGenerator.GetBytes(count));
    }

    private static VarbinarySqlType ResultType => VarbinarySqlType.Get(8000);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var lengthType = this.length.GetSqlType(batch, resolveColumnType);
        if (IsUntypedNullLiteral(this.length))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 1, FunctionName);
        if (lengthType != SqlType.Int32)
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(lengthType, this.length), 1, FunctionName);
        if (this.seed is { } seedArgument
            && seedArgument.GetSqlType(batch, resolveColumnType) is var seedType
            && seedType is not VarbinarySqlType
            && !IsUntypedNullLiteral(seedArgument))
        {
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(seedType, seedArgument), 2, FunctionName);
        }
        return ResultType;
    }

    internal override string DebugDisplay() => $"CRYPT_GEN_RANDOM({this.length.DebugDisplay()})";

    internal override void Describe(NodeShape shape)
    {
        _ = shape.Child(this.length);
        if (this.seed is not null)
            _ = shape.Child(this.seed);
    }
}
