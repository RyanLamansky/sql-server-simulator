using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>RAND([seed])</c>: returns a <c>float</c> in [0, 1). Probe-
/// confirmed runtime-constant semantics: a given <c>RAND()</c> call site
/// produces one value reused across every row of a result set —
/// <c>SELECT TOP 3 RAND() FROM t</c> returns three identical values, but
/// <c>SELECT RAND() AS r1, RAND() AS r2</c> returns two distinct values
/// (one per parsed call site) each replicated across rows.
/// </summary>
/// <remarks>
/// The simulator implements this by freezing the first-evaluation result in
/// the executing statement's frame
/// (<c>StatementContext.StatementScopedValues</c>, keyed by this instance) —
/// per statement <em>execution</em>, not per instance, because a plan-cached
/// <c>Selection</c> reuses one <see cref="Rand"/> across executions that must
/// each draw a fresh value. The values themselves come from the session's
/// <see cref="RandGenerator"/>, which reproduces real's sequence. A NULL seed
/// yields NULL.
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/rand-transact-sql</remarks>
internal sealed class Rand : Expression
{
    private readonly Expression? seed;

    public Rand(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.seed = Parse(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Per-STATEMENT-EXECUTION freeze, held in the statement frame rather
        // than on this instance: a plan-cached Selection reuses one Rand
        // instance across executions, each of which must draw a fresh value
        // (matching real SQL Server rolling per statement execution) while
        // every row within one execution reuses this call site's value.
        var frame = runtime.Batch.CurrentStatement;
        if (frame.StatementScopedValues is { } scoped && scoped.TryGetValue(this, out var frozen))
            return frozen;

        SqlValue result;
        if (this.seed is null)
        {
            result = SqlValue.FromDouble(runtime.Batch.Connection.Rand.Next());
        }
        else
        {
            var seedValue = this.seed.Run(runtime);
            result = seedValue.IsNull
                ? SqlValue.Null(SqlType.Float)
                : SqlValue.FromDouble(runtime.Batch.Connection.Rand.Seed(ScalarArguments.CoerceToInt(seedValue)));
        }

        (frame.StatementScopedValues ??= new Dictionary<Expression, SqlValue>(ReferenceEqualityComparer.Instance))[this] = result;
        return result;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => this.seed is null
        ? "RAND()"
        : $"RAND({this.seed.DebugDisplay()})";
}

/// <summary>
/// SQL Server's <c>RAND</c> generator, reverse-engineered from its outputs
/// (probed 2026-09-23 against SQL Server 2025): L'Ecuyer's two combined
/// multiplicative generators — Numerical Recipes' <c>ran2</c> without its
/// shuffle table — whose difference is scaled by the single-precision-rounded
/// constant <c>4.656613e-10</c> rather than an exact <c>1/2147483563</c>.
/// <c>RAND(n)</c> restarts the first generator at <c>|n|</c> (12345 for 0,
/// so <c>RAND(0)</c> equals <c>RAND(12345)</c> and <c>RAND(-1)</c> equals
/// <c>RAND(1)</c>) and the second at 67890, then draws; <c>RAND(1)</c> is
/// <c>0.7135919932129235</c>.
/// </summary>
internal sealed class RandGenerator
{
    private const int Modulus1 = 2147483563, Multiplier1 = 40014, Quotient1 = 53668, Remainder1 = 12211;
    private const int Modulus2 = 2147483399, Multiplier2 = 40692, Quotient2 = 52774, Remainder2 = 3791;
    private const double Scale = 4.656613e-10;

    /// <summary>A session that never seeds starts somewhere arbitrary, as real's does.</summary>
    private long state1 = ArbitraryStart();

    private long state2 = 67890;

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "T-SQL RAND is a non-cryptographic pseudo-random source.")]
    private static long ArbitraryStart() => Random.Shared.Next(1, Modulus1);

    public double Seed(int seed)
    {
        this.state1 = seed == 0 ? 12345 : Math.Abs((long)seed);
        this.state2 = 67890;
        return this.Next();
    }

    public double Next()
    {
        // Schrage's method keeps each product inside 64 bits' comfortable range.
        var k = this.state1 / Quotient1;
        this.state1 = (Multiplier1 * (this.state1 - (k * Quotient1))) - (k * Remainder1);
        if (this.state1 < 0)
            this.state1 += Modulus1;
        k = this.state2 / Quotient2;
        this.state2 = (Multiplier2 * (this.state2 - (k * Quotient2))) - (k * Remainder2);
        if (this.state2 < 0)
            this.state2 += Modulus2;
        var difference = this.state1 - this.state2;
        if (difference < 1)
            difference += Modulus1 - 1;
        return difference * Scale;
    }
}
