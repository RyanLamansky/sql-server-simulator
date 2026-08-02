using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>MIN_ACTIVE_ROWVERSION()</c>: returns the smallest active
/// rowversion value as <c>binary(8)</c>. The simulator doesn't track
/// per-transaction reservation of rowversion values, so this returns
/// the current next-to-be-allocated rowversion — a safe-over-approximation
/// of "the lowest value that any uncommitted transaction might still
/// commit at".
/// </summary>
internal sealed class MinActiveRowVersion : Expression
{
    private static readonly SqlType Binary8 = SqlType.GetBinary(8);

    public MinActiveRowVersion(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("min_active_rowversion", 0);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var current = runtime.Batch.CurrentDatabase.AllocateRowVersion();
        var bytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)(current & 0xff);
            current >>= 8;
        }
        return SqlValue.FromBinary(Binary8, bytes);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => Binary8;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "MIN_ACTIVE_ROWVERSION()";
}

/// <summary>
/// SQL <c>CHECKSUM(arg1, arg2, ...)</c> and <c>BINARY_CHECKSUM</c>:
/// return a fast <c>int</c> hash over the arguments. The semantic
/// guarantee is the relevant one: equal inputs produce equal outputs.
/// Real SQL Server uses an unpublished CRC-like algorithm; the
/// simulator uses a deterministic 32-bit FNV-1a fold over the value
/// representations (so output is reproducible but not byte-identical
/// to real SQL Server — documented as a quirk).
/// </summary>
internal sealed class Checksum : Expression
{
    private readonly bool isBinary;
    private readonly Expression[] args;

    public Checksum(ParserContext context, bool isBinary)
    {
        this.isBinary = isBinary;
        var list = new List<Expression> { Parse(context) };
        while (context.Token is Tokens.Operator { Character: ',' })
            list.Add(Parse(context.MoveNextRequiredReturnSelf()));
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.args = [.. list];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var hash = 2166136261u;
        foreach (var a in this.args)
        {
            var v = a.Run(runtime);
            if (v.IsNull)
            {
                hash = FoldByte(hash, 0xff);
                continue;
            }
            foreach (var b in BytesForValue(v, this.isBinary))
                hash = FoldByte(hash, b);
        }
        return SqlValue.FromInt32((int)hash);
    }

    private static uint FoldByte(uint h, byte b) => (h ^ b) * 16777619u;

    private static byte[] BytesForValue(SqlValue v, bool isBinary)
    {
        var t = v.Type;
        if (SqlType.IsStringCategory(t))
        {
            var s = v.CoerceTo(SqlType.NVarchar).AsString;
            // BINARY_CHECKSUM is case-sensitive byte-level; CHECKSUM is
            // collation-aware (case-insensitive under default CI_AS).
            if (!isBinary)
                s = s.ToUpperInvariant();
            return Encoding.Unicode.GetBytes(s);
        }
        if (t == SqlType.Int32) return BitConverter.GetBytes(v.AsInt32);
        if (t == SqlType.BigInt) return BitConverter.GetBytes(v.AsInt64);
        if (t == SqlType.SmallInt) return BitConverter.GetBytes(v.AsInt16);
        if (t == SqlType.TinyInt) return [v.AsByte];
        if (t == SqlType.Bit) return [(byte)(v.AsBoolean ? 1 : 0)];
        if (t == SqlType.Float) return BitConverter.GetBytes(v.AsDouble);
        if (t == SqlType.Real) return BitConverter.GetBytes(v.AsSingle);
        if (t is BinarySqlType or VarbinarySqlType) return v.AsBytes;
        if (t is DecimalSqlType) return Encoding.UTF8.GetBytes(v.AsDecimal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Date/time, guid, others — fall back to canonical string form.
        return Encoding.Unicode.GetBytes(v.CoerceTo(SqlType.NVarchar).AsString);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"{(this.isBinary ? "BINARY_CHECKSUM" : "CHECKSUM")}(...{this.args.Length} args)";
}
