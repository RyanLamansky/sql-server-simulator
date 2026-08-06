using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SUBSTRING(x, start, length)</c>: 1-indexed substring extraction over
/// a character source or a <c>varbinary</c> / <c>binary</c> / <c>image</c> one,
/// which slices bytes and projects the <c>varbinary</c> family. Per SQL Server
/// semantics:
/// <list type="bullet">
/// <item><description><c>start</c> &lt;= 0 still produces output, but the
/// effective length is reduced by the negative offset.</description></item>
/// <item><description><c>start + length</c> past the end clamps to the
/// available remainder.</description></item>
/// <item><description>Negative <c>length</c> is an error.</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/substring-transact-sql</remarks>
internal sealed class Substring : Expression
{
    private readonly Expression source;
    private readonly Expression start;
    private readonly Expression length;

    public Substring(ParserContext context)
    {
        this.source = Parse(context);
        ExpectArgumentSeparator(context);
        this.start = Parse(context.MoveNextRequiredReturnSelf());
        ExpectArgumentSeparator(context);
        this.length = Parse(context.MoveNextRequiredReturnSelf());
    }

    // A comma separates SUBSTRING's arguments; the ANSI `SUBSTRING(x FROM a
    // FOR b)` form isn't T-SQL, so a reserved keyword here (FROM / FOR) is
    // rejected with Msg 156 (probe-confirmed against SQL Server 2025), other
    // tokens with the generic Msg 102.
    private static void ExpectArgumentSeparator(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ',' })
            return;
        throw context.Token is Tokens.ReservedKeyword keyword
            ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
            : SimulatedSqlException.SyntaxErrorNear(context);
    }

    internal override bool ParallelSafe => this.source.ParallelSafe && this.start.ParallelSafe && this.length.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var s = source.Run(runtime);
        var startValue = start.Run(runtime);
        var lengthValue = length.Run(runtime);
        var resultType = ResolveResultType(s.Type, runtime.Batch);
        if (s.IsNull || startValue.IsNull || lengthValue.IsNull)
            return SqlValue.Null(resultType);
        var isBinarySource = IsBinarySource(s.Type);
        if (!isBinarySource && !SqlType.IsStringCategory(s.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(s.Type.SqlServerName, argumentIndex: 1, "substring");

        var startIndex = StringScalars.CoerceLengthArgument(startValue);
        var len = StringScalars.CoerceLengthArgument(lengthValue);
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowedAtRuntime(isRight: false);

        if (isBinarySource)
        {
            var bytes = s.AsBytes;
            var (byteStart, byteLength) = Window(startIndex, len, bytes.Length);
            return SqlValue.FromVarbinary(ResolveBinaryResultType(s.Type), bytes.AsSpan(byteStart, byteLength).ToArray());
        }

        var input = s.AsString;
        var isSc = s.Type.Collation?.IsSupplementaryCharacterAware == true;
        var inputUnits = isSc ? SupplementaryCharacters.CodepointCount(input) : input.Length;
        var (sliceStart, sliceLength) = Window(startIndex, len, inputUnits);
        if (!isSc)
            return SqlValue.FromString(resultType, input.Substring(sliceStart, sliceLength));
        var startCu = SupplementaryCharacters.CodepointToCodeUnit(input, sliceStart);
        var endCu = SupplementaryCharacters.CodepointToCodeUnit(input, sliceStart + sliceLength);
        return SqlValue.FromString(resultType, input[startCu..endCu]);
    }

    /// <summary>
    /// The clamped <c>(start, length)</c> window over an input of
    /// <paramref name="inputUnits"/> units — characters for a string source,
    /// bytes for a binary one, the arithmetic being identical either way. If
    /// <c>start</c> &lt;= 0 the leading <c>|start - 1|</c> units of the
    /// requested window fall before the input and are dropped; the tail clamps
    /// to what remains. The math runs in <see cref="long"/> so
    /// <c>int.MinValue</c> / <c>int.MaxValue</c> arguments can't overflow —
    /// SQL Server clamps that pair to an empty result rather than erroring.
    /// </summary>
    private static (int Start, int Length) Window(int startIndex, int length, int inputUnits)
    {
        var zeroBased = (long)startIndex - 1;
        var start = Math.Min(Math.Max(0L, zeroBased), inputUnits);
        var count = Math.Min(Math.Max(0L, length + Math.Min(0L, zeroBased)), inputUnits - start);
        return ((int)start, (int)count);
    }

    private static bool IsBinarySource(SqlType type) => type is VarbinarySqlType or BinarySqlType or ImageSqlType;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(source.GetSqlType(batch, resolveColumnType), batch);

    /// <summary>
    /// SUBSTRING preserves the input's string family; a constant length
    /// argument tightens the projected width to <c>min(inputWidth, length)</c>
    /// (probe-confirmed: <c>SUBSTRING(varchar(10), 2, 3)</c> → <c>varchar(3)</c>,
    /// <c>SUBSTRING(varchar(10), 5, 20)</c> → <c>varchar(10)</c> — start does
    /// not affect the width). A MAX input, a non-constant length, or an
    /// unspecified input width leaves the width at the input's. A binary source
    /// projects the <c>varbinary</c> family under the same rule, and a
    /// <c>text</c> / <c>ntext</c> / <c>image</c> source narrows to the bounded
    /// var* family a constant length names — <c>SUBSTRING(&lt;image&gt;, 2, 3)</c>
    /// is <c>varbinary(3)</c> and a non-constant length is the family container
    /// (<c>varbinary(8000)</c>), both probe-confirmed.
    /// </summary>
    private SqlType ResolveResultType(SqlType sourceType, BatchContext batch)
    {
        if (IsBinarySource(sourceType))
            return ResolveBinaryResultType(sourceType);
        StringScalars.RequireSettledCollation(sourceType, "substring");
        if (!SqlType.IsStringCategory(sourceType))
            return sourceType;
        if (StringScalars.IsConstantNegativeCount(length))
            throw SimulatedSqlException.NegativeLengthNotAllowed("substring", 8);
        if (sourceType is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel })
            return sourceType;
        var hasConstantLength = StringScalars.TryConstantCount(length, out var n);
        if (sourceType.IsLob)
        {
            return hasConstantLength
                ? StringScalars.SizedResultType(sourceType, n, batch)
                : StringScalars.ContainerResultType(sourceType, batch);
        }

        var inputWidth = StringScalars.DeclaredWidth(sourceType);
        return inputWidth > 0 && hasConstantLength
            ? StringScalars.SizedResultType(sourceType, Math.Min(inputWidth, n), batch)
            : sourceType;
    }

    /// <summary>
    /// The <c>varbinary</c> result a binary source projects. A
    /// <c>varbinary(max)</c> source stays MAX whatever the length argument;
    /// every other source caps at its own declared width (8000 for
    /// <c>image</c>, which has none), and a constant length narrows below that.
    /// Width 0 floors to 1 — SQL Server has no zero-width binary type.
    /// </summary>
    private VarbinarySqlType ResolveBinaryResultType(SqlType sourceType)
    {
        if (StringScalars.IsConstantNegativeCount(length))
            throw SimulatedSqlException.NegativeLengthNotAllowed("substring", 8);
        if (sourceType is VarbinarySqlType { length: SqlType.MaxLengthSentinel })
            return VarbinarySqlType.MaxForm;
        var sourceWidth = sourceType switch
        {
            VarbinarySqlType { length: > 0 and var w } => w,
            BinarySqlType b => b.length,
            _ => 8000,
        };
        return StringScalars.TryConstantCount(length, out var n)
            ? VarbinarySqlType.Get(Math.Max(1, Math.Min(sourceWidth, n)))
            : VarbinarySqlType.Get(sourceWidth);
    }

    internal override string DebugDisplay() => $"SUBSTRING({source.DebugDisplay()}, {start.DebugDisplay()}, {length.DebugDisplay()})";
}
