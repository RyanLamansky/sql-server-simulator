using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared implicit-coerce helpers for the string scalar expressions
/// (<see cref="Length"/>, <see cref="Lower"/>, <see cref="Upper"/>,
/// <see cref="LeftTrim"/>, <see cref="RightTrim"/>, <see cref="Reverse"/>,
/// <see cref="Left"/>, <see cref="Right"/>, <see cref="Replace"/>). Mirrors
/// the <see cref="MathScalars"/> pattern: centralizes the "non-string
/// operand implicitly casts to varchar" rule that real SQL Server applies
/// to every string scalar. Probe-confirmed against SQL Server 2025
/// (2026-05-22): <c>LOWER(12345)</c> / <c>LEN(CAST('2024-01-15' AS DATE))</c>
/// / <c>REPLACE(CAST('2024-01-15' AS DATE), '-', '/')</c> all parse, with
/// the result column reading as <c>varchar</c> regardless of the source
/// family (integer, decimal, float, money, date/time).
/// </summary>
internal static class StringScalars
{
    /// <summary>
    /// Returns <paramref name="value"/> typed at a string family, applying
    /// SQL Server's implicit-cast rule when the input isn't already a
    /// string. Numeric / date-time / uniqueidentifier sources flow through
    /// <see cref="SqlValue.CoerceTo"/> targeting <c>varchar</c> in the
    /// active database's collation — same path real SQL Server uses to
    /// render the value before applying the surrounding string function.
    /// Source families outside this set (varbinary, xml, spatial, table
    /// types) raise Msg 8116 via
    /// <see cref="SimulatedSqlException.InvalidArgumentDataType"/> so the
    /// caller surfaces the same error wording real SQL Server uses for
    /// genuinely-unsupported operands.
    /// </summary>
    public static SqlValue CoerceToVarchar(SqlValue value, BatchContext batch, string functionLowerName, int argumentIndex = 1, bool allowLegacyLob = false)
    {
        // The legacy LOB text types are string-category but a string function
        // refuses one, raising Msg 8116 that names the function and argument
        // (probe-confirmed 2026-07-31 across LEN / LEFT / RIGHT / UPPER /
        // LOWER / LTRIM / REVERSE / REPLACE / SUBSTRING / STUFF / CHARINDEX).
        // The exception is an argument that is searched rather than
        // transformed — CHARINDEX's haystack takes an ntext document happily —
        // which opts out via <paramref name="allowLegacyLob"/>.
        if (!allowLegacyLob && (value.Type == SqlType.Text || value.Type == SqlType.NText))
            throw SimulatedSqlException.InvalidArgumentDataType(value.Type.SqlServerName, argumentIndex, functionLowerName);
        if (SqlType.IsStringCategory(value.Type))
            return value;
        if (!IsCoerceableToVarchar(value.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(value.Type.SqlServerName, argumentIndex, functionLowerName);
        var target = VarcharSqlType.Get(0, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);
        return value.CoerceTo(target);
    }

    /// <summary>
    /// Narrows a scalar function's integer argument — a length, position,
    /// count, index or code point — to <c>int</c>. A value outside int range
    /// raises Msg 8115 the way real does, instead of leaking .NET's
    /// <see cref="OverflowException"/>. Probe-confirmed 2026-07-31 for
    /// SUBSTRING / CHARINDEX / STUFF / REPLICATE / SPACE / CHOOSE / CHAR /
    /// NCHAR alongside the LEFT / RIGHT pair that first needed it.
    /// </summary>
    public static int CoerceLengthArgument(SqlValue count)
    {
        try
        {
            return count.CoerceTo(SqlType.Int32).AsInt32;
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(SqlType.Int32.SqlServerName);
        }
    }

    /// <summary>
    /// Returns the projection-schema string type for a string scalar
    /// applied to an input typed at <paramref name="sourceType"/>. String
    /// sources pass through (the function preserves the input type);
    /// implicit-castable sources promote to <c>varchar</c> in the active
    /// database collation, matching the runtime coercion in
    /// <see cref="CoerceToVarchar"/>; everything else passes through (the
    /// runtime path raises the same Msg 8116 at that point — projection
    /// schema only needs to be roughly correct since the value never
    /// materializes).
    /// </summary>
    public static SqlType ResolveResultType(SqlType sourceType, BatchContext batch) =>
        SqlType.IsStringCategory(sourceType) || !IsCoerceableToVarchar(sourceType)
            ? sourceType
            : VarcharSqlType.Get(0, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    /// <summary>
    /// Resolves the trim-character set for the two-argument <c>LTRIM</c> /
    /// <c>RTRIM</c> forms. A missing second argument defaults to a single
    /// space (the legacy one-argument behavior). A supplied set is evaluated
    /// to its characters; a NULL argument returns <see langword="null"/> to
    /// signal a NULL result (probe-confirmed against SQL Server 2025). The
    /// characters form a set, not a substring.
    /// </summary>
    public static char[]? ResolveTrimCharacters(Expression? trimChars, RuntimeContext runtime)
    {
        if (trimChars is null)
            return [' '];
        var value = trimChars.Run(runtime);
        return value.IsNull ? null : value.AsString.ToCharArray();
    }

    private static bool IsCoerceableToVarchar(SqlType type) =>
        SqlType.IsIntegerCategory(type)
            || type is DecimalSqlType
            || SqlType.IsMoneyCategory(type)
            || SqlType.IsApproximateNumericCategory(type)
            || SqlType.IsDateTimeCategory(type)
            || type == SqlType.UniqueIdentifier
            || type is VarbinarySqlType or BinarySqlType;

    /// <summary>
    /// The family maximum width — 4000 for the national (nvarchar / nchar)
    /// family, 8000 for CP1252 (varchar / char) — used to cap length-deriving
    /// string scalars (<c>REPLICATE</c> / <c>SPACE</c> / <c>STUFF</c>) whose
    /// result can grow past the operand width.
    /// </summary>
    public static int FamilyCap(SqlType type) =>
        type is NVarcharSqlType or NCharSqlType ? 4000 : 8000;

    /// <summary>
    /// True when <paramref name="type"/> carries unbounded length (a
    /// <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c> or a <c>text</c> / <c>ntext</c>
    /// LOB), in which case a length-deriving scalar propagates MAX-ness rather
    /// than computing a bounded width.
    /// </summary>
    public static bool IsMaxForm(SqlType type) =>
        type.IsLob
            || type is VarcharSqlType { length: SqlType.MaxLengthSentinel }
            || type is NVarcharSqlType { length: SqlType.MaxLengthSentinel };

    /// <summary>
    /// Declared width of a bounded var / fixed string type, or 0 for the
    /// length-unspecified sentinel and any non-simple-string type. MAX-form
    /// callers are expected to branch on <see cref="IsMaxForm"/> first.
    /// </summary>
    public static int DeclaredWidth(SqlType type) => type switch
    {
        VarcharSqlType v when v.length > 0 => v.length,
        NVarcharSqlType nv when nv.length > 0 => nv.length,
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length,
        _ => 0,
    };

    /// <summary>
    /// Extracts a compile-time-constant non-negative <see cref="int"/> from an
    /// integer / decimal / money numeric literal argument (a bare
    /// <see cref="Value"/> node) so length-deriving scalars can compute their
    /// projected width the way SQL Server does when the count / length is a
    /// literal. Returns <see langword="false"/> for a non-constant, non-numeric,
    /// NULL, or negative operand — the caller falls back to a family-width
    /// (container) result, matching real SQL Server's non-constant behavior.
    /// </summary>
    public static bool TryConstantCount(Expression expression, out int value)
    {
        value = 0;
        if (expression is not Value { Constant: { IsNull: false } constant })
            return false;
        var t = constant.Type;
        if (!(SqlType.IsIntegerCategory(t) || t is DecimalSqlType || SqlType.IsMoneyCategory(t)))
            return false;
        try
        {
            var i = constant.CoerceTo(SqlType.Int32).AsInt32;
            if (i < 0)
                return false;
            value = i;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// The string comparison a searching scalar (<see cref="Replace"/>,
    /// <see cref="CharIndex"/>) should use, given its operands. SQL Server
    /// compares under the collation its arguments resolve to, so an explicit
    /// <c>COLLATE</c> on <em>any</em> argument decides the whole call —
    /// `REPLACE(name, 'r. r.', '' COLLATE …_CS_AS)` leaves a differently-cased
    /// match alone (probe-confirmed, and the shape ORMs emit to force a
    /// case-sensitive replace on a case-insensitive database).
    /// </summary>
    /// <remarks>
    /// Case sensitivity is the whole of the approximation: the comparison stays
    /// culture-based rather than routing through the collation's own comparer,
    /// which is what the surrounding string scalars already do.
    /// <para>Allocation-free per call: the operand list is a
    /// <c>params ReadOnlySpan</c> rather than an array, and the fold carries a
    /// <c>(Collation, Coercibility)</c> accumulator through
    /// <c>Collation.Resolve(Collation, Coercibility, SqlType)</c> rather
    /// than synthesizing a throwaway <see cref="SqlType"/> per step — both
    /// would otherwise cost per row on a string-scalar hot path.</para>
    /// </remarks>
    public static StringComparison ComparisonFor(BatchContext batch, params ReadOnlySpan<SqlType> operands)
    {
        var resolved = operands.Length == 0 ? null : operands[0].Collation;
        var coercibility = operands.Length == 0 ? Coercibility.CoercibleDefault : operands[0].Coercibility;
        for (var i = 1; i < operands.Length; i++)
        {
            if (Collation.Resolve(resolved ?? Collation.Baseline, coercibility, operands[i]) is not { } step)
            {
                // Unresolvable peer collations are Msg 468 territory at a
                // comparison site; this scalar keeps the database default
                // rather than raising from a code path that never has.
                resolved = null;
                break;
            }

            (resolved, coercibility) = (step.Collation, step.Coercibility);
        }

        return (resolved ?? batch.CurrentDatabase.Collation).CaseSensitive
            ? StringComparison.InvariantCulture
            : StringComparison.InvariantCultureIgnoreCase;
    }

    /// <summary>
    /// The length-0 (unspecified) form of the same variable-length string
    /// family as <paramref name="sourceType"/>, preserving its collation and
    /// coercibility. Renders as the family container width (varchar(8000) /
    /// nvarchar(4000)) over the wire — the result type SQL Server assigns to
    /// growable scalars (<c>REPLACE</c> / <c>TRANSLATE</c>) that don't compute
    /// a tighter bound. Fixed-length (char / nchar) sources drop to the
    /// variable form since the result length varies.
    /// </summary>
    public static SqlType ContainerResultType(SqlType sourceType, BatchContext batch)
    {
        var collation = sourceType.Collation ?? batch.CurrentDatabase.Collation;
        var coercibility = sourceType.Coercibility;
        return sourceType is NVarcharSqlType or NCharSqlType || sourceType == SqlType.NText
            ? NVarcharSqlType.Get(0, collation, coercibility)
            : VarcharSqlType.Get(0, collation, coercibility);
    }

    /// <summary>
    /// A bounded var* result type of <paramref name="width"/> characters in the
    /// same family / collation as <paramref name="sourceType"/>, for a
    /// length-deriving scalar whose projected width is known
    /// (<c>REPLICATE</c> / <c>SPACE</c> / <c>STUFF</c> with a constant count).
    /// A width of 0 floors to 1 — SQL Server has no zero-width string type, so
    /// <c>SPACE(0)</c> / <c>LEFT(x, 0)</c> project as <c>varchar(1)</c>
    /// (probe-confirmed).
    /// </summary>
    public static SqlType SizedResultType(SqlType sourceType, int width, BatchContext batch)
    {
        var collation = sourceType.Collation ?? batch.CurrentDatabase.Collation;
        var coercibility = sourceType.Coercibility;
        var national = sourceType is NVarcharSqlType or NCharSqlType || sourceType == SqlType.NText;
        var bounded = Math.Max(1, Math.Min(national ? 4000 : 8000, width));
        return national
            ? NVarcharSqlType.Get(bounded, collation, coercibility)
            : VarcharSqlType.Get(bounded, collation, coercibility);
    }
}
