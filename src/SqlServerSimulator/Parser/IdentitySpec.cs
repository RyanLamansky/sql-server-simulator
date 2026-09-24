using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The seed and increment an identity declaration writes — a column's
/// <c>IDENTITY(seed, increment)</c> or <c>SELECT … INTO</c>'s
/// <c>IDENTITY(type, seed, increment)</c> function — as read, before the
/// column's type is known to judge them against.
/// </summary>
/// <remarks>
/// Both take only a numeric literal, optionally signed: a variable, an
/// expression, a float (<c>1e0</c>) or a binary (<c>0x01</c>) literal is Msg
/// 102 near it. A fractional literal is a Msg 2752 (seed) or 2753
/// (increment) of its own state, even <c>1.0</c>, as is one outside the
/// column type's range and a zero increment (probed 2026-09-24 against SQL
/// Server 2025, the column and the function alike).
/// </remarks>
internal readonly struct IdentitySpec
{
    /// <summary><c>IDENTITY</c> written without arguments: <c>(1, 1)</c>.</summary>
    public static readonly IdentitySpec Default = new(1, true, 1, true);

    private readonly Int128 seed;
    private readonly bool seedIsInteger;
    private readonly Int128 increment;
    private readonly bool incrementIsInteger;

    private IdentitySpec(Int128 seed, bool seedIsInteger, Int128 increment, bool incrementIsInteger)
    {
        this.seed = seed;
        this.seedIsInteger = seedIsInteger;
        this.increment = increment;
        this.incrementIsInteger = incrementIsInteger;
    }

    /// <summary>
    /// Reads <c>seed , increment</c>. Enters on the seed's first token and
    /// leaves on the token after the increment.
    /// </summary>
    public static IdentitySpec ReadArguments(ParserContext context)
    {
        var (seed, seedIsInteger) = ReadLiteral(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var (increment, incrementIsInteger) = ReadLiteral(context);
        return new(seed, seedIsInteger, increment, incrementIsInteger);
    }

    private static (Int128 Value, bool IsInteger) ReadLiteral(ParserContext context)
    {
        var negative = false;
        if (context.Token is Operator { Character: '-' or '+' } sign)
        {
            negative = sign.Character == '-';
            context.MoveNextRequired();
        }
        if (context.Token is not Numeric numeric)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var value = numeric.Value;
        var (magnitude, isInteger) = value.Type switch
        {
            Int32SqlType => ((Int128)value.AsInt32, true),
            DecimalSqlType => ((Int128)value.AsDecimal38.Magnitude, value.AsDecimal38.Scale == 0),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextRequired();
        return (negative ? -magnitude : magnitude, isInteger);
    }

    /// <summary>
    /// Judges the seed and increment against the identity column's
    /// <paramref name="type"/>, already known to be one an identity may
    /// take, and builds its state.
    /// </summary>
    public IdentityState Resolve(SqlType type, string columnName, bool notForReplication = false)
    {
        if (!this.seedIsInteger)
            throw SimulatedSqlException.IdentityInvalidSeed(columnName, 2);
        if (!IdentityState.Fits(this.seed, type))
            throw SimulatedSqlException.IdentityInvalidSeed(columnName, 1);
        if (!this.incrementIsInteger)
            throw SimulatedSqlException.IdentityInvalidIncrement(columnName, 3);
        if (this.increment == 0)
            throw SimulatedSqlException.IdentityInvalidIncrement(columnName, 2);
        if (!IdentityState.Fits(this.increment, type))
            throw SimulatedSqlException.IdentityInvalidIncrement(columnName, 1);
        if (this.seed < long.MinValue || this.seed > long.MaxValue || this.increment < long.MinValue || this.increment > long.MaxValue)
            throw new NotSupportedException("A decimal identity seed or increment beyond bigint's range is not modeled yet.");
        return new IdentityState((long)this.seed, (long)this.increment, notForReplication);
    }
}
