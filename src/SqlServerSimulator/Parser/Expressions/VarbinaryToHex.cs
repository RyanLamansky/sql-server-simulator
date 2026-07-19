using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The system scalar functions <c>sys.fn_varbintohexsubstring</c> and
/// <c>sys.fn_varbintohexstr</c> (the latter also reachable as
/// <c>master.dbo.fn_varbintohexstr</c>), which format a <c>varbinary</c>
/// value as a lowercase hex string. SMO scripts binary defaults and login
/// SIDs through them. Both are sys-schema-qualified system functions — an
/// unqualified <c>fn_varbintohexstr(…)</c> raises Msg 195, and the current
/// database's <c>dbo.fn_varbintohexstr</c> raises Msg 4121 (probe-confirmed
/// against SQL Server 2025); only the sys / master.dbo forms resolve.
/// </summary>
/// <remarks>
/// <para><c>fn_varbintohexsubstring(@fFullLength bit, @value varbinary(max),
/// @start int, @length int)</c> returns <c>nvarchar(max)</c>. Probe-confirmed
/// semantics:</para>
/// <list type="bullet">
/// <item><description>Any NULL argument → NULL.</description></item>
/// <item><description><c>@fFullLength</c> non-zero prefixes the result with
/// <c>0x</c>; zero omits the prefix.</description></item>
/// <item><description><c>@start</c> is a 1-based byte offset. <c>@start &lt; 1</c>
/// or <c>@start &gt;</c> the byte count → NULL (so a start of 1 on an empty
/// value → NULL).</description></item>
/// <item><description><c>@length &lt;= 0</c> means "to the end"; a positive
/// length clamps to the bytes remaining from <c>@start</c>.</description></item>
/// <item><description>Hex digits are lowercase.</description></item>
/// </list>
/// <para><c>fn_varbintohexstr(@value varbinary(max))</c> is exactly
/// <c>fn_varbintohexsubstring(1, @value, 1, 0)</c>.</para>
/// </remarks>
internal sealed class VarbinaryToHex : Expression
{
    private readonly Expression? fullLength;
    private readonly Expression value;
    private readonly Expression? start;
    private readonly Expression? length;

    private VarbinaryToHex(Expression? fullLength, Expression value, Expression? start, Expression? length)
    {
        this.fullLength = fullLength;
        this.value = value;
        this.start = start;
        this.length = length;
    }

    /// <summary>
    /// Resolves a system-qualified <c>fn_varbintohexsubstring</c> /
    /// <c>fn_varbintohexstr</c> call. Returns <see langword="null"/> when the
    /// name is not one of these system functions (letting the caller fall
    /// through to user-defined-function resolution). Assumes the parser cursor
    /// sits on the first argument token (past the opening parenthesis).
    /// </summary>
    public static VarbinaryToHex? TryResolve(MultiPartName name, ParserContext context) =>
        !QualifierIsSystem(name) ? null
        : BuiltInToken.Equals(name.Leaf, "fn_varbintohexsubstring") ? ParseSubstring(context)
        : BuiltInToken.Equals(name.Leaf, "fn_varbintohexstr") ? new VarbinaryToHex(null, Parse(context), null, null)
        : null;

    private static VarbinaryToHex ParseSubstring(ParserContext context)
    {
        var fullLength = Parse(context);
        var value = ParseAfterComma(context);
        var start = ParseAfterComma(context);
        var length = ParseAfterComma(context);
        return new VarbinaryToHex(fullLength, value, start, length);
    }

    private static Expression ParseAfterComma(ParserContext context) =>
        context.Token is Tokens.Operator { Character: ',' }
            ? Parse(context.MoveNextRequiredReturnSelf())
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    // sys.fn_… resolves in any database's sys schema; the two-argument
    // fn_varbintohexstr additionally resolves as master.dbo.fn_varbintohexstr.
    // The current database's own dbo form does not resolve (Msg 4121), so
    // "dbo" is only accepted under an explicit master qualifier.
    private static bool QualifierIsSystem(MultiPartName name) => name.Count switch
    {
        2 => BuiltInToken.Equals(name[0], "sys"),
        3 => BuiltInToken.Equals(name[1], "sys")
            || (BuiltInToken.Equals(name[0], "master") && BuiltInToken.Equals(name[1], "dbo")),
        _ => false,
    };

    public override SqlValue Run(RuntimeContext runtime)
    {
        var valueRaw = this.value.Run(runtime);
        if (valueRaw.IsNull)
            return SqlValue.Null(SqlType.NVarcharMax);

        var bytes = valueRaw.CoerceTo(SqlType.VarbinaryMax).AsBytes;

        bool prefix;
        int startOffset;
        int requestedLength;
        if (this.fullLength is null)
        {
            // fn_varbintohexstr(value) == fn_varbintohexsubstring(1, value, 1, 0).
            prefix = true;
            startOffset = 1;
            requestedLength = 0;
        }
        else
        {
            var fullLengthValue = this.fullLength.Run(runtime);
            var startValue = this.start!.Run(runtime);
            var lengthValue = this.length!.Run(runtime);
            if (fullLengthValue.IsNull || startValue.IsNull || lengthValue.IsNull)
                return SqlValue.Null(SqlType.NVarcharMax);
            prefix = fullLengthValue.CoerceTo(SqlType.Int32).AsInt32 != 0;
            startOffset = startValue.CoerceTo(SqlType.Int32).AsInt32;
            requestedLength = lengthValue.CoerceTo(SqlType.Int32).AsInt32;
        }

        if (startOffset < 1 || startOffset > bytes.Length)
            return SqlValue.Null(SqlType.NVarcharMax);

        var remaining = bytes.Length - (startOffset - 1);
        var take = requestedLength <= 0 ? remaining : Math.Min(requestedLength, remaining);
        var hex = Convert.ToHexStringLower(bytes, startOffset - 1, take);
        return SqlValue.FromNVarchar(SqlType.NVarcharMax, prefix ? "0x" + hex : hex);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarcharMax;

    internal override string DebugDisplay() => this.fullLength is null
        ? $"fn_varbintohexstr({this.value.DebugDisplay()})"
        : $"fn_varbintohexsubstring({this.fullLength.DebugDisplay()}, {this.value.DebugDisplay()}, {this.start!.DebugDisplay()}, {this.length!.DebugDisplay()})";
}
