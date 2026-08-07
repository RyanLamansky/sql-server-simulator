using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// Whether a computed column's expression is <em>precise</em> — real's second
/// precondition (after determinism) for keying an index or statistics on a
/// non-persisted computed column, refused with Msg 2799 otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The rule is "no <c>float</c> or <c>real</c> anywhere in the expression",
/// which reaches further than the column's own resolved type: real calls
/// <c>CAST(f AS int)</c>, <c>CAST(SQRT(i) AS int)</c> and
/// <c>CONVERT(int, CONVERT(float, i))</c> all imprecise even though each lands
/// on <c>int</c> (probe-confirmed against SQL Server 2025).
/// So the walk looks for an approximate type in five places: the column's own
/// type, a referenced column's type, an explicit <c>float</c> / <c>real</c>
/// conversion target, a built-in whose result is <c>float</c> whatever its
/// arguments, and a scientific-notation literal — the last three being what
/// <c>CAST(SQRT(i) AS int)</c> and <c>i + CAST(1.5e0 AS int)</c> turn on, since
/// neither leaves anything approximate in the column's own type.
/// </para>
/// <para>
/// Token-level like <see cref="ModuleDeterminism"/>'s own walk, and for the
/// same reason: the parsed expression tree exposes no generic child visitor, so
/// the definition text is what every whole-expression question is asked of.
/// </para>
/// </remarks>
internal static class ComputedColumnPrecision
{
    /// <summary>
    /// Built-ins whose result is <c>float</c> whatever their arguments, so a
    /// call to one makes its expression imprecise even under a narrowing
    /// conversion. Taken from the simulator's own catalog — every expression
    /// class whose <c>GetSqlType</c> is unconditionally <see cref="SqlType.Float"/>.
    /// The neighbours that instead follow their argument's type
    /// (<c>POWER</c>, <c>DEGREES</c>, <c>RADIANS</c>, <c>ABS</c>) are absent:
    /// a float argument to one of those is already an approximate column or a
    /// nested call, which the same walk sees.
    /// </summary>
    private static readonly FrozenSet<string> ApproximateResultBuiltIns = new[]
    {
        "ACOS",
        "ASIN",
        "ATAN",
        "ATN2",
        "COS",
        "COT",
        "EXP",
        "LOG",
        "LOG10",
        "PI",
        "RAND",
        "SIN",
        "SQRT",
        "SQUARE",
        "TAN",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="definition"/> — the computed column's parenthesized
    /// source text, resolving to <paramref name="resolvedType"/> over
    /// <paramref name="scopeColumns"/> — is free of <c>float</c> / <c>real</c>.
    /// A definition the tokenizer can't get through answers imprecise, the
    /// refusing direction.
    /// </summary>
    internal static bool IsPrecise(HeapColumn[] scopeColumns, SqlType resolvedType, string definition)
    {
        if (IsApproximate(resolvedType))
            return false;

        List<Token> tokens;
        try
        {
            tokens = [];
            var index = 0;
            while (Tokenizer.NextToken(definition, ref index, Collation.Baseline) is { } token)
            {
                if (token is not (Whitespace or Comment))
                    tokens.Add(token);
            }
        }
        catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
        {
            return false;
        }

        var approximateBuiltIns = ApproximateResultBuiltIns.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var token in tokens)
        {
            // A scientific-notation literal is a float in its own right.
            if (token is Numeric numeric && IsApproximate(numeric.Value.Type))
                return false;
            if (token is not Name name)
                continue;

            // An explicit conversion target or a float-returning call, matched
            // on the *undelimited* spelling only: a column delimited `[float]`
            // arrives as a DelimitedIdentifier and reaches the column walk
            // below instead, where its declared type answers.
            if (token is UnquotedString
                && (name.Span.Equals("float", StringComparison.OrdinalIgnoreCase)
                    || name.Span.Equals("real", StringComparison.OrdinalIgnoreCase)
                    || approximateBuiltIns.Contains(name.Span)))
            {
                return false;
            }

            foreach (var column in scopeColumns)
            {
                if (Collation.Baseline.Equals(column.Name, name.Value) && IsApproximate(column.Type))
                    return false;
            }
        }

        return true;
    }

    private static bool IsApproximate(SqlType type) => type == SqlType.Float || type == SqlType.Real;
}
