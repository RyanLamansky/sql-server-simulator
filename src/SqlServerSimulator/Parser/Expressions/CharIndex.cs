using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CHARINDEX(needle, haystack [, start])</c>: 1-indexed position of
/// the first occurrence of <c>needle</c> in <c>haystack</c> at or after
/// <c>start</c>; returns 0 when not found. The search runs under the collation
/// the arguments resolve to — case, accent, kanatype and width all folding as
/// the name declares — through <c>Collation.IndexOf</c>.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/charindex-transact-sql</remarks>
internal sealed class CharIndex : Expression
{
    private readonly Expression needle;
    private readonly Expression haystack;
    private readonly Expression? start;

    // A binary haystack's value doesn't carry its declared MAX-ness, so the
    // result type the binding settled is what the run reports.
    private SqlType? binaryResultType;

    public CharIndex(ParserContext context)
    {
        this.needle = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.haystack = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.start = Parse(context.MoveNextRequiredReturnSelf());
    }

    internal override bool ParallelSafe => this.needle.ParallelSafe && this.haystack.ParallelSafe && this.start?.ParallelSafe != false;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var n = needle.Run(runtime);
        StringScalars.RejectLegacyLob(n, "charindex");
        if (n.Type is BinarySqlType or VarbinarySqlType)
            return this.RunBinary(n, runtime);
        // CHARINDEX's haystack (arg 2) implicit-coerces to varchar per real
        // (probe-confirmed 2026-05-22: CHARINDEX('2', 12345) = 2). Needle
        // (arg 1) stays strict — real rejects non-string with Msg 8116.
        var h = StringScalars.CoerceToVarchar(haystack.Run(runtime), runtime.Batch, "charindex", argumentIndex: 2, allowLegacyLob: true);
        var resultType = ResultType(h.Type);
        if (n.IsNull || h.IsNull)
            return SqlValue.Null(resultType);
        if (!SqlType.IsStringCategory(n.Type) || n.Type == SqlType.Text || n.Type == SqlType.NText)
            throw SimulatedSqlException.InvalidArgumentDataType(n.Type.SqlServerName, argumentIndex: 1, "charindex");

        var collation = StringScalars.CollationFor(runtime.Batch, h.Type, n.Type);
        var needleStr = StringScalars.TextUnder(n, collation);
        var haystackStr = StringScalars.TextUnder(h, collation);
        // CHARINDEX indexes in code units under non-SC collations and in
        // codepoints under _SC_. Probe-confirmed against SQL Server 2025:
        // CHARINDEX(N'X', N'😀X') = 3 under non-SC (surrogate pair occupies
        // positions 1-2) and = 2 under _SC_UTF8 (emoji = position 1). The
        // start argument is in the same unit as the result.
        var isSc = h.Type.Collation?.IsSupplementaryCharacterAware == true;
        var startUnits = 0;
        if (start is not null)
        {
            var startValue = start.Run(runtime);
            if (startValue.IsNull)
                return SqlValue.Null(resultType);
            startUnits = Math.Max(0, StringScalars.CoerceLengthArgument(startValue) - 1);
        }
        var startCu = isSc
            ? SupplementaryCharacters.CodepointToCodeUnit(haystackStr, startUnits)
            : startUnits;
        var foundCu = startCu >= haystackStr.Length
            ? -1
            : collation.IndexOf(haystackStr, needleStr, startCu, out _);
        var position = foundCu < 0
            ? 0
            : isSc
                ? SupplementaryCharacters.CodeUnitToCodepoint(haystackStr, foundCu) + 1
                : foundCu + 1;
        return resultType == SqlType.BigInt ? SqlValue.FromInt64(position) : SqlValue.FromInt32(position);
    }

    /// <summary>
    /// A binary needle searches the haystack's bytes — a <c>binary(n)</c>
    /// one's padding included — returning the 1-based byte position (probed
    /// 2026-10-02 against SQL Server 2025: <c>CHARINDEX(0x02, 0x010203)</c> is 2,
    /// and EF Core translates a <c>byte[].Contains</c> to it).
    /// </summary>
    private SqlValue RunBinary(SqlValue n, RuntimeContext runtime)
    {
        var h = haystack.Run(runtime);
        if (h.Type is not (BinarySqlType or VarbinarySqlType))
            h = h.CoerceTo(SqlType.VarbinaryMax);
        var resultType = this.binaryResultType ?? ResultType(h.Type);
        if (n.IsNull || h.IsNull)
            return SqlValue.Null(resultType);
        var startBytes = 0;
        if (start is not null)
        {
            var startValue = start.Run(runtime);
            if (startValue.IsNull)
                return SqlValue.Null(resultType);
            startBytes = Math.Max(0, StringScalars.CoerceLengthArgument(startValue) - 1);
        }
        var needleBytes = n.AsBytes;
        var haystackBytes = h.AsBytes;
        var found = needleBytes.Length == 0 || startBytes >= haystackBytes.Length
            ? -1
            : haystackBytes.AsSpan(startBytes).IndexOf(needleBytes);
        var position = found < 0 ? 0 : startBytes + found + 1;
        return resultType == SqlType.BigInt ? SqlValue.FromInt64(position) : SqlValue.FromInt32(position);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var needleType = StringScalars.RequireStringArgument(needle, StringScalars.BindArgument(needle, batch, resolveColumnType, "charindex"), "charindex", 1, acceptsBinary: true);
        // The haystack is searched rather than transformed, so it takes no
        // legacy-LOB rejection — but the search still needs a definite
        // collation, so an unresolved one reports from either operand.
        var haystackType = haystack.GetSqlType(batch, resolveColumnType);
        // A binary needle searches bytes, and a string haystack has no
        // implicit conversion to them (probed 2026-09-26 against SQL Server
        // 2025: Msg 257 naming the haystack's type).
        if (needleType is BinarySqlType or VarbinarySqlType && SqlType.IsStringCategory(haystackType))
            throw SimulatedSqlException.ImplicitConversionNotAllowed(SimulatedSqlException.FamilyRootName(haystackType), "varbinary");
        if (needleType is BinarySqlType or VarbinarySqlType)
            return this.binaryResultType = ResultType(haystackType);
        StringScalars.RejectLegacyLobInCoercion(haystackType, "charindex", argumentIndex: 2, allowLegacyLob: true);
        StringScalars.RequireSettledCollation(haystackType, "charindex");
        StringScalars.RequireResolvableCollations("charindex", needleType, haystackType);
        if (start is not null)
            ScalarArguments.RequireNumericSlot(start, batch, resolveColumnType, "charindex", 3, NumericSlot.IntegerOrDecimal);
        return ResultType(haystackType);
    }

    /// <summary>
    /// <c>bigint</c> over a <c>varchar(max)</c> / <c>nvarchar(max)</c> /
    /// <c>varbinary(max)</c> haystack, <c>int</c> otherwise — a <c>text</c> / <c>ntext</c> one
    /// included, and whatever the needle (probed 2026-10-01 against SQL
    /// Server 2025).
    /// </summary>
    private static SqlType ResultType(SqlType haystackType) =>
        haystackType is VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel }
            ? SqlType.BigInt
            : SqlType.Int32;

    internal override string DebugDisplay() => start is null
        ? $"CHARINDEX({needle.DebugDisplay()}, {haystack.DebugDisplay()})"
        : $"CHARINDEX({needle.DebugDisplay()}, {haystack.DebugDisplay()}, {start.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.needle).Child(this.haystack).Child(this.start);
}
