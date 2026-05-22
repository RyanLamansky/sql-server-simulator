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
/// The simulator implements this by caching the first-evaluation result on
/// the <see cref="Rand"/> expression instance — each parsed
/// <c>RAND(...)</c> call lives in exactly one expression tree, so a fresh
/// parse (each batch / each statement) gets a fresh cache. With an
/// argument, the seed value chooses the cached value via
/// <see cref="Random"/> seeded from a hash; same-seed → same-value match
/// is deterministic within a process lifetime but not byte-identical to
/// SQL Server's undocumented seed algorithm. A NULL seed yields NULL.
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/rand-transact-sql</remarks>
internal sealed class Rand : Expression
{
    private readonly Expression? seed;
    private bool cached;
    private SqlValue cachedValue;

    public Rand(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.seed = Parse(context);
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "T-SQL RAND is a non-cryptographic pseudo-random source; the simulator faithfully implements the same documented contract.")]
    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.cached)
            return this.cachedValue;

        SqlValue result;
        if (this.seed is null)
        {
            // System.Random.Shared is process-shared; the first call here
            // picks one value and every later row reuses it for THIS Rand
            // instance, which is what real SQL Server does for an unseeded
            // RAND.
            result = SqlValue.FromDouble(Random.Shared.NextDouble());
        }
        else
        {
            var seedValue = this.seed.Run(runtime);
            if (seedValue.IsNull)
            {
                result = SqlValue.Null(SqlType.Float);
            }
            else
            {
                // Coerce to float — SQL Server accepts any integer / decimal /
                // float / string-convertible-to-float; CoerceTo handles the
                // category mapping. The .NET Random constructor takes int
                // only, so the seed double is hashed into int range (the
                // numeric value isn't byte-identical to real SQL Server's
                // seed algorithm, but determinism per seed is preserved).
                var asDouble = seedValue.CoerceTo(SqlType.Float).AsDouble;
                // XOR-fold the 64 bits down to 32 — straight cast-to-int
                // drops the high half, so small integer seeds like 1 and
                // 999999 (whose mantissas live entirely in the high bits)
                // collapse to the same 0 hash. Folding mixes both halves
                // into the int Random expects.
                var bits = BitConverter.DoubleToInt64Bits(asDouble);
                var seedInt = unchecked((int)(bits ^ (bits >> 32)));
                result = SqlValue.FromDouble(new Random(seedInt).NextDouble());
            }
        }

        this.cachedValue = result;
        this.cached = true;
        return result;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => this.seed is null
        ? "RAND()"
        : $"RAND({this.seed.DebugDisplay()})";
}
