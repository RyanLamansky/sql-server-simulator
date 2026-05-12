using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One user-defined scalar function. Created via <c>CREATE FUNCTION
/// schema.name(@params) RETURNS &lt;type&gt; AS BEGIN ... END</c>, dropped via
/// <c>DROP FUNCTION</c>, and called as a scalar expression. The function lives
/// in its owning <see cref="Schema"/>'s <see cref="Schema.Functions"/> dict;
/// resolution at the call site is 2-part-name-required (bare <c>fn(x)</c>
/// raises Msg 195, matching real SQL Server).
/// </summary>
/// <remarks>
/// <para>
/// The body source is captured at CREATE time as a raw <see cref="string"/>
/// (the text between the outer <c>BEGIN</c> and matching <c>END</c>) and
/// re-tokenized + re-dispatched on every invocation. Re-tokenization cost is
/// negligible at the simulator's scale; the token-snapshot path is a perf
/// optimization for later if a real workload needs it.
/// </para>
/// <para>
/// Per-call execution allocates a fresh <see cref="BatchContext"/> via
/// <c>Simulation.InvokeScalarFunction</c>; the child batch's
/// <see cref="BatchContext.Variables"/> are seeded from the call's argument
/// values, the <see cref="BatchContext.UdfFrame"/> is set so value-form
/// <c>RETURN &lt;expr&gt;</c> is legal and lands the return value, and the
/// connection's UDF recursion counter is incremented (Msg 217 when it would
/// exceed 32, matching probe-confirmed behavior).
/// </para>
/// </remarks>
internal sealed class UserDefinedFunction(
    Schema schema,
    string name,
    int objectId,
    UdfParameter[] parameters,
    SqlType returnType,
    bool returnsNullOnNullInput,
    string bodyText,
    DateTime createDate)
{
    public readonly Schema Schema = schema;
    public readonly string Name = name;
    public readonly int ObjectId = objectId;

    /// <summary>
    /// Declared parameters in source order. Each parameter has a name (with
    /// the leading <c>@</c> stripped), a declared <see cref="SqlType"/>, and
    /// an optional default expression that takes effect when the caller passes
    /// the <c>DEFAULT</c> keyword (probe-confirmed: bare omission raises
    /// Msg 313 — the <c>DEFAULT</c> keyword is required at the call site).
    /// </summary>
    public readonly UdfParameter[] Parameters = parameters;

    public readonly SqlType ReturnType = returnType;

    /// <summary>
    /// True when the function was declared with
    /// <c>WITH RETURNS NULL ON NULL INPUT</c>. At call time, if any argument
    /// is NULL the body is skipped entirely and the function returns
    /// <see cref="SqlValue.Null"/> of <see cref="ReturnType"/>.
    /// </summary>
    public readonly bool ReturnsNullOnNullInput = returnsNullOnNullInput;

    /// <summary>
    /// Raw source text of the body between <c>BEGIN</c> and <c>END</c>
    /// (exclusive of both). Re-tokenized and re-dispatched per call.
    /// </summary>
    public readonly string BodyText = bodyText;

    public readonly DateTime CreateDate = createDate;
}

/// <summary>
/// One declared parameter on a <see cref="UserDefinedFunction"/>. The
/// <see cref="Name"/> is stored with the leading <c>@</c> stripped (matching
/// the <see cref="BatchContext.Variables"/> keying convention).
/// </summary>
internal sealed class UdfParameter(string name, SqlType type, Expression? defaultExpression)
{
    public readonly string Name = name;
    public readonly SqlType Type = type;

    /// <summary>
    /// The <c>= expr</c> default, parsed once at CREATE FUNCTION time and
    /// evaluated in the per-call <see cref="BatchContext"/> when the caller
    /// passes the <c>DEFAULT</c> keyword. <see langword="null"/> when no
    /// default was declared — calls must supply the argument or raise
    /// Msg 313 (probe-confirmed).
    /// </summary>
    public readonly Expression? Default = defaultExpression;
}
